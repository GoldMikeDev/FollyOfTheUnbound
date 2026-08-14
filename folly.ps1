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
        Write-Host "  cleanse   Delete artefacts (ignores config)"
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
        & $buildScript -restore -build -solution $solution -configuration $configuration
        $buildExitCode = $LASTEXITCODE
        if ($buildExitCode -ne 0) {
            exit $buildExitCode
        }
        $testResultsDir = Join-Path $PSScriptRoot "artifacts\TestResults\$configuration"
        $logDir = Join-Path $PSScriptRoot "artifacts\log\$configuration"
        $coreClrTestResultsDir = "$testResultsDir-CoreClr"
        $coreClrLogDir = "$logDir-CoreClr"
        Remove-Item -Recurse -Force -LiteralPath $testResultsDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $logDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $coreClrTestResultsDir -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -LiteralPath $coreClrLogDir -ErrorAction SilentlyContinue
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