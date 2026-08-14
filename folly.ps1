param
(
    [string]$action,
    [string]$config
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
        Write-Host "  cleanse   Delete artifacts/ (ignores config)"
        Write-Host "  grimoire  Show this text (default when no action is given; ignores config)"
        Write-Host ""
        Write-Host "[config] (optional, defaults to Research):"
        Write-Host "  research  Debug"
        Write-Host "  truth     Release"
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
        Write-Host "Unrecognized configuration '$config'. Expected 'Debug', 'Release', or omitted (defaults to Debug)." -ForegroundColor Red
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
        # Windows is the only platform that can run Desktop/.NET Framework tests at all (there's no net472
        # runtime on Linux/macOS, which is why folly.sh's scry only ever runs CoreCLR tests) -- so here, where
        # both are available, run both rather than picking one and silently dropping the other's coverage.
        # Build once, then two test-only passes against the same build (see build.ps1's own remarks on
        # `-build -testDesktop` followed by repeated test-only calls being safe without rebuilding).
        & $buildScript -restore -build -solution $solution -configuration $configuration
        $buildExitCode = $LASTEXITCODE
        if ($buildExitCode -ne 0) {
            exit $buildExitCode
        }

        # RunTests names each pass's result/log files by partition index and architecture alone, truncating
        # (not appending to) whatever is already there, and never removes stale files (e.g. old
        # xUnitFailure-* logs) it doesn't happen to overwrite. Without clearing these directories before the
        # CoreCLR pass even starts, a rerun of scry (without cleanse first) could carry a *previous* run's
        # Desktop-pass leftovers into this run's CoreCLR archive below, and without moving the CoreCLR pass's
        # own results out of the way before the Desktop pass, that pass would then silently overwrite them --
        # including on a CoreCLR failure, right when a developer most needs to see what failed.
        $testResultsDir = Join-Path $PSScriptRoot "artifacts\TestResults\$configuration"
        $logDir = Join-Path $PSScriptRoot "artifacts\log\$configuration"
        $coreClrTestResultsDir = "$testResultsDir-CoreClr"
        $coreClrLogDir = "$logDir-CoreClr"
        # Also clear any stale -CoreClr archive from a previous run up front (not just when this run produces a
        # replacement below): if this pass fails before RunTests even creates $testResultsDir/$logDir (e.g. test
        # discovery finding no assemblies), leaving the old archive in place would misrepresent a prior run's
        # results as diagnostics for the current failure.
        Remove-Item -Recurse -Force -LiteralPath $testResultsDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $logDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $coreClrTestResultsDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $coreClrLogDir -ErrorAction SilentlyContinue

        # -testInteractiveConsole: scry is a known-interactive, human-invoked entry point (unlike a bare
        # eng/build.ps1 call, which might be piped by some other caller), so it's safe to let RunTests inherit
        # the real console here -- that's what lets its live progress table engage. See build.ps1's own remarks
        # on why this can't just be auto-detected.
        & $buildScript -testCoreClr -testInteractiveConsole -solution $solution -configuration $configuration
        $coreClrExitCode = $LASTEXITCODE

        if (Test-Path -LiteralPath $testResultsDir) {
            Move-Item -Path $testResultsDir -Destination $coreClrTestResultsDir
        }
        if (Test-Path -LiteralPath $logDir) {
            Move-Item -Path $logDir -Destination $coreClrLogDir
        }

        & $buildScript -testDesktop -testInteractiveConsole -solution $solution -configuration $configuration
        $desktopExitCode = $LASTEXITCODE

        Write-Host ""
        Write-Host "CoreCLR test results: $coreClrTestResultsDir (logs: $coreClrLogDir)"
        Write-Host "Desktop test results: $testResultsDir (logs: $logDir)"

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

            $files = @(Get-ChildItem -LiteralPath $artifactsDir -Recurse -Force -File -ErrorAction SilentlyContinue)
            $totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
            if (-not $totalBytes) { $totalBytes = 0 }
            $totalFormatted = Format-ByteSize $totalBytes

            $deletedBytes = 0L
            $lastUpdate = Get-Date -Year 1970
            foreach ($file in $files) {
                $fileLength = $file.Length
                Remove-Item -Force -LiteralPath $file.FullName -ErrorAction SilentlyContinue
                $deletedBytes += $fileLength

                $now = Get-Date
                if (($now - $lastUpdate).TotalMilliseconds -ge 100) {
                    $lastUpdate = $now
                    $percent = if ($totalBytes -gt 0) { [Math]::Min(100, [int](($deletedBytes / $totalBytes) * 100)) } else { 100 }
                    Write-Progress -Activity "Cleansing artifacts/" -Status "$(Format-ByteSize $deletedBytes) / $totalFormatted" -PercentComplete $percent
                }
            }
            Write-Progress -Activity "Cleansing artifacts/" -Completed

            Remove-Item -Recurse -Force -LiteralPath $artifactsDir -ErrorAction SilentlyContinue
            Write-Host "Cleansed $totalFormatted from artifacts/."
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