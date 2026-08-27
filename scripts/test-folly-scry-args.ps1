# Regression test for folly.ps1 scry's argument parsing (action, [config], --core/--framework,
# --timeout) and its unified test summary, run against a mocked eng/build.ps1 so no real build/test
# happens.
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
    # writes the pass-specific runtestsCore.log/runtestsFramework.log RunTests now emits (see
    # Program.WriteLogFile) with exactly one PASSED row so folly.ps1's summary reader has
    # something real to parse, without running any actual build or tests.
    $mockBuild = @'
param(
    [switch]$restore,[switch]$build,[switch]$rebuild,[switch]$pack,
    [switch]$testCoreClr,[switch]$testDesktop,[switch]$testInteractiveConsole,
    [int]$testTimeout,
    [string]$solution,[string]$configuration,
    [switch]$binaryLog,[string]$verbosity
)
$scriptroot = $PSScriptRoot
$repoRoot = Split-Path $scriptroot -Parent
$suffix = $env:FOTU_TEST_RESULTS_SUFFIX
$testResultsDir = Join-Path $repoRoot "artifacts\TestResults\$configuration-$suffix"
$logDir = Join-Path $repoRoot "artifacts\log\$configuration-$suffix"
# Records the -testTimeout value this mock actually received, so the test harness can assert
# folly.ps1 forwarded the value the caller asked for instead of silently dropping it.
Add-Content -LiteralPath (Join-Path $repoRoot "testTimeout-received.log") -Value "$suffix=$testTimeout"
# Same idea for -binaryLog/-verbosity: records what this mock actually received so the harness can
# assert folly.ps1 forwarded them unchanged, without a $suffix qualifier since these aren't per-leg.
Add-Content -LiteralPath (Join-Path $repoRoot "buildArgs-received.log") -Value "binaryLog=$binaryLog verbosity=$verbosity"
function Write-FakeRunTestsLog([string]$LogDir, [string]$LogFileName) {
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $lines = @(
        "================",
        "Assembly.Fake.UnitTests_0   PASSED   00:01",
        "================",
        "Extra run diagnostics for logging, did not impact run results"
    )
    Set-Content -LiteralPath (Join-Path $LogDir $LogFileName) -Value $lines
}
if ($testCoreClr) {
    New-Item -ItemType Directory -Force -Path $testResultsDir | Out-Null
    Write-FakeRunTestsLog -LogDir $logDir -LogFileName "runtestsCore.log"
    exit 0
}
elseif ($testDesktop) {
    New-Item -ItemType Directory -Force -Path $testResultsDir | Out-Null
    Write-FakeRunTestsLog -LogDir $logDir -LogFileName "runtestsFramework.log"
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
    # A dedicated mock (rather than New-TestCase's) whose runtestsCore.log has a failed test's
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
    [int]$testTimeout,
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
    Set-Content -LiteralPath (Join-Path $logDir "runtestsCore.log") -Value $lines
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
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research")
    if ($result.ExitCode -eq 0 -and $result.Output -match "Core: 1 passed" -and $result.Output -match "Framework: 1 passed") {
        Test-Pass "default 'scry' runs both Core and Framework"
    }
    else {
        Test-Fail "default 'scry' (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --core only ---
    $dir = New-TestCase "core-only"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--core")
    if ($result.ExitCode -eq 0 -and $result.Output -match "Core: 1 passed" -and $result.Output -notmatch "Framework:") {
        Test-Pass "'scry --core' runs only Core"
    }
    else {
        Test-Fail "'scry --core' (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --framework only ---
    $dir = New-TestCase "framework-only"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--framework")
    if ($result.ExitCode -eq 0 -and $result.Output -match "Framework: 1 passed" -and $result.Output -notmatch "Core:") {
        Test-Pass "'scry --framework' runs only Framework"
    }
    else {
        Test-Fail "'scry --framework' (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- positional [config] alongside a selector ---
    $dir = New-TestCase "positional-config"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "truth", "--core")
    if ($result.ExitCode -eq 0 -and $result.Output -match "Release-Core") {
        Test-Pass "positional 'scry truth --core' selects Release"
    }
    else {
        Test-Fail "positional config (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- named -config (backward compat with the pre-existing invocation style) ---
    $dir = New-TestCase "named-config"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("-action", "scry", "-config", "truth", "--core")
    if ($result.ExitCode -eq 0 -and $result.Output -match "Release-Core") {
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
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--core")
    if ($result.ExitCode -eq 1 -and $result.Output -match "Core: 1 passed, 1 failed") {
        Test-Pass "stray markers in captured failure output are not mistaken for the summary table"
    }
    else {
        Test-Fail "false-marker log (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --core/--framework rejected for non-scry actions ---
    $dir = New-TestCase "selector-on-non-scry"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "--framework")
    if ($result.ExitCode -eq 1 -and $result.Output -match "only valid with the 'scry' action") {
        Test-Pass "'--framework' is rejected on a non-scry action"
    }
    else {
        Test-Fail "selector on non-scry action (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --timeout is actually forwarded to eng/build.ps1 for both legs ---
    $dir = New-TestCase "timeout-forwarded"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--timeout", "180")
    $receivedPath = Join-Path $dir "testTimeout-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "Core=180" -and $received -match "Framework=180") {
        Test-Pass "'--timeout 180' is forwarded to eng/build.ps1 for both legs"
    }
    else {
        Test-Fail "timeout forwarding (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
    }

    # --- --timeout with a missing value is rejected ---
    $dir = New-TestCase "timeout-missing-value"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "--timeout")
    if ($result.ExitCode -eq 1 -and $result.Output -match "requires a") {
        Test-Pass "'--timeout' with no value is rejected"
    }
    else {
        Test-Fail "timeout missing value (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --timeout with a non-numeric/non-positive value is rejected ---
    $dir = New-TestCase "timeout-invalid-value"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "--timeout", "banana")
    if ($result.ExitCode -eq 1 -and $result.Output -match "positive integer minute count") {
        Test-Pass "'--timeout banana' is rejected"
    }
    else {
        Test-Fail "timeout invalid value (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --timeout rejected for non-scry actions ---
    $dir = New-TestCase "timeout-on-non-scry"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "--timeout", "180")
    if ($result.ExitCode -eq 1 -and $result.Output -match "only valid with the 'scry' action") {
        Test-Pass "'--timeout' is rejected on a non-scry action"
    }
    else {
        Test-Fail "timeout on non-scry action (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --timeout exceeding Task.Delay's supported maximum is rejected before the initial build ---
    $dir = New-TestCase "timeout-exceeds-task-delay-max"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "--timeout", "100000")
    if ($result.ExitCode -eq 1 -and $result.Output -match "71582") {
        Test-Pass "'--timeout 100000' (exceeds Task.Delay's supported maximum) is rejected"
    }
    else {
        Test-Fail "timeout exceeds Task.Delay max (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --binaryLog is forwarded to eng/build.ps1 ---
    $dir = New-TestCase "binarylog-forwarded"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "research", "--binaryLog")
    $receivedPath = Join-Path $dir "buildArgs-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "binaryLog=True") {
        Test-Pass "'--binaryLog' is forwarded to eng/build.ps1"
    }
    else {
        Test-Fail "binaryLog forwarding (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
    }

    # --- -bl is accepted as a short alias, forwarded as the long form ---
    $dir = New-TestCase "binarylog-short-alias"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "research", "-bl")
    $receivedPath = Join-Path $dir "buildArgs-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "binaryLog=True") {
        Test-Pass "'-bl' is forwarded to eng/build.ps1 as -binaryLog"
    }
    else {
        Test-Fail "binaryLog short alias (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
    }

    # --- --verbosity is forwarded to eng/build.ps1 with its value ---
    $dir = New-TestCase "verbosity-forwarded"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--verbosity", "diag")
    $receivedPath = Join-Path $dir "buildArgs-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "verbosity=diag") {
        Test-Pass "'--verbosity diag' is forwarded to eng/build.ps1"
    }
    else {
        Test-Fail "verbosity forwarding (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
    }

    # --- --verbosity with a missing value is rejected ---
    $dir = New-TestCase "verbosity-missing-value"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "--verbosity")
    if ($result.ExitCode -eq 1 -and $result.Output -match "requires a value") {
        Test-Pass "'--verbosity' with no value is rejected"
    }
    else {
        Test-Fail "verbosity missing value (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --binaryLog is rejected on 'cleanse' ---
    $dir = New-TestCase "binarylog-on-cleanse"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("cleanse", "--binaryLog")
    if ($result.ExitCode -eq 1 -and $result.Output -match "aren't valid with 'cleanse'") {
        Test-Pass "'--binaryLog' is rejected on 'cleanse'"
    }
    else {
        Test-Fail "binaryLog on cleanse (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --verbosity is rejected alongside 'scry reflection' ---
    $dir = New-TestCase "verbosity-on-reflection"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "reflection", "--verbosity", "diag")
    if ($result.ExitCode -eq 1 -and $result.Output -match "doesn't take '--binaryLog'/'--verbosity'") {
        Test-Pass "'--verbosity' is rejected alongside 'scry reflection'"
    }
    else {
        Test-Fail "verbosity on reflection (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- 'reflection' is rejected on a non-scry action ---
    $dir = New-TestCase "reflection-non-scry"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "reflection")
    if ($result.ExitCode -eq 1 -and $result.Output -match "only valid with the 'scry' action") {
        Test-Pass "'reflection' is rejected on a non-scry action"
    }
    else {
        Test-Fail "reflection on non-scry action (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- 'reflection' rejects a [config] alongside it ---
    $dir = New-TestCase "reflection-with-config"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "reflection", "truth")
    if ($result.ExitCode -eq 1 -and $result.Output -match "doesn't take a \[config\]") {
        Test-Pass "'reflection' rejects a [config] alongside it"
    }
    else {
        Test-Fail "reflection with config (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- 'reflection' rejects '--timeout' alongside it ---
    $dir = New-TestCase "reflection-with-timeout"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "reflection", "--timeout", "5")
    if ($result.ExitCode -eq 1 -and $result.Output -match "doesn't take '--core'/'--framework'/'--timeout'") {
        Test-Pass "'reflection' rejects '--timeout' alongside it"
    }
    else {
        Test-Fail "reflection with timeout (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- 'scry reflection' runs folly's own test harnesses instead of the (mocked) build ---
    $dir = New-TestCase "reflection-runs-harnesses"
    New-Item -ItemType Directory -Force -Path (Join-Path $dir "scripts") | Out-Null
    Set-Content -LiteralPath (Join-Path $dir "scripts\test-folly-cleanse.ps1") -Value 'Write-Host "cleanse harness ran"; exit 0'
    Set-Content -LiteralPath (Join-Path $dir "scripts\test-folly-scry-args.ps1") -Value 'Write-Host "scry-args harness ran"; exit 0'
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "reflection")
    $buildRan = Test-Path -LiteralPath (Join-Path $dir "testTimeout-received.log")
    if ($result.ExitCode -eq 0 -and $result.Output -match "cleanse harness ran" -and $result.Output -match "scry-args harness ran" -and -not $buildRan) {
        Test-Pass "'scry reflection' runs both harnesses instead of building"
    }
    else {
        Test-Fail "scry reflection runs harnesses (exit=$($result.ExitCode)): $($result.Output)"
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
