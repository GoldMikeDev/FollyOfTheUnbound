# Regression test for folly.ps1 scry's argument parsing (action, [config], --core/--desktop) and
# its unified test summary, run against a mocked eng/build.ps1 so no real build/test happens.
# Run by hand (or wire into CI) after touching folly.ps1's argument parsing or scry action:
#   pwsh -File ./scripts/test-folly-scry-args.ps1
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $PSScriptRoot
$follyPs1 = Join-Path $scriptRoot "folly.ps1"
$pwshExe = (Get-Process -Id $PID).Path

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("folly-scry-args-test-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

$script:passCount = 0
$script:failCount = 0

function Test-Pass([string]$Message) {
    Write-Host "PASS: $Message" -ForegroundColor Green
    $script:passCount++
}

function Test-Fail([string]$Message) {
    Write-Host "FAIL: $Message" -ForegroundColor Red
    $script:failCount++
}

function New-TestCase([string]$Name) {
    $dir = Join-Path $workRoot $Name
    Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $dir "eng") | Out-Null
    Copy-Item -LiteralPath $follyPs1 -Destination (Join-Path $dir "folly.ps1")

    # A minimal stand-in for eng/build.ps1: succeeds for restore/build, and for the test legs
    # writes a runtests.log with exactly one PASSED row so folly.ps1's summary reader has
    # something real to parse, without running any actual build or tests.
    $mockBuild = @'
param(
    [switch]$restore,[switch]$build,[switch]$rebuild,[switch]$pack,
    [switch]$testCoreClr,[switch]$testDesktop,[switch]$testInteractiveConsole,
    [string]$solution,[string]$configuration
)
$scriptroot = $PSScriptRoot
$repoRoot = Split-Path $scriptroot -Parent
$suffix = $env:FOTU_TEST_RESULTS_SUFFIX
$testResultsDir = Join-Path $repoRoot "artifacts\TestResults\$configuration-$suffix"
$logDir = Join-Path $repoRoot "artifacts\log\$configuration-$suffix"
function Write-FakeRunTestsLog([string]$LogDir) {
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $lines = @(
        "================",
        "Assembly.Fake.UnitTests_0   PASSED   00:01",
        "================",
        "Extra run diagnostics for logging, did not impact run results"
    )
    Set-Content -LiteralPath (Join-Path $LogDir "runtests.log") -Value $lines
}
if ($testCoreClr) {
    New-Item -ItemType Directory -Force -Path $testResultsDir | Out-Null
    Write-FakeRunTestsLog -LogDir $logDir
    exit 0
}
elseif ($testDesktop) {
    New-Item -ItemType Directory -Force -Path $testResultsDir | Out-Null
    Write-FakeRunTestsLog -LogDir $logDir
    exit 0
}
else {
    exit 0
}
'@
    Set-Content -LiteralPath (Join-Path $dir "eng\build.ps1") -Value $mockBuild
    return $dir
}

function New-FalseMarkerTestCase([string]$Name) {
    # A dedicated mock (rather than New-TestCase's) whose runtests.log has a failed test's
    # captured stdout/stderr -- written both before the real summary table (by
    # TestRunner.PrintFailedTestResult) and after it (by Program.LogProcessResultDetails, which
    # dumps every process's raw stdout/stderr post-Print()) -- coincidentally containing its own
    # "================" pairs on both sides, to prove the summary reader anchors on the fixed
    # footer line rather than assuming the real table is the first or last pair in the file.
    $dir = Join-Path $workRoot $Name
    Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $dir "eng") | Out-Null
    Copy-Item -LiteralPath $follyPs1 -Destination (Join-Path $dir "folly.ps1")
    $mockBuild = @'
