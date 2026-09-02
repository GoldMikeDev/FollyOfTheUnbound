# Regression test for folly.ps1's argument parsing (action, primary arg, --core/--framework,
# --timeout) and its unified test summary, plus --binaryLog and --verbosity (forwarded across every
# build-invoking action and rejected on 'cleanse' and 'scry reflection'), run against a mocked
# eng/build.ps1 so no real build/test happens.
# Run by hand (or wire into CI) after touching folly.ps1's argument parsing or scry action:
#   pwsh -File ./scripts/test-folly-scry-args.ps1
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $PSScriptRoot
$follyPs1 = Join-Path $scriptRoot "folly.ps1"
$pwshExe = (Get-Process -Id $PID).Path
# folly.ps1 itself only runs Framework tests (and only defaults to running them) on an actual
# Windows host -- this harness runs on whatever host it's invoked from, so its own expectations for
# the Framework-touching cases below must follow the same host check, not assume Windows.
$onWindows = if (Test-Path variable:IsWindows) { $IsWindows } else { $true }

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
    [switch]$testSuppressConsoleSummary,
    [switch]$testCompilerOnly,[string]$testFilter,[switch]$testIOperation,
    [switch]$collectDumps,
    [switch]$bootstrap,[string]$bootstrapDir,
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
# Records whether -testSuppressConsoleSummary was passed for this pass, so the harness can assert
# folly.ps1 only passes it when running both legs together (never for a single-leg run).
Add-Content -LiteralPath (Join-Path $repoRoot "testSuppressConsoleSummary-received.log") -Value "$suffix=$testSuppressConsoleSummary"
# Same idea for -binaryLog/-verbosity: records what this mock actually received so the harness can
# assert folly.ps1 forwarded them unchanged, without a $suffix qualifier since these aren't per-leg.
Add-Content -LiteralPath (Join-Path $repoRoot "buildArgs-received.log") -Value "binaryLog=$binaryLog verbosity=$verbosity"
# Same idea for -testCompilerOnly/-testFilter/-testIOperation.
Add-Content -LiteralPath (Join-Path $repoRoot "testArgs-received.log") -Value "$suffix testCompilerOnly=$testCompilerOnly testFilter=$testFilter testIOperation=$testIOperation"
# Same idea for -collectDumps: unlike -testIOperation, folly.ps1's own --collectDumps is opt-in
# (mutates a machine-wide WER registry key and its timeout-dump path can capture unrelated
# processes, so it isn't safe as scry's silent default) -- this confirms it's forwarded only when
# requested, not dropped silently either way. An unrelated plain param() script would ignore an
# undeclared switch like this one without erroring, so declaring and logging it here is the only way
# this harness can tell "not forwarded" apart from "forwarded as false".
Add-Content -LiteralPath (Join-Path $repoRoot "collectDumps-received.log") -Value "$suffix collectDumps=$collectDumps"
# Same idea for -bootstrap/-bootstrapDir, appended once per invocation (not per-leg-suffixed) so the
# harness can see the initial build call's plain -bootstrap alongside each leg's -bootstrapDir reuse.
Add-Content -LiteralPath (Join-Path $repoRoot "bootstrapArgs-received.log") -Value "testCoreClr=$testCoreClr testDesktop=$testDesktop bootstrap=$bootstrap bootstrapDir=$bootstrapDir"
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
    [switch]$testSuppressConsoleSummary,
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
    # --- default: both legs run on Windows, Core-only elsewhere ---
    $dir = New-TestCase "default"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research")
    if ($onWindows) {
        if ($result.ExitCode -eq 0 -and $result.Output -match "Core: 1 passed" -and $result.Output -match "Framework: 1 passed") {
            Test-Pass "default 'scry' runs both Core and Framework on Windows"
        }
        else {
            Test-Fail "default 'scry' on Windows (exit=$($result.ExitCode)): $($result.Output)"
        }
    }
    else {
        if ($result.ExitCode -eq 0 -and $result.Output -match "Core: 1 passed" -and $result.Output -notmatch "Framework:") {
            Test-Pass "default 'scry' runs Core only off-Windows"
        }
        else {
            Test-Fail "default 'scry' off-Windows (exit=$($result.ExitCode)): $($result.Output)"
        }
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
    if ($onWindows) {
        if ($result.ExitCode -eq 0 -and $result.Output -match "Framework: 1 passed" -and $result.Output -notmatch "Core:") {
            Test-Pass "'scry --framework' runs only Framework"
        }
        else {
            Test-Fail "'scry --framework' (exit=$($result.ExitCode)): $($result.Output)"
        }
    }
    else {
        if ($result.ExitCode -eq 1 -and $result.Output -match "requires a Windows host") {
            Test-Pass "'scry --framework' is rejected off-Windows"
        }
        else {
            Test-Fail "'scry --framework' off-Windows (exit=$($result.ExitCode)): $($result.Output)"
        }
    }

    # --- -testSuppressConsoleSummary is only passed when both legs run together ---
    # Default 'scry' only runs both Core and Framework legs on an actual Windows host (Framework is
    # rejected/skipped elsewhere off-Windows -- see the $onWindows-gated '--framework' cases above),
    # so this expectation must follow the same host check.
    $dir = New-TestCase "suppress-summary-both"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research")
    $receivedPath = Join-Path $dir "testSuppressConsoleSummary-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($onWindows) {
        if ($result.ExitCode -eq 0 -and $received -match "Core=True" -and $received -match "Framework=True") {
            Test-Pass "default 'scry' (both legs) passes -testSuppressConsoleSummary to each leg"
        }
        else {
            Test-Fail "suppress-summary both-legs (exit=$($result.ExitCode)): received='$received'"
        }
    }
    else {
        if ($result.ExitCode -eq 0 -and $received -match "Core=False" -and $received -notmatch "Framework=") {
            Test-Pass "default 'scry' (Core-only off-Windows) does not pass -testSuppressConsoleSummary"
        }
        else {
            Test-Fail "suppress-summary off-Windows default (exit=$($result.ExitCode)): received='$received'"
        }
    }

    $dir = New-TestCase "suppress-summary-core-only"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--core")
    $receivedPath = Join-Path $dir "testSuppressConsoleSummary-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "Core=False") {
        Test-Pass "'scry --core' (single leg) does not pass -testSuppressConsoleSummary"
    }
    else {
        Test-Fail "suppress-summary core-only (exit=$($result.ExitCode)): received='$received'"
    }

    # --- both legs, unequal name-column widths: combined tables realign to a shared Status/Elapsed
    # column position (see folly.ps1's $resultRowPattern realignment block) instead of each leg's
    # already-formatted table keeping its own leg-local width (TestRunner.Print sizes each leg's
    # name column to that leg's own longest name, so a longer Framework name would otherwise push
    # its Status/Elapsed columns further right than Core's). Only exercisable where both legs
    # actually run -- an actual Windows host -- same as the -testSuppressConsoleSummary case above.
    if ($onWindows) {
        function Format-CenterPad([string]$Text, [int]$Width) {
            $pad = $Width - $Text.Length
            $left = [Math]::Floor($pad / 2)
            $right = $pad - $left
            return (" " * $left) + $Text + (" " * $right)
        }
        $dir = Join-Path $workRoot "realign-unequal-widths"
        Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path (Join-Path $dir "eng") | Out-Null
        Copy-Item -LiteralPath $follyPs1 -Destination (Join-Path $dir "folly.ps1")
        $coreName = "Short.Fake.UnitTests_0"
        $frameworkName = "Very.Long.Namespace.That.Pushes.Well.Past.The.Seventyfive.Character.Floor.Fake.UnitTests_0"
        $coreWidth = 75
        $frameworkWidth = $frameworkName.Length
        $coreRow = $coreName.PadRight($coreWidth) + " " + (Format-CenterPad "PASSED" 10) + " " + (Format-CenterPad "00:01" 10)
        $frameworkRow = $frameworkName.PadRight($frameworkWidth) + " " + (Format-CenterPad "PASSED" 10) + " " + (Format-CenterPad "00:02" 10)
        $mockBuild = @"
param(
    [switch]`$restore,[switch]`$build,[switch]`$testCoreClr,[switch]`$testDesktop,
    [switch]`$testInteractiveConsole,[switch]`$testSuppressConsoleSummary,
    [int]`$testTimeout,[string]`$solution,[string]`$configuration
)
`$scriptroot = `$PSScriptRoot
`$repoRoot = Split-Path `$scriptroot -Parent
`$suffix = `$env:FOTU_TEST_RESULTS_SUFFIX
`$logDir = Join-Path `$repoRoot "artifacts\log\`$configuration-`$suffix"
if (`$testCoreClr) {
    New-Item -ItemType Directory -Force -Path `$logDir | Out-Null
    Set-Content -LiteralPath (Join-Path `$logDir "runtestsCore.log") -Value @("================", "$coreRow", "================", "Extra run diagnostics for logging, did not impact run results")
    exit 0
}
elseif (`$testDesktop) {
    New-Item -ItemType Directory -Force -Path `$logDir | Out-Null
    Set-Content -LiteralPath (Join-Path `$logDir "runtestsFramework.log") -Value @("================", "$frameworkRow", "================", "Extra run diagnostics for logging, did not impact run results")
    exit 0
}
else { exit 0 }
"@
        Set-Content -LiteralPath (Join-Path $dir "eng\build.ps1") -Value $mockBuild
        $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research")
        $coreLine = ($result.Output -split "`r?`n") | Where-Object { $_ -like "*$coreName*" } | Select-Object -First 1
        $frameworkLine = ($result.Output -split "`r?`n") | Where-Object { $_ -like "*$frameworkName*" } | Select-Object -First 1
        $coreStatusCol = if ($coreLine) { $coreLine.IndexOf("PASSED") } else { -1 }
        $frameworkStatusCol = if ($frameworkLine) { $frameworkLine.IndexOf("PASSED") } else { -1 }
        # Not just equal to each other: pinned to the exact absolute offset TestRunner.Print's own
        # formatting recipe implies -- $frameworkWidth, then the single-space ColumnGap, then
        # Format-CenterPad's own 2-space left-pad for a 6-character word ("PASSED"/"FAILED") in a
        # 10-wide field -- so a bug that drops the ColumnGap (shifting both legs left by the same one
        # column) can't pass just because both legs shifted identically.
        $expectedStatusCol = $frameworkWidth + 1 + 2
        if ($result.ExitCode -eq 0 -and $coreStatusCol -eq $expectedStatusCol -and $frameworkStatusCol -eq $expectedStatusCol) {
            Test-Pass "combined Core/Framework tables realign to the same, correctly-offset Status column despite unequal name widths"
        }
        else {
            Test-Fail "realign-unequal-widths (exit=$($result.ExitCode)): core_col=$coreStatusCol framework_col=$frameworkStatusCol expected_col=$expectedStatusCol output=$($result.Output)"
        }
    }

    # --- positional primary arg alongside a selector ---
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

    # --- --testCompilerOnly/--testFilter are forwarded to eng/build.ps1 for each requested leg ---
    $dir = New-TestCase "test-args-forwarded"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--core", "--testCompilerOnly", "--testFilter", "FullyQualifiedName~Foo")
    $receivedPath = Join-Path $dir "testArgs-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "Core testCompilerOnly=True testFilter=FullyQualifiedName~Foo") {
        Test-Pass "'--testCompilerOnly'/'--testFilter' are forwarded to .\eng\build.ps1"
    }
    else {
        Test-Fail "testCompilerOnly/testFilter forwarding (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
    }

    # --- --testFilter with a missing value is rejected ---
    $dir = New-TestCase "test-filter-missing-value"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "--testFilter")
    if ($result.ExitCode -eq 1 -and $result.Output -match "requires a value") {
        Test-Pass "'--testFilter' with no value is rejected"
    }
    else {
        Test-Fail "testFilter missing value (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --testCompilerOnly/--testFilter rejected for non-scry actions ---
    $dir = New-TestCase "test-args-on-non-scry"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "--testCompilerOnly")
    if ($result.ExitCode -eq 1 -and $result.Output -match "only valid with the 'scry' action") {
        Test-Pass "'--testCompilerOnly' is rejected on a non-scry action"
    }
    else {
        Test-Fail "testCompilerOnly on non-scry action (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --testIOperation is forwarded to eng/build.ps1 for each requested leg ---
    $dir = New-TestCase "test-ioperation-forwarded"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--core", "--testIOperation")
    $receivedPath = Join-Path $dir "testArgs-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "Core testCompilerOnly=False testFilter= testIOperation=True") {
        Test-Pass "'--testIOperation' is forwarded to .\eng\build.ps1"
    }
    else {
        Test-Fail "testIOperation forwarding (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
    }

    # --- --testIOperation rejected for non-scry actions ---
    $dir = New-TestCase "test-ioperation-on-non-scry"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "--testIOperation")
    if ($result.ExitCode -eq 1 -and $result.Output -match "only valid with the 'scry' action") {
        Test-Pass "'--testIOperation' is rejected on a non-scry action"
    }
    else {
        Test-Fail "testIOperation on non-scry action (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- '--collectDumps' is opt-in: absent by default, not forwarded to eng/build.ps1's test call ---
    $dir = New-TestCase "collectdumps-not-forwarded-by-default"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--core")
    $receivedPath = Join-Path $dir "collectDumps-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "Core collectDumps=False") {
        Test-Pass "'scry' does not forward '-collectDumps' to .\eng\build.ps1 by default"
    }
    else {
        Test-Fail "collectDumps default (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
    }

    # --- '--collectDumps' is forwarded when explicitly requested ---
    $dir = New-TestCase "collectdumps-forwarded-when-requested"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--core", "--collectDumps")
    $receivedPath = Join-Path $dir "collectDumps-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "Core collectDumps=True") {
        Test-Pass "'--collectDumps' is forwarded to .\eng\build.ps1 when requested"
    }
    else {
        Test-Fail "collectDumps forwarding (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
    }

    # --- '--collectDumps' rejected for non-scry actions ---
    $dir = New-TestCase "collectdumps-on-non-scry"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "--collectDumps")
    if ($result.ExitCode -eq 1 -and $result.Output -match "only valid with the 'scry' action") {
        Test-Pass "'--collectDumps' is rejected on a non-scry action"
    }
    else {
        Test-Fail "collectDumps on non-scry action (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --bootstrap: the initial build call gets -bootstrap, the test leg gets -bootstrapDir
    # pointing at the same deterministic artifacts\bootstrap\build dir instead of rebuilding it ---
    $dir = New-TestCase "bootstrap-forwarded"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--core", "--bootstrap")
    $receivedPath = Join-Path $dir "bootstrapArgs-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath } else { @() }
    $buildLine = $received | Where-Object { $_ -match "^testCoreClr=False testDesktop=False" }
    $legLine = $received | Where-Object { $_ -match "^testCoreClr=True" }
    $expectedBootstrapDir = Join-Path (Join-Path $dir "artifacts") "bootstrap\build"
    if ($result.ExitCode -eq 0 -and $buildLine -match "bootstrap=True bootstrapDir=$" `
        -and $legLine -and $legLine -match [regex]::Escape("bootstrap=False bootstrapDir=$expectedBootstrapDir")) {
        Test-Pass "'--bootstrap' builds once and is reused via -bootstrapDir for the test leg"
    }
    else {
        Test-Fail "bootstrap forwarding (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
    }

    # --- --bootstrap is rejected on 'cleanse' ---
    $dir = New-TestCase "bootstrap-on-cleanse"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("cleanse", "--bootstrap")
    if ($result.ExitCode -eq 1 -and $result.Output -match "aren't valid with 'cleanse'") {
        Test-Pass "'--bootstrap' is rejected on 'cleanse'"
    }
    else {
        Test-Fail "bootstrap on cleanse (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --bootstrap is forwarded on a non-scry action too (not scoped to 'scry') ---
    $dir = New-TestCase "bootstrap-on-weave"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "research", "--bootstrap")
    $receivedPath = Join-Path $dir "bootstrapArgs-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "bootstrap=True") {
        Test-Pass "'--bootstrap' is forwarded to .\eng\build.ps1 on 'weave'"
    }
    else {
        Test-Fail "bootstrap on weave (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
    }

    # --- --timeout is actually forwarded to eng/build.ps1 for both legs ---
    $dir = New-TestCase "timeout-forwarded"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--timeout", "180")
    $receivedPath = Join-Path $dir "testTimeout-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($onWindows) {
        if ($result.ExitCode -eq 0 -and $received -match "Core=180" -and $received -match "Framework=180") {
            Test-Pass "'--timeout 180' is forwarded to .\eng\build.ps1 for both legs"
        }
        else {
            Test-Fail "timeout forwarding (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
        }
    }
    else {
        if ($result.ExitCode -eq 0 -and $received -match "Core=180" -and $received -notmatch "Framework=") {
            Test-Pass "'--timeout 180' is forwarded to .\eng\build.ps1 for the Core-only leg"
        }
        else {
            Test-Fail "timeout forwarding off-Windows (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
        }
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
        Test-Pass "'--binaryLog' is forwarded to .\eng\build.ps1"
    }
    else {
        Test-Fail "binaryLog forwarding (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
    }

    # --- --verbosity is forwarded to eng/build.ps1 with its value ---
    $dir = New-TestCase "verbosity-forwarded"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "research", "--verbosity", "diagnostic")
    $receivedPath = Join-Path $dir "buildArgs-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "verbosity=diagnostic") {
        Test-Pass "'--verbosity diagnostic' is forwarded to .\eng\build.ps1"
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

    # --- --verbosity rejects MSBuild's own single-letter/abbreviated shorthand (e.g. 'diag'): full words only ---
    $dir = New-TestCase "verbosity-shorthand-rejected"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "--verbosity", "diag")
    if ($result.ExitCode -eq 1 -and $result.Output -match "requires one of" -and $result.Output -match "Got 'diag'") {
        Test-Pass "'--verbosity diag' (shorthand) is rejected"
    }
    else {
        Test-Fail "verbosity shorthand rejected (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- --verbosity accepts a full word case-insensitively ---
    $dir = New-TestCase "verbosity-case-insensitive"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("weave", "research", "--verbosity", "DIAGNOSTIC")
    $receivedPath = Join-Path $dir "buildArgs-received.log"
    $received = if (Test-Path -LiteralPath $receivedPath) { Get-Content -LiteralPath $receivedPath -Raw } else { "" }
    if ($result.ExitCode -eq 0 -and $received -match "verbosity=DIAGNOSTIC") {
        Test-Pass "'--verbosity DIAGNOSTIC' is accepted case-insensitively"
    }
    else {
        Test-Fail "verbosity case-insensitive (exit=$($result.ExitCode)): received='$received' output=$($result.Output)"
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
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "reflection", "--verbosity", "diagnostic")
    if ($result.ExitCode -eq 1 -and $result.Output -match "doesn't take a primary arg or any switches") {
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

    # --- 'reflection' rejects a primary arg alongside it ---
    $dir = New-TestCase "reflection-with-config"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "reflection", "truth")
    if ($result.ExitCode -eq 1 -and $result.Output -match "doesn't take a primary arg or any switches") {
        Test-Pass "'reflection' rejects a primary arg alongside it"
    }
    else {
        Test-Fail "reflection with config (exit=$($result.ExitCode)): $($result.Output)"
    }

    # --- 'reflection' rejects '--timeout' alongside it ---
    $dir = New-TestCase "reflection-with-timeout"
    $result = Invoke-Folly -Dir $dir -FollyArgs @("scry", "reflection", "--timeout", "5")
    if ($result.ExitCode -eq 1 -and $result.Output -match "doesn't take a primary arg or any switches") {
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
