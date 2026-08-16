param
(
    [Parameter(Position = 0)][string]$action,
    # Deliberately not given an explicit Position: PowerShell only auto-numbers positional slots
    # for parameters when *none* of a script's parameters declare an explicit Position, so once
    # $action above claims Position 0, $config here becomes name-only (-config still works) and
    # every other positional token -- [config] included -- falls through to $remainingArgs, letting
    # the manual parse below tell "truth"/"research" apart from --core/--desktop without ambiguity.
    # (Verified against pwsh 7.6.0: `-action scry -config truth` still binds $config normally.)
    [string]$config,
    [parameter(ValueFromRemainingArguments = $true)][string[]]$remainingArgs
)
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    try { [Console]::CursorVisible = $false } catch {}
    $ErrorActionPreference = "Stop"
    $solution = "FollyOfTheUnbound.slnx"
    $buildScript = Join-Path $PSScriptRoot "eng\build.ps1"
    $nupkgRoot = Join-Path $PSScriptRoot "..\.nupkg\FotU"

    # PowerShell's automatic parameter binding only recognises single-dash switches, so --core/
    # --desktop (matching folly.sh's own --restore/--build style) are parsed by hand here; the
    # [config] positional falls through to here too (see the param block comment above) unless it
    # was already supplied by name via -config.
    $core = $false
    $desktop = $false
    $testTimeout = 0
    $expectTimeoutValue = $false
    foreach ($arg in $remainingArgs) {
        if ($expectTimeoutValue) {
            if (-not [int]::TryParse($arg, [ref]$testTimeout) -or $testTimeout -le 0) {
                Write-Host "'--timeout' requires a positive integer minute count, got '$arg'." -ForegroundColor Red
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
        Write-Host "  scry      Restore + build + run CoreCLR and Desktop unit tests [config]"
        Write-Host "  cleanse   Delete artefacts (ignores config)"
        Write-Host "  grimoire  Show this text (default when no action is given; ignores config)"
        Write-Host ""
        Write-Host "[config] (optional, positional, or named as -config <config>; defaults to Research):"
        Write-Host "  research  Debug"
        Write-Host "  truth     Release"
        Write-Host ""
        Write-Host "scry-only switches (not positional -- always passed by name, after [config]):"
        Write-Host "  --core               Run only the CoreCLR tests (skip Desktop)"
        Write-Host "  --desktop            Run only the Desktop tests (skip CoreCLR)"
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
    if ($action -eq "attune") {
        & $buildScript -restore -solution $solution -configuration $configuration
    }
    elseif ($action -eq "weave") {
        & $buildScript -restore -build -solution $solution -configuration $configuration
    }
    elseif ($action -eq "reweave") {
        & $buildScript -restore -rebuild -solution $solution -configuration $configuration
    }
    elseif ($action -eq "bind") {
        & $buildScript -restore -build -pack -solution $solution -configuration $configuration
    }
    elseif ($action -eq "scry") {
        # Default to both when neither switch is given; either switch alone runs just that one.
        $runCoreClr = $core -or -not ($core -or $desktop)
        $runDesktop = $desktop -or -not ($core -or $desktop)

        & $buildScript -restore -build -solution $solution -configuration $configuration
        $buildExitCode = $LASTEXITCODE
        if ($buildExitCode -ne 0) {
            exit $buildExitCode
        }

        # RunTests already prints its own PASSED/FAILED/TIMEOUT table live (Print() runs after
        # LiveTestProgressDisplay.Complete() exits the alternate screen, so it lands in the normal,
        # persisted scrollback, not just the ephemeral live table) -- replaying those same rows here
        # would just print every table a second time. What's actually missing is a single combined
        # rollup once *both* legs are done, so this only tallies each leg's counts from the
        # already-logged runtests.log (every line RunTests prints also goes through ConsoleUtil,
        # which logs it -- see src/Tools/RunTests/ConsoleUtil.cs) without re-printing the rows.
        function Get-TestSummary([string]$LogPath, [string]$Label, [int]$ExitCode) {
            # ExitCode is carried through unchanged so a work item that threw before producing a
            # TestResult (e.g. its response file or test process couldn't be created --
            # TestRunner.RunAllAsync counts that as a failure but never adds it to `completed`, so
            # it never becomes a row in the table at all) still marks this leg red even though the
            # parsed Failed/Timeout counts alone wouldn't show it.
            $result = [pscustomobject]@{ Label = $Label; Found = $false; Passed = 0; Failed = 0; Timeout = 0; ExitCode = $ExitCode }
            if (-not (Test-Path -LiteralPath $LogPath)) {
                return $result
            }
            # runtests.log also contains every failed test's captured stdout/stderr -- once before
            # the summary table (TestRunner.PrintFailedTestResult, called from Print() ahead of the
            # table) and again after it (Program.LogProcessResultDetails, called after RunAllAsync
            # returns but before WriteLogFile persists the log -- see Program.cs) -- and that
            # captured output could itself contain a line equal to "================" on either
            # side, so neither the first nor the last marker pair in the file is a reliable
            # delimiter. Print() does write one fixed, RunTests-authored line immediately after the
            # table's real closing marker, though (see TestRunner.cs), which -- being an exact,
            # deliberately-worded internal string -- is far less likely to occur by chance in
            # arbitrary captured test output than a generic divider is. Anchor on the marker
            # immediately preceding the last occurrence of that footer instead.
            $footerText = "Extra run diagnostics for logging, did not impact run results"
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

        $coreClrTestResultsDir = Join-Path $PSScriptRoot "artifacts\TestResults\$configuration-CoreClr"
        $coreClrLogDir = Join-Path $PSScriptRoot "artifacts\log\$configuration-CoreClr"
        $desktopTestResultsDir = Join-Path $PSScriptRoot "artifacts\TestResults\$configuration-Desktop"
        $desktopLogDir = Join-Path $PSScriptRoot "artifacts\log\$configuration-Desktop"
        # Matches eng/common/tools.ps1's own (unsuffixed) $LogDir\MsbuildDebugLogs convention.
        $msbuildDebugPath = Join-Path $PSScriptRoot "artifacts\log\$configuration\MsbuildDebugLogs"
        # Preserve whatever the caller already had set (if anything) so the per-pass overrides below
        # don't leak out and permanently change the invoking shell's environment once scry returns.
        $callerMsbuildDebugPath = $env:MSBUILDDEBUGPATH
        Remove-Item -Recurse -Force -LiteralPath $coreClrTestResultsDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $coreClrLogDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $desktopTestResultsDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $desktopLogDir -ErrorAction SilentlyContinue

        $coreClrExitCode = 0
        if ($runCoreClr) {
            $env:FOTU_TEST_RESULTS_SUFFIX = "CoreClr"
            # eng/common/tools.ps1 only ever sets $env:MSBUILDDEBUGPATH itself when it's unset
            # (`if (-not $env:MSBUILDDEBUGPATH)`), and since folly.ps1 invokes eng/build.ps1
            # multiple times in this same PowerShell process, that guard means only the very
            # first invocation (the initial -restore -build above) would ever actually set it,
            # leaving every later pass stuck reusing that first value instead of getting its own.
            # Set it explicitly here. Both passes share one directory -- MSBuild's own debug log
            # filenames already embed a unique per-process token, and the passes run sequentially
            # (never concurrently), so there's no collision risk, and one directory is simpler to
            # read later than per-pass ones.
            $env:MSBUILDDEBUGPATH = $msbuildDebugPath
            try {
                & $buildScript -testCoreClr -testInteractiveConsole -testTimeout $testTimeout -solution $solution -configuration $configuration
                $coreClrExitCode = $LASTEXITCODE
            } finally {
                Remove-Item Env:\FOTU_TEST_RESULTS_SUFFIX -ErrorAction SilentlyContinue
                if ($null -eq $callerMsbuildDebugPath) {
                    Remove-Item Env:\MSBUILDDEBUGPATH -ErrorAction SilentlyContinue
                } else {
                    $env:MSBUILDDEBUGPATH = $callerMsbuildDebugPath
                }
            }
        }

        $desktopExitCode = 0
        if ($runDesktop) {
            $env:FOTU_TEST_RESULTS_SUFFIX = "Desktop"
            $env:MSBUILDDEBUGPATH = $msbuildDebugPath
            try {
                & $buildScript -testDesktop -testInteractiveConsole -testTimeout $testTimeout -solution $solution -configuration $configuration
                $desktopExitCode = $LASTEXITCODE
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
        if ($runCoreClr) {
            $summaries += Get-TestSummary -LogPath (Join-Path $coreClrLogDir "runtestsCoreCLR.log") -Label "CoreCLR" -ExitCode $coreClrExitCode
        }
        if ($runDesktop) {
            $summaries += Get-TestSummary -LogPath (Join-Path $desktopLogDir "runtestsDesktop.log") -Label "Desktop" -ExitCode $desktopExitCode
        }

        $missingSummaries = @($summaries | Where-Object { -not $_.Found })
        $anyLegFailedExitCode = ($runCoreClr -and $coreClrExitCode -ne 0) -or ($runDesktop -and $desktopExitCode -ne 0)
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
        # Green requires every requested leg to have both exited 0 *and* produced a readable
        # summary with no failures/timeouts -- any of those being off (a crash before runtests.log
        # was written, a nonzero exit the parsed counts didn't capture, or an actual failure) must
        # never present as green.
        $overallSuccess = -not $anyLegFailedExitCode -and $missingSummaries.Count -eq 0 -and $totalFailed -eq 0 -and $totalTimeout -eq 0
        $overallColor = if ($overallSuccess) { "Green" } else { "Red" }
        Write-Host "Overall: $totalPassed passed, $totalFailed failed, $totalTimeout timeout" -ForegroundColor $overallColor

        Write-Host ""
        if ($runCoreClr) {
            Write-Host "CoreCLR test results: $coreClrTestResultsDir (logs: $coreClrLogDir)"
        }
        if ($runDesktop) {
            Write-Host "Desktop test results: $desktopTestResultsDir (logs: $desktopLogDir)"
        }
        if ($coreClrExitCode -ne 0) {
            exit $coreClrExitCode
        }
        if ($desktopExitCode -ne 0) {
            exit $desktopExitCode
        }
        if (-not $overallSuccess) {
            # Every requested leg exited 0, but the summary itself says otherwise (e.g. RunTests hit
            # a caught I/O error writing runtests.log -- see Program.WriteLogFile -- so the process
            # still exits 0 with no readable summary). Don't let that read as success to automation.
            exit 1
        }
        exit 0
    }
    elseif ($action -eq "cleanse") {
        $artifactsDir = Join-Path $PSScriptRoot "artifacts"
        # VBCSCompiler / the MSBuild build server / the Razor build server
        # keep running between invocations and can hold an out-of-process
        # BuildHost alive with Microsoft.CodeAnalysis.Workspaces.MSBuild*.dll
        # loaded from artifacts/ -- Windows blocks deleting a DLL a running
        # process still has open, so cleanse would intermittently fail on
        # those two files. Shut the servers down first so it never races a
        # locked file.
        #
        # attune/weave/etc. run through eng/common/tools.ps1's
        # InitializeDotNetCli, which bootstraps a repo-local SDK under
        # .dotnet/ and only puts it on PATH inside that child build process
        # -- it never updates this process's PATH. A developer without a
        # global `dotnet` install would silently skip the shutdown here and
        # still hit the DLL lock, so check the repo-local SDK first and only
        # fall back to a global `dotnet` on PATH.
        $localDotnet = Join-Path $PSScriptRoot ".dotnet\dotnet.exe"
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
            # Best-effort: under this script's ErrorActionPreference = "Stop",
            # a launch failure (e.g. an incomplete or wrong-architecture SDK
            # bootstrap) would otherwise be a terminating error that aborts
            # cleanse before it ever attempts to remove artifacts/. A failed
            # shutdown just means the DLL-lock workaround didn't help this
            # run -- it must never block cleanup outright.
            try { & $dotnetExe build-server shutdown *> $null } catch {}
        }
        if (Test-Path -LiteralPath $artifactsDir) {
            # Binary units throughout (1MB/1GB are PowerShell's built-in
            # 1048576/1073741824 literals) -- labelled MiB/GiB, never MB/GB.
            function Format-ByteSize([long]$bytes) {
                if ($bytes -ge 1GB) { return "{0:N2} GiB" -f ($bytes / 1GB) }
                elseif ($bytes -ge 1MB) { return "{0:N2} MiB" -f ($bytes / 1MB) }
                elseif ($bytes -ge 1KB) { return "{0:N2} KiB" -f ($bytes / 1KB) }
                else { return "$bytes B" }
            }
            # Pipe straight into Measure-Object rather than assigning
            # Get-ChildItem's output to a variable first -- an assignment
            # materializes every FileInfo into an array before summing,
            # which on the large trees this is meant to help with retains a
            # full-tree object array on every progress refresh. Piping keeps
            # this streaming, one FileInfo at a time.
            function Get-DirStats([string]$dir) {
                # -ErrorVariable still captures errors that -ErrorAction
                # SilentlyContinue suppresses from the console (e.g. Access
                # to the path 'foo' is denied for a subtree this process
                # can't read) -- Ok is $false whenever any were hit, so a
                # partial/truncated traversal can be told apart from a
                # genuinely empty or fully-readable one. Matches folly.sh's
                # dir_stats "ok" flag for the same reason: silently trusting
                # a partial count as exact would let the final summary
                # report "0 files could not be removed" when files actually
                # survived in a subtree this scan couldn't see into.
                $errs = $null
                $sum = Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue -ErrorVariable errs |
                    Measure-Object -Property Length -Sum
                $bytes = if ($sum.Sum) { $sum.Sum } else { 0L }
                # [PSCustomObject], not a Hashtable (@{...}) -- Hashtable has
                # its own native .Count property (the number of keys, always
                # 2 here), which shadows a key literally named "Count" and
                # silently returns the wrong number for every caller of this
                # function.
                return [PSCustomObject]@{ Bytes = $bytes; Count = $sum.Count; Ok = ($errs.Count -eq 0) }
            }

            # The actual removal is a single bulk `Remove-Item -Recurse -Force`,
            # run in a background job -- far faster than the previous
            # implementation's per-file foreach loop, which is why cleanse felt
            # much slower than Explorer's "Remove item". Progress is reported
            # by periodically re-scanning what's left with Get-DirStats, so the
            # display doesn't add per-file cost back into the deletion path.
            $spinnerFrames = @('|', '/', '-', '\')
            $spinnerIndex = 0

            # Get-DirStats on the full tree can itself take a while on a
            # large build output -- run it as a background job too and show
            # a spinner instead of leaving the terminal blank until the scan
            # finishes.
            #
            # Both this and the delete job below are wrapped in try/finally:
            # if Ctrl+C interrupts the polling loop, PowerShell unwinds
            # through the finally block, which stops and removes whichever
            # job is still non-$null at that point -- without it, a job
            # left running would stay attached to the session and keep
            # executing Remove-Item after the prompt returns, so Ctrl+C
            # wouldn't actually cancel the deletion.
            $scanJob = $null
            $job = $null
            try {
                $scanJob = Start-Job -ScriptBlock {
                    param($dir)
                    $sum = Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue |
                        Measure-Object -Property Length -Sum
                    [PSCustomObject]@{ Bytes = if ($sum.Sum) { $sum.Sum } else { 0L }; Count = $sum.Count }
                } -ArgumentList $artifactsDir
                $lastScanUpdate = Get-Date -Year 1970
                while ($scanJob.State -eq 'Running') {
                    $now = Get-Date
                    if (($now - $lastScanUpdate).TotalMilliseconds -ge 100) {
                        $lastScanUpdate = Get-Date
                        $spinnerIndex = ($spinnerIndex + 1) % $spinnerFrames.Length
                        Write-Progress -Activity "Scanning artefacts" -Status "$($spinnerFrames[$spinnerIndex])"
                    }
                    Start-Sleep -Milliseconds 50
                }
                $totalStats = Receive-Job -Job $scanJob
                Remove-Job -Job $scanJob
                $scanJob = $null
                Write-Progress -Activity "Scanning artefacts" -Completed

                $totalBytes = $totalStats.Bytes
                $totalCount = $totalStats.Count
                $totalFormatted = Format-ByteSize $totalBytes

                $startTime = Get-Date
                $job = Start-Job -ScriptBlock {
                    param($dir)
                    Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue
                } -ArgumentList $artifactsDir

                $deletedBytes = 0L
                $deletedCount = 0
                $lastUpdate = Get-Date -Year 1970
                while ($job.State -eq 'Running') {
                    $now = Get-Date
                    if (($now - $lastUpdate).TotalMilliseconds -ge 100) {
                        $spinnerIndex = ($spinnerIndex + 1) % $spinnerFrames.Length
                        $remainingBytes = 0L
                        $remainingCount = 0
                        if (Test-Path -LiteralPath $artifactsDir) {
                            $remainingStats = Get-DirStats $artifactsDir
                            $remainingBytes = $remainingStats.Bytes
                            $remainingCount = $remainingStats.Count
                        }
                        # Stamp the throttle from *after* the scan, not before
                        # -- on the large trees this is meant to help with,
                        # Get-DirStats can itself take longer than 100ms, and
                        # timestamping before it would let the next loop
                        # iteration fire immediately, keeping a second
                        # full-tree walker running continuously alongside
                        # Remove-Item and fighting it for the same filesystem
                        # I/O.
                        $lastUpdate = Get-Date
                        $deletedBytes = [Math]::Max(0L, $totalBytes - $remainingBytes)
                        $deletedCount = [Math]::Max(0, $totalCount - $remainingCount)
                        $percent = if ($totalBytes -gt 0) { [Math]::Min(99, [int](($deletedBytes / $totalBytes) * 100)) } else { [Math]::Min(99, [int](($deletedCount / [Math]::Max(1, $totalCount)) * 100)) }
                        $elapsedSeconds = ($lastUpdate - $startTime).TotalSeconds
                        $bytesPerSecond = if ($elapsedSeconds -gt 0) { $deletedBytes / $elapsedSeconds } else { 0 }
                        Write-Progress -Activity "Cleansing artefacts" -Status "$($spinnerFrames[$spinnerIndex]) $deletedCount / $totalCount files, $(Format-ByteSize $deletedBytes) / $totalFormatted, $(Format-ByteSize $bytesPerSecond)/s" -PercentComplete $percent
                    }
                    Start-Sleep -Milliseconds 50
                }
                Receive-Job -Job $job -ErrorAction SilentlyContinue | Out-Null
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
            # Write-Progress's "completed" pane doesn't reliably leave the
            # cursor at column 0 in every console host (conhost in
            # particular), which left stray blank padding in front of the
            # line below -- force a fresh line rather than trusting
            # -Completed alone.
            Write-Host ""

            if (Test-Path -LiteralPath $artifactsDir) {
                # A lock held only transiently (e.g. an antivirus scanner, or
                # a process still winding down) can clear between the bulk
                # delete and now -- retry once before reporting survivors,
                # the same second chance the old per-file loop gave every
                # file implicitly by continuing past individual failures.
                Remove-Item -Recurse -Force -LiteralPath $artifactsDir -ErrorAction SilentlyContinue
            }

            if (Test-Path -LiteralPath $artifactsDir) {
                $remainingStats = Get-DirStats $artifactsDir
                $deletedBytes = [Math]::Max(0L, $totalBytes - $remainingStats.Bytes)
                if ($remainingStats.Ok) {
                    Write-Host "Cleansed $(Format-ByteSize $deletedBytes) of artefacts; $($remainingStats.Count) file(s) could not be removed." -ForegroundColor Yellow
                }
                else {
                    # Get-ChildItem hit an error partway (e.g. an
                    # access-denied subtree), so remainingStats.Count only
                    # reflects what it could see -- reporting it as exact
                    # would understate (possibly to a false "0") how much is
                    # actually left behind. Matches folly.sh's equivalent
                    # "at least N ... unreadable" wording.
                    Write-Host "Cleansed $(Format-ByteSize $deletedBytes) of artefacts; at least $($remainingStats.Count) file(s) could not be removed (some may be unreadable and not counted)." -ForegroundColor Yellow
                }
                # folly.sh exits 1 whenever artifacts/ survives cleanup, so
                # scripting/CI around either tool can rely on the same exit
                # code meaning the same thing -- this previously always
                # exited 0 here, hiding an incomplete cleanup from callers.
                exit 1
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