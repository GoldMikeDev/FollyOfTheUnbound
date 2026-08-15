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
    foreach ($arg in $remainingArgs) {
        if ($arg -eq "--core") {
            $core = $true
        }
        elseif ($arg -eq "--desktop") {
            $desktop = $true
        }
        elseif ([string]::IsNullOrEmpty($config)) {
            $config = $arg
        }
        else {
            Write-Host "Unrecognised argument '$arg'." -ForegroundColor Red
            exit 1
        }
    }

    if ([string]::IsNullOrEmpty($action) -or $action -eq "grimoire") {
        Write-Host "folly.ps1 <action> [config]"
        Write-Host ""
        Write-Host "Actions:"
        Write-Host "  attune    Restore only [config]"
        Write-Host "  weave     Restore + build [config]"
        Write-Host "  reweave   Restore + rebuild [config]"
        Write-Host "  bind      Restore + build + pack [config] (copies .nupkg output to ../.nupkg/FotU)"
        Write-Host "  scry      Restore + build + run CoreCLR and Desktop unit tests [config]"
        Write-Host "  cleanse   Delete artefacts (ignores config)"
        Write-Host "  grimoire  Show this text (default when no action is given; ignores config)"
        Write-Host ""
        Write-Host "[config] (optional, defaults to Research):"
        Write-Host "  research  Debug"
        Write-Host "  truth     Release"
        Write-Host ""
        Write-Host "scry-only switches:"
        Write-Host "  --core     Run only the CoreCLR tests (skip Desktop)"
        Write-Host "  --desktop  Run only the Desktop tests (skip CoreCLR)"
        Write-Host "             (omit both to run both, the default)"
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
            # A full scry run's runtests.log also contains every failed test's captured
            # stdout/stderr, written before the summary table (see TestRunner.PrintFailedTestResult,
            # called from Print() before it writes the table) -- and that captured output could
            # itself contain a line that happens to equal "================", so the *first* two
            # matches in the file aren't reliable delimiters. The real summary table is always the
            # *last* such delimited block RunTests writes (nothing meaningful follows it in the log
            # but the small "Extra run diagnostics" section), so this finds the last two marker line
            # numbers in one streaming pass, then extracts just the lines between them in a second
            # streaming pass -- two passes, but neither materializes the whole (potentially large)
            # file into memory at once the way Get-Content or Select-String's match objects would.
            $markerLineNumbers = [System.Collections.Generic.List[int]]::new()
            $lineNumber = 0
            foreach ($line in [System.IO.File]::ReadLines($LogPath)) {
                $lineNumber++
                if ($line -eq "================") {
                    $markerLineNumbers.Add($lineNumber)
                }
            }
            if ($markerLineNumbers.Count -lt 2) {
                return $result
            }
            $startLine = $markerLineNumbers[$markerLineNumbers.Count - 2]
            $endLine = $markerLineNumbers[$markerLineNumbers.Count - 1]

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

        $testResultsDir = Join-Path $PSScriptRoot "artifacts\TestResults\$configuration"
        $logDir = Join-Path $PSScriptRoot "artifacts\log\$configuration"
        $coreClrTestResultsDir = "$testResultsDir-CoreClr"
        $coreClrLogDir = "$logDir-CoreClr"
        Remove-Item -Recurse -Force -LiteralPath $testResultsDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $logDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $coreClrTestResultsDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $coreClrLogDir -ErrorAction SilentlyContinue

        $coreClrExitCode = 0
        if ($runCoreClr) {
            & $buildScript -testCoreClr -testInteractiveConsole -solution $solution -configuration $configuration
            $coreClrExitCode = $LASTEXITCODE
            if (Test-Path -LiteralPath $testResultsDir) {
                Move-Item -Path $testResultsDir -Destination $coreClrTestResultsDir
            }
            if (Test-Path -LiteralPath $logDir) {
                Move-Item -Path $logDir -Destination $coreClrLogDir
            }
        }

        $desktopExitCode = 0
        if ($runDesktop) {
            & $buildScript -testDesktop -testInteractiveConsole -solution $solution -configuration $configuration
            $desktopExitCode = $LASTEXITCODE
        }

        $summaries = @()
        if ($runCoreClr) {
            $summaries += Get-TestSummary -LogPath (Join-Path $coreClrLogDir "runtests.log") -Label "CoreCLR" -ExitCode $coreClrExitCode
        }
        if ($runDesktop) {
            $summaries += Get-TestSummary -LogPath (Join-Path $logDir "runtests.log") -Label "Desktop" -ExitCode $desktopExitCode
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
            Write-Host "Desktop test results: $testResultsDir (logs: $logDir)"
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
        if (Test-Path -LiteralPath $artifactsDir) {
            function Format-ByteSize([long]$bytes) {
                if ($bytes -ge 1GB) { return "{0:N2} GiB" -f ($bytes / 1GB) }
                elseif ($bytes -ge 1MB) { return "{0:N2} MiB" -f ($bytes / 1MB) }
                elseif ($bytes -ge 1KB) { return "{0:N2} KiB" -f ($bytes / 1KB) }
                else { return "$bytes B" }
            }
            $spinnerFrames = @('|', '/', '-', '\')
            $spinnerIndex = 0
            $lastSpinnerUpdate = Get-Date -Year 1970
            $fileList = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
            Get-ChildItem -LiteralPath $artifactsDir -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object {
                $fileList.Add($_)
                $now = Get-Date
                if (($now - $lastSpinnerUpdate).TotalMilliseconds -ge 100) {
                    $lastSpinnerUpdate = $now
                    $spinnerIndex = ($spinnerIndex + 1) % $spinnerFrames.Length
                    Write-Progress -Activity "Enumerating files" -Status "$($spinnerFrames[$spinnerIndex]) $($fileList.Count) file(s) found"
                }
            }
            Write-Progress -Activity "Enumerating files" -Completed
            $files = $fileList.ToArray()
            $totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
            if (-not $totalBytes) { $totalBytes = 0 }
            $totalFormatted = Format-ByteSize $totalBytes
            $totalCount = $files.Count
            $deletedBytes = 0L
            $deletedCount = 0
            $failedCount = 0
            $startTime = Get-Date
            $lastUpdate = Get-Date -Year 1970
            foreach ($file in $files) {
                Remove-Item -Force -LiteralPath $file.FullName -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $file.FullName) {
                    $failedCount++
                }
                else {
                    $deletedBytes += $file.Length
                    $deletedCount++
                }
                $now = Get-Date
                if (($now - $lastUpdate).TotalMilliseconds -ge 100) {
                    $lastUpdate = $now
                    $percent = if ($totalBytes -gt 0) { [Math]::Min(99, [int](($deletedBytes / $totalBytes) * 100)) } else { [Math]::Min(99, [int](($deletedCount / $totalCount) * 100)) }
                    $elapsedSeconds = ($now - $startTime).TotalSeconds
                    $bytesPerSecond = if ($elapsedSeconds -gt 0) { $deletedBytes / $elapsedSeconds } else { 0 }
                    Write-Progress -Activity "Cleansing artefacts" -Status "$deletedCount / $totalCount files, $(Format-ByteSize $deletedBytes) / $totalFormatted, $(Format-ByteSize $bytesPerSecond)/s" -PercentComplete $percent
                }
            }
            Write-Progress -Activity "Cleansing artefacts" -Completed
            Remove-Item -Recurse -Force -LiteralPath $artifactsDir -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $artifactsDir) {
                Write-Host "Cleansed $(Format-ByteSize $deletedBytes) of artefacts; $failedCount file(s) could not be removed." -ForegroundColor Yellow
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