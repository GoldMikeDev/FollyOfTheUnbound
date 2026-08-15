param
(
    [string]$action,
    [string]$config,
    [switch]$core,
    [switch]$desktop
)
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    try { [Console]::CursorVisible = $false } catch {}
    $ErrorActionPreference = "Stop"
    $solution = "FollyOfTheUnbound.slnx"
    $buildScript = Join-Path $PSScriptRoot "eng\build.ps1"
    $nupkgRoot = Join-Path $PSScriptRoot "..\.nupkg\FotU"
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
        Write-Host "  -core     Run only the CoreCLR tests (skip Desktop)"
        Write-Host "  -desktop  Run only the Desktop tests (skip CoreCLR)"
        Write-Host "            (omit both to run both, the default)"
        Write-Host ""
        exit 0
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

        # Prints the PASSED/FAILED/TIMEOUT summary table RunTests already wrote to runtests.log
        # (every line it prints goes through ConsoleUtil, which logs as well as writing to the
        # console -- see src/Tools/RunTests/ConsoleUtil.cs). Reading it back here, after both legs
        # have fully finished, avoids relying on terminal scrollback surviving RunTests' live
        # table's alternate-screen-buffer switches across two back-to-back invocations, which is
        # unreliable -- particularly for CoreCLR's table, immediately buried by Desktop's own
        # alt-screen entry right after.
        function Show-TestSummary([string]$LogPath, [string]$Label) {
            $result = [pscustomobject]@{ Label = $Label; Found = $false; Passed = 0; Failed = 0; Timeout = 0 }
            Write-Host ""
            Write-Host "=== $Label ===" -ForegroundColor Cyan
            if (-not (Test-Path -LiteralPath $LogPath)) {
                Write-Host "No log found at $LogPath" -ForegroundColor Yellow
                return $result
            }
            $markers = Select-String -LiteralPath $LogPath -Pattern '^================$'
            if ($markers.Count -lt 2) {
                Write-Host "Summary table not found in $LogPath" -ForegroundColor Yellow
                return $result
            }
            $lines = Get-Content -LiteralPath $LogPath
            $startLine = $markers[0].LineNumber
            $endLine = $markers[1].LineNumber
            for ($i = $startLine - 1; $i -le $endLine - 1; $i++) {
                $line = $lines[$i]
                if ($line -match '\bTIMEOUT\b') {
                    $result.Timeout++
                    Write-Host $line -ForegroundColor Yellow
                }
                elseif ($line -match '\bFAILED\b') {
                    $result.Failed++
                    Write-Host $line -ForegroundColor Red
                }
                elseif ($line -match '\bPASSED\b') {
                    $result.Passed++
                    Write-Host $line -ForegroundColor Green
                }
                else {
                    Write-Host $line
                }
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
            $summaries += Show-TestSummary -LogPath (Join-Path $coreClrLogDir "runtests.log") -Label "CoreCLR test summary"
        }
        if ($runDesktop) {
            $summaries += Show-TestSummary -LogPath (Join-Path $logDir "runtests.log") -Label "Desktop test summary"
        }

        $totalPassed = ($summaries | Measure-Object -Property Passed -Sum).Sum
        $totalFailed = ($summaries | Measure-Object -Property Failed -Sum).Sum
        $totalTimeout = ($summaries | Measure-Object -Property Timeout -Sum).Sum
        Write-Host ""
        $overallColor = if ($totalFailed -gt 0 -or $totalTimeout -gt 0) { "Red" } else { "Green" }
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