param(
    [switch]$restore,[switch]$build,[switch]$rebuild,[switch]$pack,
    [switch]$testCoreClr,[switch]$testDesktop,[switch]$testInteractiveConsole,
    [string]$solution,[string]$configuration
)
$scriptroot = $PSScriptRoot
$repoRoot = Split-Path $scriptroot -Parent
$suffix = $env:FOTU_TEST_RESULTS_SUFFIX
$testResultsDir = Join-Path $repoRoot "artifacts\TestResults\$configuration-$suffix"
$logDir = Join-Path $repoRoot "artifacts\log\$configuration-$suffix"
if ($testCoreClr) {
    New-Item -ItemType Directory -Force -Path $testResultsDir | Out-Null
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $lines = @(
        "Errors Assembly.CoreClr.UnitTests_0",
        "some test printed a divider as part of its own diagnostic output:",
        "================",
        "unrelated captured text that happens to be between two stray markers",
        "================",
        "Command: dotnet test ...",
        "================",
        "Assembly.CoreClr.UnitTests_0                                                FAILED       00:34    ",
        "Assembly.CoreClr.UnitTests_1                                                PASSED       00:12    ",
        "================",
        "Extra run diagnostics for logging, did not impact run results",
        "### Begin logging executed process details",
        "### Standard Output",
        "================",
        "raw xunit console output that also happens to contain a divider line",
        "================",
        "### End logging executed process details"
    )
    Set-Content -LiteralPath (Join-Path $logDir "runtests.log") -Value $lines
    exit 1
}
else { exit 0 }
'@
    Set-Content -LiteralPath (Join-Path $dir "eng\build.ps1") -Value $mockBuild
    return $dir
}

function Invoke-Folly([string]$Dir, [string[]]$FollyArgs) {
    $output = & $pwshExe -NoProfile -File (Join-Path $Dir "folly.ps1") @FollyArgs 2>&1 | Out-String
    return [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
}

try {
    # --- default: both legs run ---
    $dir = New-TestCase "default"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry")
    if ($result.ExitCode -eq 0 -and $result.Output -match "CoreCLR: 1 passed" -and $result.Output -match "Desktop: 1 passed") {
        Test-Pass "default 'scry' runs both CoreCLR and Desktop"
    }
    else {
        Test-Fail "default 'scry' (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --core only ---
    $dir = New-TestCase "core-only"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "--core")
    if ($result.ExitCode -eq 0 -and $result.Output -match "CoreCLR: 1 passed" -and $result.Output -notmatch "Desktop:") {
        Test-Pass "'scry --core' runs only CoreCLR"
    }
    else {
        Test-Fail "'scry --core' (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --desktop only ---
    $dir = New-TestCase "desktop-only"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "--desktop")
    if ($result.ExitCode -eq 0 -and $result.Output -match "Desktop: 1 passed" -and $result.Output -notmatch "CoreCLR:") {
        Test-Pass "'scry --desktop' runs only Desktop"
    }
    else {
        Test-Fail "'scry --desktop' (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- positional [config] alongside a selector ---
    $dir = New-TestCase "positional-config"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "truth", "--core")
    if ($result.ExitCode -eq 0 -and $result.Output -match "Release-CoreClr") {
        Test-Pass "positional 'scry truth --core' selects Release"
    }
    else {
        Test-Fail "positional config (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- named -config (backward compat with the pre-existing invocation style) ---
    $dir = New-TestCase "named-config"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("-action", "scry", "-config", "truth", "--core")
    if ($result.ExitCode -eq 0 -and $result.Output -match "Release-CoreClr") {
        Test-Pass "named '-action scry -config truth --core' selects Release"
    }
    else {
        Test-Fail "named config (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- rejected argument ---
    $dir = New-TestCase "rejected-arg"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--bogus")
    if ($result.ExitCode -eq 1 -and $result.Output -match "Unrecognised argument") {
        Test-Pass "unknown argument is rejected"
    }
    else {
        Test-Fail "rejected argument (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- stray "================" lines in captured failure output don't fool the parser ---
    $dir = New-FalseMarkerTestCase "false-marker"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "--core")
    if ($result.ExitCode -eq 1 -and $result.Output -match "CoreCLR: 1 passed, 1 failed") {
        Test-Pass "stray markers in captured failure output are not mistaken for the summary table"
    }
    else {
        Test-Fail "false-marker log (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --core/--desktop rejected for non-scry actions ---
    $dir = New-TestCase "selector-on-non-scry"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "--desktop")
    if ($result.ExitCode -eq 1 -and $result.Output -match "only valid with the 'scry' action") {
        Test-Pass "'--desktop' is rejected on a non-scry action"
    }
    else {
        Test-Fail "selector on non-scry action (exit=$($result.ExitCode)): $($result.Output)"
    }

    Write-Host ""
    Write-Host "$($script:passCount) passed, $($script:failCount) failed"
    if ($script:failCount -gt 0) {
        exit 1
    }
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $workRoot -ErrorAction SilentlyContinue
}
