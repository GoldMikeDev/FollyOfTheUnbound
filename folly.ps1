param
(
    [string]$action,
    [string]$config
)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

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

if ($action -eq "weave") {
    & $buildScript -restore -build -solution $solution -configuration $configuration
}
elseif ($action -eq "bind") {
    & $buildScript -restore -build -pack -solution $solution -configuration $configuration
    if ($LASTEXITCODE -eq 0) {
        New-Item -ItemType Directory -Force -Path $nupkgDir | Out-Null
        $packagesDir = Join-Path $PSScriptRoot "artifacts\packages\$configuration"
        Get-ChildItem -Path $packagesDir -Filter "*.nupkg" -Recurse | Copy-Item -Destination $nupkgDir -Force
    }
}