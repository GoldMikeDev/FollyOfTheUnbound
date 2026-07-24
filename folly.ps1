param
(
    [string]$action,
    [string]$config
)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = "Stop"

# FollyOfTheUnbound.slnx is Roslyn.slnx with the RoslynAnalyzers projects removed. Those projects
# build the shared Formatting/Extensions files against an older, released Microsoft.CodeAnalysis
# reference (by design, since an analyzer needs a stable host) and don't know about C#Unbound's new
# SyntaxKinds, so they fail whenever the language grows. They're Roslyn's own dogfooding lint tools
# anyway - not needed to build or use C#Unbound. Kept as its own file (not an edit to Roslyn.slnx)
# so merging from upstream dotnet/roslyn doesn't conflict here.

$solution = "FollyOfTheUnbound.slnx"
$buildScript = Join-Path $PSScriptRoot "eng\build.ps1"
$nupkgRoot = Join-Path $PSScriptRoot "..\.nupkg\FotU"

if ([string]::IsNullOrEmpty($config) -or $config -eq "Debug") {
    $configuration = "Debug"
    $nupkgDir = Join-Path $nupkgRoot "Debug"
}
elseif ($config -eq "Release") {
    $configuration = "Release"
    $nupkgDir = Join-Path $nupkgRoot "Release"
}
else {
    Write-Host "Unrecognized configuration '$config'. Expected 'Debug', 'Release', or omitted (defaults to Debug)." -ForegroundColor Red
    exit 1
}

# Plain `dotnet build`/`dotnet pack` bypass this repo's SDK bootstrap and Arcade toolset (the thing
# that made Build.cmd succeed earlier when a bare `dotnet build <csproj>` failed with an SDK-not-found
# error), so both actions go through eng/build.ps1 instead.

if ($action -eq "attune") {
    & $buildScript -restore -solution $solution -configuration $configuration
}
elseif ($action -eq "weave") {
    & $buildScript -restore -build -solution $solution -configuration $configuration
}
elseif ($action -eq "bind") {
    & $buildScript -restore -build -pack -solution $solution -configuration $configuration
}
else {
    Write-Host "Unrecognized action '$action'. Expected 'attune', 'weave', or 'bind'." -ForegroundColor Red
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