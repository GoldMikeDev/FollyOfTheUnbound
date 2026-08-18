param
(
    [Parameter(Position = 0)][string]$action,
    [string]$config,  # name-only, not positional: $action above already claimed Position 0, so [config] falls through to $remainingArgs and gets matched by the manual parse below instead
    [parameter(ValueFromRemainingArguments = $true)][string[]]$remainingArgs
)
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    try { [Console]::CursorVisible = $false } catch {}
    $ErrorActionPreference = "Stop"
    $solution = "FollyOfTheUnbound.slnx"
    $buildScript = Join-Path $PSScriptRoot "eng\build.ps1"
    $nupkgRoot = Join-Path $PSScriptRoot "..\.nupkg\FotU"
    $core = $false  # PowerShell's automatic binding only recognises single-dash switches, so --core/--desktop/--timeout (matching folly.sh's style) are parsed by hand below
    $desktop = $false
    $testTimeout = 0
    $expectTimeoutValue = $false
    foreach ($arg in $remainingArgs) {
        if ($expectTimeoutValue) {
            if (-not [int]::TryParse($arg, [ref]$testTimeout) -or $testTimeout -le 0 -or $testTimeout -gt 71582) {  # 71582 min = Task.Delay's ms ceiling, which Program.RunCoreAsync forwards this straight into
                Write-Host "'--timeout' requires a positive integer minute count, up to 71582 (Task.Delay's supported maximum), got '$arg'." -ForegroundColor Red
                exit 1
            }
            $expectTimeoutValue = $false
        }
        elseif ($arg -eq "--core") {
            $core = $true
        }
        elseif ($arg -eq "--desktop") {
            $desktop = $true
        }
        elseif ($arg -eq "--timeout") {
            $expectTimeoutValue = $true
        }
        elseif ([string]::IsNullOrEmpty($config)) {
            $config = $arg
        }
        else {
            Write-Host "Unrecognised argument '$arg'." -ForegroundColor Red
            exit 1
        }
    }
    if ($expectTimeoutValue) {
        Write-Host "'--timeout' requires a minute count argument." -ForegroundColor Red
        exit 1
    }
    if ([string]::IsNullOrEmpty($action) -or $action -eq "grimoire") {
        Write-Host "folly.ps1 <action> [config] [switches]"
        Write-Host ""
        Write-Host "Actions (positional, or named as -action <action>):"
        Write-Host "  attune    Restore only [config]"
        Write-Host "  weave     Restore + build [config]"
        Write-Host "  reweave   Restore + rebuild [config]"
        Write-Host "  bind      Restore + build + pack [config] (copies .nupkg output to ../.nupkg/FotU)"
        Write-Host "  scry      Restore + build + run Core and Framework unit tests [config]"
        Write-Host "  cleanse   Delete artefacts (ignores config)"
        Write-Host "  grimoire  Show this text (default when no action is given; ignores config)"
        Write-Host ""
        Write-Host "[config] (optional, positional, or named as -config <config>; defaults to Research):"
        Write-Host "  research  Debug"
        Write-Host "  truth     Release"
        Write-Host ""
        Write-Host "scry-only switches (not positional -- always passed by name, after [config]):"
        Write-Host "  --core               Run only the Core tests (skip Framework)"
        Write-Host "  --desktop            Run only the Framework tests (skip Core)"
        Write-Host "                       (omit both to run both, the default)"
        Write-Host "  --timeout <minutes>  Override RunTests' whole-run watchdog (default: 90)"
        Write-Host ""
        Write-Host "Example: folly.ps1 scry truth --core --timeout 180"
        Write-Host "Example (named): folly.ps1 -action scry -config truth --core --timeout 180"
        Write-Host ""
        exit 0
    }
    if (($core -or $desktop) -and $action -ne "scry") {
        Write-Host "'--core'/'--desktop' are only valid with the 'scry' action." -ForegroundColor Red
        exit 1
    }
    if ($testTimeout -gt 0 -and $action -ne "scry") {
        Write-Host "'--timeout' is only valid with the 'scry' action." -ForegroundColor Red
        exit 1
    }
    if ([string]::IsNullOrEmpty($config) -or $config -eq "research") {
        $configuration = "Debug"
        $nupkgDir = Join-Path $nupkgRoot "Debug"
    }
    elseif ($config -eq "truth") {
        $configuration = "Release"
        $nupkgDir = Join-Path $nupkgRoot "Release"
    }
    else {
        Write-Host "Unrecognised configuration '$config'. Expected 'Debug', 'Release', or omitted (defaults to Debug)." -ForegroundColor Red
        exit 1
    }
    if ($action -eq "attune") {  # -nodeReuse:$false everywhere below: eng/common/tools.ps1 defaults nodeReuse true locally, leaving MSBuild workers running after exit, still holding DLLs open under artifacts/ (cleanse's build-server shutdown only stops VBCSCompiler/Razor, not these)
        & $buildScript -restore -nodeReuse:$false -solution $solution -configuration $configuration
    }
    elseif ($action -eq "weave") {
        & $buildScript -restore -build -nodeReuse:$false -solution $solution -configuration $configuration
    }
    elseif ($action -eq "reweave") {
        & $buildScript -restore -rebuild -nodeReuse:$false -solution $solution -configuration $configuration
    }
    elseif ($action -eq "bind") {
        & $buildScript -restore -build -pack -nodeReuse:$false -solution $solution -configuration $configuration
    }
    elseif ($action -eq "scry") {
        $runCore = $core -or -not ($core -or $desktop)  # default to both when neither switch is given; either switch alone runs just that one
        $runFramework = $desktop -or -not ($core -or $desktop)
        $callerMsbuildDebugPath = $env:MSBUILDDEBUGPATH  # captured before the restore/build below sets its own default, or this would snapshot that build-created value instead of "nothing was set"
        & $buildScript -restore -build -nodeReuse:$false -solution $solution -configuration $configuration
        $buildExitCode = $LASTEXITCODE
        if ($buildExitCode -ne 0) {
            exit $buildExitCode
        }
        function Get-TestSummary([string]$LogPath, [string]$Label, [int]$ExitCode) {  # tallies each leg's PASSED/FAILED/TIMEOUT counts from its already-logged runtests.log rather than re-printing RunTests' own live table a second time
            $result = [pscustomobject]@{ Label = $Label; Found = $false; Passed = 0; Failed = 0; Timeout = 0; ExitCode = $ExitCode }  # ExitCode carried through unchanged so a work item that threw before producing a TestResult still marks this leg red
            if (-not (Test-Path -LiteralPath $LogPath)) {
                return $result
            }
            $footerText = "Extra run diagnostics for logging, did not impact run results"  # anchor on the marker pair immediately before this exact, RunTests-authored footer -- captured test stdout/stderr elsewhere in the log can itself contain a "================" line, so neither the first nor the last marker pair alone is reliable
            $markerLineNumbers = [System.Collections.Generic.List[int]]::new()
            $footerLine = -1
            $lineNumber = 0
            foreach ($line in [System.IO.File]::ReadLines($LogPath)) {
                $lineNumber++
                if ($line -eq "================") {
                    $markerLineNumbers.Add($lineNumber)
                }
                elseif ($line -eq $footerText) {
                    $footerLine = $lineNumber
                }
            }
            if ($footerLine -lt 0) {
                return $result
            }
            $markersBeforeFooter = @($markerLineNumbers | Where-Object { $_ -lt $footerLine })
            if ($markersBeforeFooter.Count -lt 2) {
                return $result
            }
            $endLine = $markersBeforeFooter[$markersBeforeFooter.Count - 1]
            $startLine = $markersBeforeFooter[$markersBeforeFooter.Count - 2]
            $lineNumber = 0
            foreach ($line in [System.IO.File]::ReadLines($LogPath)) {
                $lineNumber++
                if ($lineNumber -le $startLine) {
                    continue
                }
                if ($lineNumber -ge $endLine) {
                    break
                }
                if ($line -match '\bTIMEOUT\b') { $result.Timeout++ }
                elseif ($line -match '\bFAILED\b') { $result.Failed++ }
                elseif ($line -match '\bPASSED\b') { $result.Passed++ }
            }
            $result.Found = $true
            return $result
        }
        $coreTestResultsDir = Join-Path $PSScriptRoot "artifacts\TestResults\$configuration-Core"
        $coreLogDir = Join-Path $PSScriptRoot "artifacts\log\$configuration-Core"
        $frameworkTestResultsDir = Join-Path $PSScriptRoot "artifacts\TestResults\$configuration-Framework"
        $frameworkLogDir = Join-Path $PSScriptRoot "artifacts\log\$configuration-Framework"
        $msbuildDebugPath = Join-Path $PSScriptRoot "artifacts\log\$configuration\MsbuildDebugLogs"  # matches eng/common/tools.ps1's own (unsuffixed) $LogDir\MsbuildDebugLogs convention
        Remove-Item -Recurse -Force -LiteralPath $coreTestResultsDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $coreLogDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $frameworkTestResultsDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $frameworkLogDir -ErrorAction SilentlyContinue
        $coreExitCode = 0
        if ($runCore) {
            $env:FOTU_TEST_RESULTS_SUFFIX = "Core"
            $env:MSBUILDDEBUGPATH = $msbuildDebugPath  # set explicitly every pass -- tools.ps1 only sets this itself when unset, so only the very first build.ps1 invocation in this process would otherwise ever set it
            try {
                & $buildScript -testCoreClr -testInteractiveConsole -nodeReuse:$false -testTimeout $testTimeout -solution $solution -configuration $configuration
                $coreExitCode = $LASTEXITCODE
            } finally {
                Remove-Item Env:\FOTU_TEST_RESULTS_SUFFIX -ErrorAction SilentlyContinue
                if ($null -eq $callerMsbuildDebugPath) {
                    Remove-Item Env:\MSBUILDDEBUGPATH -ErrorAction SilentlyContinue
                } else {
                    $env:MSBUILDDEBUGPATH = $callerMsbuildDebugPath
                }
            }
        }
        $frameworkExitCode = 0
        if ($runFramework) {
            $env:FOTU_TEST_RESULTS_SUFFIX = "Framework"
            $env:MSBUILDDEBUGPATH = $msbuildDebugPath
            try {
                & $buildScript -testDesktop -testInteractiveConsole -nodeReuse:$false -testTimeout $testTimeout -solution $solution -configuration $configuration
                $frameworkExitCode = $LASTEXITCODE
            } finally {
                Remove-Item Env:\FOTU_TEST_RESULTS_SUFFIX -ErrorAction SilentlyContinue
                if ($null -eq $callerMsbuildDebugPath) {
                    Remove-Item Env:\MSBUILDDEBUGPATH -ErrorAction SilentlyContinue
                } else {
                    $env:MSBUILDDEBUGPATH = $callerMsbuildDebugPath
                }
            }
        }
        $summaries = @()
        if ($runCore) {
            $summaries += Get-TestSummary -LogPath (Join-Path $coreLogDir "runtestsCore.log") -Label "Core" -ExitCode $coreExitCode
        }
        if ($runFramework) {
            $summaries += Get-TestSummary -LogPath (Join-Path $frameworkLogDir "runtestsFramework.log") -Label "Framework" -ExitCode $frameworkExitCode
        }
        $missingSummaries = @($summaries | Where-Object { -not $_.Found })
        $anyLegFailedExitCode = ($runCore -and $coreExitCode -ne 0) -or ($runFramework -and $frameworkExitCode -ne 0)
        $totalPassed = ($summaries | Measure-Object -Property Passed -Sum).Sum
        $totalFailed = ($summaries | Measure-Object -Property Failed -Sum).Sum
        $totalTimeout = ($summaries | Measure-Object -Property Timeout -Sum).Sum
        Write-Host ""
        Write-Host "=== Test summary ===" -ForegroundColor Cyan
        foreach ($summary in $summaries) {
            if ($summary.Found) {
                $legColor = if ($summary.Failed -gt 0 -or $summary.Timeout -gt 0 -or $summary.ExitCode -ne 0) { "Red" } else { "Green" }
                Write-Host "$($summary.Label): $($summary.Passed) passed, $($summary.Failed) failed, $($summary.Timeout) timeout" -ForegroundColor $legColor
            }
            else {
                Write-Host "$($summary.Label): summary unavailable (no runtests.log found)" -ForegroundColor Yellow
            }
        }
        $overallSuccess = -not $anyLegFailedExitCode -and $missingSummaries.Count -eq 0 -and $totalFailed -eq 0 -and $totalTimeout -eq 0  # green requires every requested leg to have exited 0 AND produced a readable summary with no failures/timeouts
        $overallColor = if ($overallSuccess) { "Green" } else { "Red" }
        Write-Host "Overall: $totalPassed passed, $totalFailed failed, $totalTimeout timeout" -ForegroundColor $overallColor
        Write-Host ""
        if ($runCore) {
            Write-Host "Core test results: $coreTestResultsDir (logs: $coreLogDir)"
        }
        if ($runFramework) {
            Write-Host "Framework test results: $frameworkTestResultsDir (logs: $frameworkLogDir)"
        }
        if ($coreExitCode -ne 0) {
            exit $coreExitCode
        }
        if ($frameworkExitCode -ne 0) {
            exit $frameworkExitCode
        }
        if (-not $overallSuccess) {
            exit 1  # every requested leg exited 0, but the summary itself says otherwise (e.g. a caught I/O error writing runtests.log) -- don't let that read as success to automation
        }
        exit 0
    }
    elseif ($action -eq "cleanse") {
        $artifactsDir = Join-Path $PSScriptRoot "artifacts"
        $localDotnet = Join-Path $PSScriptRoot ".dotnet\dotnet.exe"  # VBCSCompiler/MSBuild/the Razor server keep DLLs open under artifacts/ between invocations -- shut them down first so cleanse never races a locked file
        if (-not (Test-Path -LiteralPath $localDotnet)) {
            $localDotnet = Join-Path $PSScriptRoot ".dotnet/dotnet"
        }
        $dotnetExe = if (Test-Path -LiteralPath $localDotnet) {
            $localDotnet
        }
        else {
            $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
            if ($cmd) { $cmd.Source } else { $null }
        }
        if ($dotnetExe) {
            try { & $dotnetExe build-server shutdown *> $null } catch {}  # best-effort under ErrorActionPreference=Stop -- a failed shutdown must never block cleanup outright
        }
        if (Test-Path -LiteralPath $artifactsDir) {
            function Format-ByteSize([long]$bytes) {  # binary units throughout (1MB/1GB are PowerShell's built-in 1048576/1073741824 literals) -- labelled MiB/GiB, never MB/GB
                if ($bytes -ge 1GB) { return "{0:N2} GiB" -f ($bytes / 1GB) }
                elseif ($bytes -ge 1MB) { return "{0:N2} MiB" -f ($bytes / 1MB) }
                elseif ($bytes -ge 1KB) { return "{0:N2} KiB" -f ($bytes / 1KB) }
                else { return "$bytes B" }
            }
            function Get-DirStats([string]$dir) {
                $errs = $null
                $sum = Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue -ErrorVariable errs |  # piped straight into Measure-Object, not assigned first -- an assignment would materialize every FileInfo into an array before summing
                    Measure-Object -Property Length -Sum
                $bytes = if ($sum.Sum) { $sum.Sum } else { 0L }
                return [PSCustomObject]@{ Bytes = $bytes; Count = $sum.Count; Ok = ($errs.Count -eq 0) }  # Ok is $false whenever ErrorVariable caught anything, so a partial/truncated scan can be told apart from a genuinely complete one (matches folly.sh's dir_stats)
            }
            $spinnerFrames = @('|', '/', '-', '\')
            $spinnerIndex = 0
            $clearLine = "`r" + [char]27 + "[K"  # the scanning phase below is a bare manually-redrawn spinner (no Write-Progress); the cleansing phase keeps Write-Progress's percent/rate/counts, just without the spinner glyph
            $threadJobAvailable = $null -ne (Get-Command Start-ThreadJob -ErrorAction SilentlyContinue)  # Start-Job spins up a whole new powershell(.exe) process (multi-second cold start); Start-ThreadJob (in-box since PS7) runs in a runspace inside this process instead
            function Start-CleanseJob([scriptblock]$ScriptBlock, $ArgumentList) {
                if ($threadJobAvailable) {
                    return Start-ThreadJob -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList
                }
                return Start-Job -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList
            }
            $scanJob = $null  # both jobs are wrapped in try/finally below: on Ctrl+C, the finally block stops and removes whichever job is still running instead of leaving it attached to the session past the prompt returning
            $job = $null
            try {
                $scanJob = Start-CleanseJob -ScriptBlock {
                    param($dir)
                    $sum = Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue |
                        Measure-Object -Property Length -Sum
                    [PSCustomObject]@{ Bytes = if ($sum.Sum) { $sum.Sum } else { 0L }; Count = $sum.Count }
                } -ArgumentList $artifactsDir
                Write-Host -NoNewline "${clearLine}Scanning artefacts $($spinnerFrames[$spinnerIndex])"  # drawn immediately, before the loop's first 150ms tick, or the line stays blank that whole first interval
                while ($scanJob.State -notin 'Completed', 'Failed', 'Stopped') {  # not -eq 'Running': a freshly started job can still read 'NotStarted' on this first check (Start-ThreadJob starts fast enough to race it), which isn't 'Running' either but isn't finished
                    Start-Sleep -Milliseconds 150
                    $spinnerIndex = ($spinnerIndex + 1) % $spinnerFrames.Length
                    Write-Host -NoNewline "${clearLine}Scanning artefacts $($spinnerFrames[$spinnerIndex])"
                }
                $totalStats = Receive-Job -Job $scanJob
                Remove-Job -Job $scanJob
                $scanJob = $null
                $totalBytes = $totalStats.Bytes
                $totalCount = $totalStats.Count
                $totalFormatted = Format-ByteSize $totalBytes
                Write-Host -NoNewline $clearLine
                $job = Start-CleanseJob -ScriptBlock {  # raw .NET File.Delete/Directory.Delete, not the Remove-Item cmdlet -- writes each deleted file's length to the job's own output stream as it goes, unverified against Remove-Item -Recurse -Force's speed (no pwsh here to benchmark)
                    param($dir)
                    try { foreach ($f in [System.IO.Directory]::EnumerateFiles($dir, '*', [System.IO.SearchOption]::AllDirectories)) { try { $len = ([System.IO.FileInfo]$f).Length; [System.IO.File]::Delete($f); Write-Output $len } catch {} } } catch {}  # EnumerateFiles itself (not just File.Delete) can throw mid-walk on a locked subtree -- stop rather than fault the whole job with nothing cleaned up
                    try { $dirs = [System.IO.Directory]::EnumerateDirectories($dir, '*', [System.IO.SearchOption]::AllDirectories) | Sort-Object -Property { ($_ -split '[\\/]').Count } -Descending; foreach ($d in $dirs) { try { [System.IO.Directory]::Delete($d) } catch {} } } catch {}  # deepest first so a dir is always empty by the time it's deleted
                    try { [System.IO.Directory]::Delete($dir) } catch {}
                } -ArgumentList $artifactsDir
                $startTime = Get-Date
                $deletedBytes = 0L
                $deletedCount = 0
                while ($job.State -notin 'Completed', 'Failed', 'Stopped') {
                    Start-Sleep -Milliseconds 150
                    foreach ($size in (Receive-Job -Job $job)) { $deletedBytes += [long]$size; $deletedCount++ }  # drains the job's own stream instead of re-scanning the tree -- no second operation racing the delete
                    $percent = if ($totalBytes -gt 0) { [Math]::Min(99, [int](($deletedBytes / $totalBytes) * 100)) } else { [Math]::Min(99, [int](($deletedCount / [Math]::Max(1, $totalCount)) * 100)) }
                    $elapsedSeconds = ((Get-Date) - $startTime).TotalSeconds
                    $bytesPerSecond = if ($elapsedSeconds -gt 0) { $deletedBytes / $elapsedSeconds } else { 0 }
                    Write-Progress -Activity "Cleansing artefacts" -Status "$deletedCount / $totalCount files, $(Format-ByteSize $deletedBytes) / $totalFormatted, $(Format-ByteSize $bytesPerSecond)/s" -PercentComplete $percent
                }
                foreach ($size in (Receive-Job -Job $job -ErrorAction SilentlyContinue)) { $deletedBytes += [long]$size; $deletedCount++ }
                Remove-Job -Job $job
                $job = $null
            }
            finally {
                if ($scanJob) {
                    Stop-Job -Job $scanJob -ErrorAction SilentlyContinue
                    Remove-Job -Job $scanJob -Force -ErrorAction SilentlyContinue
                }
                if ($job) {
                    Stop-Job -Job $job -ErrorAction SilentlyContinue
                    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
                }
            }
            Write-Progress -Activity "Cleansing artefacts" -Completed
            Write-Host -NoNewline "`r"  # -Completed doesn't reliably leave the cursor at column 0
            if (Test-Path -LiteralPath $artifactsDir) {
                Remove-Item -Recurse -Force -LiteralPath $artifactsDir -ErrorAction SilentlyContinue  # a transiently held lock (e.g. an antivirus scanner) can clear between the bulk delete and now -- retry once before reporting survivors
            }
            if (Test-Path -LiteralPath $artifactsDir) {
                $remainingStats = Get-DirStats $artifactsDir
                $deletedBytes = [Math]::Max(0L, $totalBytes - $remainingStats.Bytes)
                if ($remainingStats.Ok) {
                    Write-Host "Cleansed $(Format-ByteSize $deletedBytes) of artefacts; $($remainingStats.Count) file(s) could not be removed." -ForegroundColor Yellow
                }
                else {
                    Write-Host "Cleansed $(Format-ByteSize $deletedBytes) of artefacts; at least $($remainingStats.Count) file(s) could not be removed (some may be unreadable and not counted)." -ForegroundColor Yellow  # Get-ChildItem hit an error partway, so remainingStats.Count is a lower bound, not exact
                }
                exit 1  # folly.sh exits 1 whenever artifacts/ survives cleanup too, so scripting/CI around either tool can rely on the same exit code meaning the same thing
            }
            else {
                Write-Host "Cleansed $totalFormatted from artefacts." -ForegroundColor Green
            }
        }
        exit 0
    }
    else {
        Write-Host "Unrecognized action '$action'. Expected 'attune', 'weave', 'reweave', 'bind', 'scry', 'cleanse', or 'grimoire'." -ForegroundColor Red
        exit 1
    }
    $buildExitCode = $LASTEXITCODE
    if ($buildExitCode -ne 0) {
        exit $buildExitCode
    }
    if ($action -eq "bind") {
        $packagesDir = Join-Path $PSScriptRoot "artifacts\packages\$configuration"
        if (-not (Test-Path -LiteralPath $packagesDir -PathType Container)) {
            Write-Host "Package output directory '$packagesDir' does not exist." -ForegroundColor Red
            exit 1
        }
        New-Item -ItemType Directory -Force -Path $nupkgDir | Out-Null
        & robocopy $packagesDir $nupkgDir /MIR /MT:16
        $robocopyExitCode = $LASTEXITCODE
        if ($robocopyExitCode -ge 8) {
            Write-Host "Robocopy failed with exit code $robocopyExitCode." -ForegroundColor Red
            exit $robocopyExitCode
        }
    }
    exit 0
}
finally {
    try { [Console]::CursorVisible = $true } catch {}
}
