param
(
	[Parameter(Position = 0)][string]$action,
	[string]$config,  # name-only, not positional: $action above already claimed Position 0, so [config] falls through to $remainingArgs and gets matched by the manual parse below instead
	[parameter(ValueFromRemainingArguments = $true)][string[]]$remainingArgs
)
try {
	[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
	try { [Console]::CursorVisible = $false } catch {}
	$ErrorActionPreference = "Stop"
	$solution = "FollyOfTheUnbound.slnx"
	$buildScript = Join-Path $PSScriptRoot "eng\build.ps1"
	$nupkgRoot = Join-Path $PSScriptRoot "..\.nupkg\FotU"
	# .NET Framework (net472) tests have no cross-platform runtime and only ever run on a genuine
	# Windows host, even though this script itself (pwsh) can run elsewhere -- '--framework' is
	# rejected outright off-Windows, and 'scry's own no-switches default only includes Framework here.
	$onWindows = if (Test-Path variable:IsWindows) { $IsWindows } else { $true }  # $IsWindows only exists on PowerShell Core (6+); Windows PowerShell 5.1 (Desktop edition) has no such variable and only ever runs on Windows anyway
	$core = $false  # PowerShell's automatic binding only recognises single-dash switches, so --core/--framework/--timeout (matching folly.sh's style) are parsed by hand below
	$framework = $false
	$testCompilerOnly = $false
	$testFilter = $null
	$expectTestFilterValue = $false
	$testIOperation = $false
	$bootstrap = $false
	$reflection = $false
	$testTimeout = 0
	$expectTimeoutValue = $false
	$binaryLog = $false
	$verbosity = $null
	$expectVerbosityValue = $false
	foreach ($arg in $remainingArgs) {
		if ($expectTimeoutValue) {
			if (-not [int]::TryParse($arg, [ref]$testTimeout) -or $testTimeout -le 0 -or $testTimeout -gt 71582) {  # 71582 min = Task.Delay's ms ceiling, which Program.RunCoreAsync forwards this straight into
				Write-Host "'--timeout' requires a positive integer minute count, up to 71582 (Task.Delay's supported maximum), got '$arg'." -ForegroundColor Red
				exit 1
			}
			$expectTimeoutValue = $false
		}
		elseif ($expectTestFilterValue) {
			$testFilter = $arg
			$expectTestFilterValue = $false
		}
		elseif ($expectVerbosityValue) {
			if ($arg -notin @("quiet", "minimal", "normal", "detailed", "diagnostic")) {  # full words only, not MSBuild's own q/m/n/d/diag shorthand -- explicit over terse
				Write-Host "'--verbosity' requires one of: quiet, minimal, normal, detailed, diagnostic. Got '$arg'." -ForegroundColor Red
				exit 1
			}
			$verbosity = $arg
			$expectVerbosityValue = $false
		}
		elseif ($arg -eq "--core") {
			$core = $true
		}
		elseif ($arg -eq "--framework") {
			$framework = $true
		}
		elseif ($arg -eq "--testCompilerOnly") {
			$testCompilerOnly = $true
		}
		elseif ($arg -eq "--testFilter") {
			$expectTestFilterValue = $true
		}
		elseif ($arg -eq "--testIOperation") {
			$testIOperation = $true
		}
		elseif ($arg -eq "--bootstrap") {
			$bootstrap = $true
		}
		elseif ($arg -eq "--timeout") {
			$expectTimeoutValue = $true
		}
		elseif ($arg -eq "--binaryLog") {
			$binaryLog = $true
		}
		elseif ($arg -eq "--verbosity") {
			$expectVerbosityValue = $true
		}
		elseif ($arg -eq "reflection") {
			$reflection = $true
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
	if ($expectTestFilterValue) {
		Write-Host "'--testFilter' requires a value." -ForegroundColor Red
		exit 1
	}
	if ($expectVerbosityValue) {
		Write-Host "'--verbosity' requires a value: quiet, minimal, normal, detailed, or diagnostic." -ForegroundColor Red
		exit 1
	}
	if ([string]::IsNullOrEmpty($action) -or $action -eq "grimoire") {
		Write-Host ""
		Write-Host "Commands:"
		Write-Host "    'attune'                                        Restore only."
		Write-Host "    'bind'                                          Restore, build & pack (nupkg files packed to ..\.nupkg\FotU\)."
		Write-Host "    'cleanse'                                       Delete artefacts."
		Write-Host "    'grimoire'                                      Show this text (default when no action is given)."
		Write-Host "    'reweave'                                       Restore & rebuild."
		Write-Host "    'scry'                                          Restore, build & run Core (and, on Windows, Framework) unit tests."
		Write-Host "    'weave'                                         Restore & build."
		Write-Host "Primary args:"
		Write-Host "    '<scry> reflection'                             Runs folly script test harnesses."
		Write-Host "    '<command> research [switches]'                 Debug configuration."
		Write-Host "    '<command> truth [switches]'                    Release configuration."
		Write-Host "Switches:"
		Write-Host "    '<scry> <primary> --core'                       Run only the Core tests (skip Framework)."
		Write-Host "    '<scry> <primary> --framework'                  Run only the Framework tests (skip Core; Windows only)."
		Write-Host "    '<scry> <primary> --testCompilerOnly'           Run only the compiler unit test assemblies."
		Write-Host "    '<scry> <primary> --testFilter <xunit filter>'  Filter tests to run, e.g. FullyQualifiedName~TestClass1|Category=CategoryA."
		Write-Host "    '<scry> <primary> --testIOperation'             Run tests with the IOperation test hook enabled."
		Write-Host "    '<scry> <primary> --timeout <minutes>'          Override RunTests' whole-run watchdog (default: 90)."
		Write-Host "    '<command> <primary> --binaryLog'               Write MSBuild binary log to .\artifacts\log\<config>\Build.binlog."
		Write-Host "    '<command> <primary> --verbosity <level>'       MSBuild verbosity: quiet, minimal, normal, detailed, diagnostic."
		Write-Host "    '<command> <primary> --bootstrap'               Build/test using a locally-built bootstrap compiler (not 'cleanse')."
		Write-Host ""
		exit 0
	}
	# Every '<selector>'/reflection is scoped to 'scry' -- one combined check/message rather than
	# one per selector, since they're all the same rule applied to different args. '--bootstrap' is
	# NOT scoped to 'scry': like '--binaryLog'/'--verbosity' it's valid on any build-invoking action,
	# just rejected on 'cleanse' below.
	if ($action -ne "scry" -and ($core -or $framework -or $testCompilerOnly -or $testFilter -or $testIOperation -or $testTimeout -gt 0 -or $reflection)) {
		Write-Host "'--core'/'--framework'/'--testCompilerOnly'/'--testFilter'/'--testIOperation'/'--timeout'/'reflection' are only valid with the 'scry' action." -ForegroundColor Red
		exit 1
	}
	if ($framework -and -not $onWindows) {
		Write-Host "'--framework' requires a Windows host (.NET Framework tests have no cross-platform runtime)." -ForegroundColor Red
		exit 1
	}
	# By this point $action -eq "scry" is already guaranteed whenever $reflection is true (the
	# check above would have rejected it otherwise), so this doesn't need to re-check $action itself.
	if ($reflection -and (-not [string]::IsNullOrEmpty($config) -or $core -or $framework -or $testCompilerOnly -or $testFilter -or $testIOperation -or $testTimeout -gt 0 -or $binaryLog -or $verbosity -or $bootstrap)) {
		Write-Host "'reflection' doesn't take a primary arg or any switches -- it runs folly's own test harnesses, not a build/RunTests." -ForegroundColor Red
		exit 1
	}
	if (($binaryLog -or $verbosity -or $bootstrap) -and $action -eq "cleanse") {
		Write-Host "'--binaryLog'/'--verbosity'/'--bootstrap' aren't valid with 'cleanse' -- there's no build to log or bootstrap." -ForegroundColor Red
		exit 1
	}
	if ($action -eq "cleanse" -or ($action -eq "scry" -and $reflection)) {
		$configuration = $null
		$nupkgDir = $null
	}
	elseif ([string]::IsNullOrEmpty($config)) {
		Write-Host "Primary arg is required for action '$action'. Expected 'research' or 'truth'." -ForegroundColor Red
		exit 1
	}
	elseif ($config -eq "research") {
		$configuration = "Debug"
		$nupkgDir = Join-Path $nupkgRoot "Debug"
	}
	elseif ($config -eq "truth") {
		$configuration = "Release"
		$nupkgDir = Join-Path $nupkgRoot "Release"
	}
	else {
		Write-Host "Unrecognised configuration '$config'. Expected 'research' or 'truth'." -ForegroundColor Red
		exit 1
	}
	$extraBuildArgs = @{}  # --binaryLog: forwarded as-is to eng/build.ps1's own -binaryLog/-bl. --verbosity: already restricted to full words above (eng/build.ps1's own -verbosity/-v itself still accepts MSBuild's q/m/n/d/diag shorthand too, but folly.ps1 only ever forwards the validated full-word form here). A hashtable splat, not an array: splatting a bare "-binaryLog" string in an array does NOT bind a [switch] parameter to $true (PowerShell only recognizes that shorthand when the token is typed literally on the command line, not when it arrives via array splatting) -- a hashtable splat maps the parameter name to its value explicitly and works correctly for switches.
	if ($binaryLog) {
		$extraBuildArgs["binaryLog"] = $true
	}
	if ($verbosity) {
		$extraBuildArgs["verbosity"] = $verbosity
	}
	# Passed as a raw MSBuild property (not a named eng/build.ps1 parameter, so via $properties'
	# ValueFromRemainingArguments passthrough, not the $extraBuildArgs splat above) on every build this
	# script runs: eng/build.ps1's BuildSolution invokes MSBuild on Arcade's toolset Build.proj, passing
	# the .slnx only via /p:Projects=..., so the built-in $(SolutionName) is never actually
	# "FollyOfTheUnbound" here -- see the matching comment in Microsoft.CodeAnalysis.Analyzer.Testing.csproj
	# for the RoslynSdk collision this was added to fix.
	$identityArgs = @("/p:FollyOfTheUnboundBuild=true")
	# --bootstrap gets two different forwarded forms, not just one flag folded into $extraBuildArgs
	# above: a "build" invocation (attune/weave/reweave/bind, and scry's own initial restore+build
	# call) passes plain -bootstrap, which builds the bootstrap compiler fresh into a deterministic
	# artifacts\bootstrap\build dir (see eng/build.ps1's own -bootstrapDir handling). scry then runs
	# each requested test leg as its own separate eng/build.ps1 invocation -- passing -bootstrap again
	# there would rebuild it from scratch per leg, so those instead pass -bootstrapDir pointing at the
	# same dir to reuse it.
	$bootstrapBuildArgs = @{}
	$bootstrapTestArgs = @{}
	if ($bootstrap) {
		$bootstrapBuildArgs["bootstrap"] = $true
		$bootstrapTestArgs["bootstrapDir"] = Join-Path (Join-Path $PSScriptRoot "artifacts") "bootstrap\build"
	}
	if ($action -eq "attune") {  # -nodeReuse:$false everywhere below: eng/common/tools.ps1 defaults nodeReuse true locally, leaving MSBuild workers running after exit, still holding DLLs open under artifacts/ (cleanse's build-server shutdown only stops VBCSCompiler/Razor, not these)
		& $buildScript -restore -nodeReuse:$false -solution $solution -configuration $configuration @extraBuildArgs @bootstrapBuildArgs @identityArgs
	}
	elseif ($action -eq "weave") {
		& $buildScript -restore -build -nodeReuse:$false -solution $solution -configuration $configuration @extraBuildArgs @bootstrapBuildArgs @identityArgs
	}
	elseif ($action -eq "reweave") {
		& $buildScript -restore -rebuild -nodeReuse:$false -solution $solution -configuration $configuration @extraBuildArgs @bootstrapBuildArgs @identityArgs
	}
	elseif ($action -eq "bind") {
		& $buildScript -restore -build -pack -nodeReuse:$false -solution $solution -configuration $configuration @extraBuildArgs @bootstrapBuildArgs @identityArgs
	}
	elseif ($action -eq "scry" -and $reflection) {
		$pwshExe = (Get-Process -Id $PID).Path  # a harness's own `exit` would otherwise terminate this process too -- run each in its own child pwsh, same as the harnesses do to folly.ps1 under test
		$harnessFail = $false
		Write-Host ""
		foreach ($harness in @("test-folly-cleanse.ps1", "test-folly-scry-args.ps1")) {
			Write-Host "--- $harness ---"
			& $pwshExe -NoProfile -File (Join-Path $PSScriptRoot "scripts\$harness")
			if ($LASTEXITCODE -ne 0) { $harnessFail = $true }
			Write-Host ""
		}
		exit ($(if ($harnessFail) { 1 } else { 0 }))
	}
	elseif ($action -eq "scry") {
		$runCore = $core -or -not ($core -or $framework)  # default to both (on Windows) when neither switch is given; either switch alone runs just that one
		$runFramework = ($framework -or -not ($core -or $framework)) -and $onWindows  # off-Windows the no-switches default is Core-only; '--framework' itself was already rejected above on a non-Windows host
		$callerMsbuildDebugPath = $env:MSBUILDDEBUGPATH  # captured before the restore/build below sets its own default, or this would snapshot that build-created value instead of "nothing was set"
		& $buildScript -restore -build -nodeReuse:$false -solution $solution -configuration $configuration @extraBuildArgs @bootstrapBuildArgs @identityArgs
		$buildExitCode = $LASTEXITCODE
		if ($buildExitCode -ne 0) {
			exit $buildExitCode
		}
		function Get-TestSummary([string]$LogPath, [string]$Label, [int]$ExitCode) {  # tallies each leg's PASSED/FAILED/TIMEOUT counts -- and keeps the raw table lines -- from its already-logged runtests.log. RunTests always writes this table to the log file regardless of -testSuppressConsoleSummary (only whether it *also* goes to the console is conditional -- see that switch's comment below); the raw Lines rebuild both legs' tables together in the "combined results" block once both have finished, and the tallies drive the compact numeric recap after it.
			$result = [pscustomobject]@{ Label = $Label; Found = $false; Passed = 0; Failed = 0; Timeout = 0; ExitCode = $ExitCode; Lines = @() }  # ExitCode carried through unchanged so a work item that threw before producing a TestResult still marks this leg red
			if (-not (Test-Path -LiteralPath $LogPath)) {
				return $result
			}
			$footerText = "Extra run diagnostics for logging, did not impact run results"  # anchor on the marker pair immediately before this exact, RunTests-authored footer -- captured test stdout/stderr elsewhere in the log can itself contain a "================" line, so neither the first nor the last marker pair alone is reliable
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
			$lines = [System.Collections.Generic.List[string]]::new()
			$lineNumber = 0
			foreach ($line in [System.IO.File]::ReadLines($LogPath)) {
				$lineNumber++
				if ($lineNumber -le $startLine) {
					continue
				}
				if ($lineNumber -ge $endLine) {
					break
				}
				$lines.Add($line)
				if ($line -match '\bTIMEOUT\b') { $result.Timeout++ }
				elseif ($line -match '\bFAILED\b') { $result.Failed++ }
				elseif ($line -match '\bPASSED\b') { $result.Passed++ }
			}
			$result.Lines = $lines.ToArray()
			$result.Found = $true
			return $result
		}
		$coreTestResultsDir = Join-Path $PSScriptRoot "artifacts\TestResults\$configuration-Core"
		$coreLogDir = Join-Path $PSScriptRoot "artifacts\log\$configuration-Core"
		$frameworkTestResultsDir = Join-Path $PSScriptRoot "artifacts\TestResults\$configuration-Framework"
		$frameworkLogDir = Join-Path $PSScriptRoot "artifacts\log\$configuration-Framework"
		$msbuildDebugPath = Join-Path $PSScriptRoot "artifacts\log\$configuration\MsbuildDebugLogs"  # matches eng/common/tools.ps1's own (unsuffixed) $LogDir\MsbuildDebugLogs convention
		Remove-Item -Recurse -Force -LiteralPath $coreTestResultsDir -ErrorAction SilentlyContinue
		Remove-Item -Recurse -Force -LiteralPath $coreLogDir -ErrorAction SilentlyContinue
		Remove-Item -Recurse -Force -LiteralPath $frameworkTestResultsDir -ErrorAction SilentlyContinue
		Remove-Item -Recurse -Force -LiteralPath $frameworkLogDir -ErrorAction SilentlyContinue
		# -testInteractiveConsole lets RunTests inherit the real console directly: its live per-work-item
		# progress table (LiveTestProgressDisplay, drawn into the terminal's alternate screen buffer -- see
		# src/Tools/RunTests/LiveTestProgressDisplay.cs) goes straight to the terminal, bypassing PowerShell's
		# pipeline entirely. This is passed unconditionally, for both legs, always, and neither leg's output
		# is ever piped -- piping a leg's output through the pipeline at all (even just to relay it back out
		# line-by-line) makes Console.IsOutputRedirected true for that child process, a one-way flag
		# TryCreate checks once up front: nothing downstream can "un-redirect" it and get the real redrawn
		# table back, only the plain scrolling fallback RunTests uses outside a real terminal. So each leg's
		# live table always prints live, unpiped, immediately, same as a single-leg run always has.
		#
		# -testSuppressConsoleSummary is different: it doesn't touch the console mode or piping at all, only
		# whether RunTests' own final PASSED/FAILED/TIMEOUT table (src/Tools/RunTests/TestRunner.cs's Print)
		# additionally prints straight to that same real console -- the table is still written to that leg's
		# log file regardless (RunTests logs everything it prints via ConsoleUtil, console-suppressed or
		# not). Passed only when both legs are requested: each leg's own table would otherwise print live the
		# moment that leg finishes, which then gets buried under the *other* leg's own subsequent build/live
		# progress output -- so instead both legs' tables are read back from their log files once everything
		# finishes (Get-TestSummary) and shown together, once, in the "combined results" block below. A
		# single-leg run passes neither leg two logs to combine, so its one table just prints live as normal.
		$bothLegs = $runCore -and $runFramework
		# Same hashtable-splat reasoning as $extraBuildArgs above ([switch]$testCompilerOnly needs a
		# real $true value, not a bare splatted string) -- scoped separately since these two are
		# 'scry'-only, unlike $extraBuildArgs which every build-invoking action forwards.
		$extraTestArgs = @{}
		if ($testCompilerOnly) {
			$extraTestArgs["testCompilerOnly"] = $true
		}
		if ($testFilter) {
			$extraTestArgs["testFilter"] = $testFilter
		}
		if ($testIOperation) {
			$extraTestArgs["testIOperation"] = $true
		}
		$coreExitCode = 0
		if ($runCore) {
			$env:FOTU_TEST_RESULTS_SUFFIX = "Core"
			$env:MSBUILDDEBUGPATH = $msbuildDebugPath  # set explicitly every pass -- tools.ps1 only sets this itself when unset, so only the very first build.ps1 invocation in this process would otherwise ever set it
			try {
				& $buildScript -testCoreClr -testInteractiveConsole -testSuppressConsoleSummary:$bothLegs -nodeReuse:$false -testTimeout $testTimeout -solution $solution -configuration $configuration @extraTestArgs @extraBuildArgs @bootstrapTestArgs @identityArgs
				$coreExitCode = $LASTEXITCODE
			} finally {
				Remove-Item Env:\FOTU_TEST_RESULTS_SUFFIX -ErrorAction SilentlyContinue
				if ($null -eq $callerMsbuildDebugPath) {
					Remove-Item Env:\MSBUILDDEBUGPATH -ErrorAction SilentlyContinue
				} else {
					$env:MSBUILDDEBUGPATH = $callerMsbuildDebugPath
				}
			}
		}
		$frameworkExitCode = 0
		if ($runFramework) {
			$env:FOTU_TEST_RESULTS_SUFFIX = "Framework"
			$env:MSBUILDDEBUGPATH = $msbuildDebugPath
			try {
				& $buildScript -testDesktop -testInteractiveConsole -testSuppressConsoleSummary:$bothLegs -nodeReuse:$false -testTimeout $testTimeout -solution $solution -configuration $configuration @extraTestArgs @extraBuildArgs @bootstrapTestArgs @identityArgs
				$frameworkExitCode = $LASTEXITCODE
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
		if ($runCore) {
			$summaries += Get-TestSummary -LogPath (Join-Path $coreLogDir "runtestsCore.log") -Label "Core" -ExitCode $coreExitCode
		}
		if ($runFramework) {
			$summaries += Get-TestSummary -LogPath (Join-Path $frameworkLogDir "runtestsFramework.log") -Label "Framework" -ExitCode $frameworkExitCode
		}
		$missingSummaries = @($summaries | Where-Object { -not $_.Found })
		$anyLegFailedExitCode = ($runCore -and $coreExitCode -ne 0) -or ($runFramework -and $frameworkExitCode -ne 0)
		$totalPassed = ($summaries | Measure-Object -Property Passed -Sum).Sum
		$totalFailed = ($summaries | Measure-Object -Property Failed -Sum).Sum
		$totalTimeout = ($summaries | Measure-Object -Property Timeout -Sum).Sum
		# Print both legs' own PASSED/FAILED/TIMEOUT tables together here, once every requested leg has
		# finished -- read back from each leg's own runtests*.log, which -testSuppressConsoleSummary above
		# kept out of the console the first time around specifically so this is the only place either leg's
		# table appears, not a second copy of something already shown live. Single-leg runs never pass that
		# switch, so their one table already printed live as normal -- nothing to combine here.
		if ($bothLegs) {
			foreach ($summary in $summaries) {
				Write-Host ""
				Write-Host "=== $($summary.Label) results ===" -ForegroundColor Cyan
				if ($summary.Found) {
					# Colored per-line to match RunTests' own live-console table (TestRunner.Print) and the
					# scry live table (LiveTestProgressDisplay) -- PASSED/FAILED/TIMEOUT are exactly the
					# same three tokens Get-TestSummary above already tallies each line by.
					foreach ($line in $summary.Lines) {
						if ($line -match '\bTIMEOUT\b') {
							Write-Host $line -ForegroundColor Yellow
						}
						elseif ($line -match '\bFAILED\b') {
							Write-Host $line -ForegroundColor Red
						}
						elseif ($line -match '\bPASSED\b') {
							Write-Host $line -ForegroundColor Green
						}
						else {
							Write-Host $line
						}
					}
				}
				else {
					Write-Host "summary unavailable (no runtests.log found)" -ForegroundColor Yellow
				}
			}
		}
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
		$overallSuccess = -not $anyLegFailedExitCode -and $missingSummaries.Count -eq 0 -and $totalFailed -eq 0 -and $totalTimeout -eq 0  # green requires every requested leg to have exited 0 AND produced a readable summary with no failures/timeouts
		$overallColor = if ($overallSuccess) { "Green" } else { "Red" }
		Write-Host "Overall: $totalPassed passed, $totalFailed failed, $totalTimeout timeout" -ForegroundColor $overallColor
		Write-Host ""
		if ($runCore) {
			Write-Host "Core test results: $coreTestResultsDir (logs: $coreLogDir)"
		}
		if ($runFramework) {
			Write-Host "Framework test results: $frameworkTestResultsDir (logs: $frameworkLogDir)"
		}
		if ($coreExitCode -ne 0) {
			exit $coreExitCode
		}
		if ($frameworkExitCode -ne 0) {
			exit $frameworkExitCode
		}
		if (-not $overallSuccess) {
			exit 1  # every requested leg exited 0, but the summary itself says otherwise (e.g. a caught I/O error writing runtests.log) -- don't let that read as success to automation
		}
		exit 0
	}
	elseif ($action -eq "cleanse") {
		$artifactsDir = Join-Path $PSScriptRoot "artifacts"
		$localDotnet = Join-Path $PSScriptRoot ".dotnet\dotnet.exe"  # VBCSCompiler/MSBuild/the Razor server keep DLLs open under artifacts/ between invocations -- shut them down first so cleanse never races a locked file
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
		$onWindows = if (Test-Path variable:IsWindows) { $IsWindows } else { $true }  # $IsWindows only exists on PowerShell Core (6+); Windows PowerShell 5.1 (Desktop edition) has no such variable and only ever runs on Windows anyway
		function Get-ProcessSnapshot {  # Pid/PPid/CommandLine for every process -- Win32_Process (WMI/CIM) is Windows-only and PowerShell Core on Linux/macOS has no CIM server here, so non-Windows falls back to `ps -eo pid,ppid,command`, mirroring folly.sh's own ps-based approach
			if ($onWindows) {
				return Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
					ForEach-Object { [PSCustomObject]@{ Pid = $_.ProcessId; PPid = $_.ParentProcessId; CommandLine = $_.CommandLine } }
			}
			$psExe = Get-Command -Name ps -CommandType Application -ErrorAction SilentlyContinue |  # -CommandType Application resolves the native ps binary specifically, bypassing PowerShell's built-in "ps" alias for Get-Process (which doesn't accept ps's -eo argument and would throw a parameter-binding error under ErrorActionPreference=Stop). Select-Object -First 1: some systems have more than one "ps" on PATH (e.g. both /usr/bin/ps and /bin/ps), and Get-Command returns all matches -- piping straight into & would space-join their .Source paths into one invalid command.
				Select-Object -First 1
			if (-not $psExe) { return @() }
			$lines = & $psExe.Source -eo pid,ppid,command 2>$null
			if (-not $lines) { return @() }
			$lines | Select-Object -Skip 1 | ForEach-Object {
				if ($_ -match '^\s*(\d+)\s+(\d+)\s+(.*)$') {
					[PSCustomObject]@{ Pid = [int]$Matches[1]; PPid = [int]$Matches[2]; CommandLine = $Matches[3] }
				}
			}
		}
		function Get-AncestorPids {  # walks the PPid chain from this script's own process up to and including PID 1 (or as far as it can resolve) -- kill candidates get filtered against this set so cleanse can never terminate its own invoking shell/CI agent, even if that ancestor's command line happens to match the build-server/node-worker patterns (e.g. an automation wrapper that embeds the search text in its own argv). PID 1 is deliberately included, not just excluded as a loop bound: in a container where the invoking CI agent *is* PID 1, leaving it unprotected would make the container's own init process a killable candidate.
			$snapshot = Get-ProcessSnapshot
			$result = New-Object System.Collections.Generic.HashSet[int]
			$current = $PID
			while ($current -and $current -ne 0 -and -not $result.Contains($current)) {
				[void]$result.Add($current)
				if ($current -eq 1) { break }
				$proc = $snapshot | Where-Object { $_.Pid -eq $current } | Select-Object -First 1
				if (-not $proc) { break }
				$current = $proc.PPid
			}
			return ,$result  # the leading comma prevents PowerShell's pipeline output from enumerating the HashSet -- without it, a single-element set is unrolled and returned as a bare scalar (or $null if empty), and callers' `.Contains()` calls would throw under ErrorActionPreference=Stop
		}
		function Get-PidsMatchingRegex([string]$Pattern) {  # PIDs of processes whose command line matches an extended regex
			Get-ProcessSnapshot |
				Where-Object { $_.CommandLine -and ($_.CommandLine -match $Pattern) } |
				Select-Object -ExpandProperty Pid
		}
		function Get-PidsMatchingAll([string[]]$Substrings) {  # PIDs of processes whose command line contains every literal substring given (no regex escaping needed for paths)
			$comparison = if ($onWindows) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }  # same reasoning as Get-PidsMatchingRegexAndSubstring's $scopeComparison: this is also used to scope node-reuse worker matches to this checkout's own .dotnet path, and two Unix checkouts differing only by casing must not be treated as the same scope
			Get-ProcessSnapshot |
				Where-Object {
					$cmd = $_.CommandLine
					if (-not $cmd) { return $false }
					foreach ($s in $Substrings) {
						if ($cmd.IndexOf($s, $comparison) -lt 0) { return $false }
					}
					return $true
				} |
				Select-Object -ExpandProperty Pid
		}
		function Get-PidsMatchingRegexAndSubstring([string]$Pattern, [string]$Substring) {  # PIDs whose command line matches an extended regex AND contains a literal substring -- scopes the build-server name pattern to this checkout's own bootstrapped SDK, so a force-kill can never reach some other checkout's or tool's build server
			$scopeComparison = if ($onWindows) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }  # Windows paths are case-insensitive at the OS level so the scope check must match that; Unix paths are case-sensitive, so two checkouts differing only by casing (e.g. /work/repo vs /work/Repo) are genuinely different roots and must not be treated as the same scope -- matches folly.sh's plain (case-sensitive) `grep -F` scope check
			Get-ProcessSnapshot |
				Where-Object { $_.CommandLine -and ($_.CommandLine -match $Pattern) -and ($_.CommandLine.IndexOf($Substring, $scopeComparison) -ge 0) } |
				Select-Object -ExpandProperty Pid
		}
		function Stop-ProcessTree([int]$ProcessId) {  # kills a process's children first, then the process itself, escalating from a graceful stop to -Force if it's still alive after a short wait -- only reports success once the pid is confirmed gone AND this call actually had to signal it, since Stop-Process not throwing doesn't mean the process actually died (e.g. it ignores the initial signal), and a candidate that exited on its own between snapshot and kill attempt was never force-killed by cleanse at all
			$children = Get-ProcessSnapshot | Where-Object { $_.PPid -eq $ProcessId }
			foreach ($child in $children) {
				Stop-ProcessTree -ProcessId $child.Pid | Out-Null  # discard -- an uncaptured call's return value is implicit function output, and a multi-element array (this call's $true/$false alongside the parent's own) is *always* truthy to a caller's `Where-Object { Stop-ProcessTree ... }` regardless of the parent's real outcome
			}
			if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return $false }  # already gone on its own -- nothing for this call to count as killed
			try { Stop-Process -Id $ProcessId -ErrorAction Stop } catch {}
			$deadline = (Get-Date).AddSeconds(5)
			while ((Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
				Start-Sleep -Milliseconds 200
			}
			if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
				try { Stop-Process -Id $ProcessId -Force -ErrorAction Stop } catch {}
				Start-Sleep -Milliseconds 200
			}
			return -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
		}
		$ancestorPids = Get-AncestorPids
		if ($dotnetExe) {  # `build-server shutdown` always reports success whether or not a server was actually running, so its own output can't say what happened -- diff the PIDs before/after instead
			$buildServerPattern = 'VBCSCompiler|Microsoft\.CodeAnalysis\.Razor\.[A-Za-z.]*Server|(^|\s)rzc(\.dll)?(\s|$)'
			$beforeServerPids = @(Get-PidsMatchingRegex $buildServerPattern)
			try { & $dotnetExe build-server shutdown *> $null } catch {}  # best-effort under ErrorActionPreference=Stop -- a failed shutdown must never block cleanup outright
			if ($beforeServerPids.Count -gt 0) {
				$afterServerPids = @(Get-PidsMatchingRegex $buildServerPattern)
				$stoppedServerCount = @($beforeServerPids | Where-Object { $afterServerPids -notcontains $_ }).Count
				if ($stoppedServerCount -gt 0) {
					Write-Host "Stopped $stoppedServerCount build server process(es) (VBCSCompiler/Razor) via 'dotnet build-server shutdown'."
				}
				# `build-server shutdown` talks to the RPC pipe of servers registered by *this* SDK; a server started by a
				# different dotnet install (or one whose RPC pipe is already wedged/orphaned) doesn't respond and survives
				# silently. Force-killing is scoped tightly to avoid collateral damage: only a PID that (a) was already
				# alive in the *original* beforeServerPids snapshot -- never one that merely appears in a later snapshot,
				# which could be an unrelated process that started in between -- (b) is still alive after the shutdown
				# call, and (c) belongs to this checkout's own bootstrapped `.dotnet` SDK (the same scope MSBuild
				# node-reuse workers below are held to) is unconditionally stale and gets force-killed. A build server
				# for a different repo/SDK is left alone even if its name matches the pattern. The trailing
				# separator on the scope substring is required, not cosmetic: without it, a sibling directory
				# whose name merely starts with ".dotnet" (e.g. a ".dotnet-old" leftover from a prior bootstrap)
				# would also match, since "...\.dotnet-old\..." contains "...\.dotnet" as a plain substring.
				$scopedAfterServerPids = @(Get-PidsMatchingRegexAndSubstring $buildServerPattern ((Join-Path $PSScriptRoot ".dotnet") + [System.IO.Path]::DirectorySeparatorChar))
				$survivorServerPids = @($beforeServerPids | Where-Object { ($scopedAfterServerPids -contains $_) -and (-not $ancestorPids.Contains($_)) })  # never kill this script's own invoking shell/CI agent, even if it happens to match the scoped pattern above (e.g. an automation wrapper whose own command line embeds the search text)
				if ($survivorServerPids.Count -gt 0) {
					$forceKilledCount = @($survivorServerPids | Where-Object { Stop-ProcessTree -ProcessId $_ }).Count
					if ($forceKilledCount -gt 0) {
						Write-Host "Force-killed $forceKilledCount build server process(es) that ignored 'dotnet build-server shutdown'."
					}
				}
			}
		}
		# Node-reuse MSBuild worker processes are a different mechanism from build servers above -- left behind
		# by any dotnet/MSBuild invocation that didn't pass --nodeReuse false (an IDE build, a bare `dotnet
		# build`/`dotnet test`, `dotnet run --file eng/generate-compiler-code.cs`, ...) -- and are never
		# registered as build servers, so `build-server shutdown` can't see or stop them. cleanse itself never
		# launches a build, so any live MSBuild.dll worker rooted at this repo's own bootstrapped SDK is
		# unconditionally stale. Trailing separator on the scope substring is required for the same reason as
		# the build-server scope above -- without it, a ".dotnet-old"-style sibling directory would falsely match too.
		$nodeWorkerPids = @(Get-PidsMatchingAll @(((Join-Path $PSScriptRoot ".dotnet") + [System.IO.Path]::DirectorySeparatorChar), "MSBuild.dll") | Where-Object { -not $ancestorPids.Contains($_) })  # never kill this script's own invoking shell/CI agent -- see the build-server exclusion above
		if ($nodeWorkerPids.Count -gt 0) {
			$killedNodeCount = @($nodeWorkerPids | Where-Object { Stop-ProcessTree -ProcessId $_ }).Count
			if ($killedNodeCount -gt 0) {
				Write-Host "Killed $killedNodeCount leftover MSBuild node-reuse worker process(es)."
			}
		}
		if (Test-Path -LiteralPath $artifactsDir) {
			function Format-ByteSize([long]$bytes) {  # binary units throughout (1MB/1GB are PowerShell's built-in 1048576/1073741824 literals) -- labelled MiB/GiB, never MB/GB
				if ($bytes -ge 1GB) { return "{0:N2} GiB" -f ($bytes / 1GB) }
				elseif ($bytes -ge 1MB) { return "{0:N2} MiB" -f ($bytes / 1MB) }
				elseif ($bytes -ge 1KB) { return "{0:N2} KiB" -f ($bytes / 1KB) }
				else { return "$bytes B" }
			}
			function Get-DirStats([string]$dir) {
				$errs = $null
				$sum = Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue -ErrorVariable errs |  # piped straight into Measure-Object, not assigned first -- an assignment would materialize every FileInfo into an array before summing
					Measure-Object -Property Length -Sum
				$bytes = if ($sum.Sum) { $sum.Sum } else { 0L }
				return [PSCustomObject]@{ Bytes = $bytes; Count = $sum.Count; Ok = ($errs.Count -eq 0) }  # Ok is $false whenever ErrorVariable caught anything, so a partial/truncated scan can be told apart from a genuinely complete one (matches folly.sh's dir_stats)
			}
			$spinnerFrames = @('|', '/', '-', '\')
			$spinnerIndex = 0
			$clearLine = "`r" + [char]27 + "[K"  # the scanning phase below is a bare manually-redrawn spinner (no Write-Progress); the cleansing phase keeps Write-Progress's percent/rate/counts, just without the spinner glyph
			$threadJobAvailable = $null -ne (Get-Command Start-ThreadJob -ErrorAction SilentlyContinue)  # Start-Job spins up a whole new powershell(.exe) process (multi-second cold start); Start-ThreadJob (in-box since PS7) runs in a runspace inside this process instead
			function Start-CleanseJob([scriptblock]$ScriptBlock, $ArgumentList) {
				if ($threadJobAvailable) {
					return Start-ThreadJob -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList
				}
				return Start-Job -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList
			}
			$scanJob = $null  # both jobs are wrapped in try/finally below: on Ctrl+C, the finally block stops and removes whichever job is still running instead of leaving it attached to the session past the prompt returning
			$job = $null
			try {
				$scanJob = Start-CleanseJob -ScriptBlock {
					param($dir)
					$sum = Get-ChildItem -LiteralPath $dir -Recurse -Force -File -ErrorAction SilentlyContinue |
						Measure-Object -Property Length -Sum
					[PSCustomObject]@{ Bytes = if ($sum.Sum) { $sum.Sum } else { 0L }; Count = $sum.Count }
				} -ArgumentList $artifactsDir
				Write-Host -NoNewline "${clearLine}Scanning artefacts $($spinnerFrames[$spinnerIndex])"  # drawn immediately, before the loop's first 150ms tick, or the line stays blank that whole first interval
				while ($scanJob.State -notin 'Completed', 'Failed', 'Stopped') {  # not -eq 'Running': a freshly started job can still read 'NotStarted' on this first check (Start-ThreadJob starts fast enough to race it), which isn't 'Running' either but isn't finished
					Start-Sleep -Milliseconds 150
					$spinnerIndex = ($spinnerIndex + 1) % $spinnerFrames.Length
					Write-Host -NoNewline "${clearLine}Scanning artefacts $($spinnerFrames[$spinnerIndex])"
				}
				$totalStats = Receive-Job -Job $scanJob
				Remove-Job -Job $scanJob
				$scanJob = $null
				$totalBytes = $totalStats.Bytes
				$totalCount = $totalStats.Count
				$totalFormatted = Format-ByteSize $totalBytes
				Write-Host -NoNewline $clearLine
				$job = Start-CleanseJob -ScriptBlock {  # raw .NET File.Delete/Directory.Delete, not the Remove-Item cmdlet -- writes each deleted file's length to the job's own output stream as it goes, unverified against Remove-Item -Recurse -Force's speed (no pwsh here to benchmark)
					param($dir)
					try { foreach ($f in [System.IO.Directory]::EnumerateFiles($dir, '*', [System.IO.SearchOption]::AllDirectories)) { try { $len = ([System.IO.FileInfo]$f).Length; [System.IO.File]::Delete($f); Write-Output $len } catch {} } } catch {}  # EnumerateFiles itself (not just File.Delete) can throw mid-walk on a locked subtree -- stop rather than fault the whole job with nothing cleaned up
					try { $dirs = [System.IO.Directory]::EnumerateDirectories($dir, '*', [System.IO.SearchOption]::AllDirectories) | Sort-Object -Property { ($_ -split '[\\/]').Count } -Descending; foreach ($d in $dirs) { try { [System.IO.Directory]::Delete($d) } catch {} } } catch {}  # deepest first so a dir is always empty by the time it's deleted
					try { [System.IO.Directory]::Delete($dir) } catch {}
				} -ArgumentList $artifactsDir
				$startTime = Get-Date
				$deletedBytes = 0L
				$deletedCount = 0
				while ($job.State -notin 'Completed', 'Failed', 'Stopped') {
					Start-Sleep -Milliseconds 150
					foreach ($size in (Receive-Job -Job $job)) { $deletedBytes += [long]$size; $deletedCount++ }  # drains the job's own stream instead of re-scanning the tree -- no second operation racing the delete
					$percent = if ($totalBytes -gt 0) { [Math]::Min(99, [int](($deletedBytes / $totalBytes) * 100)) } else { [Math]::Min(99, [int](($deletedCount / [Math]::Max(1, $totalCount)) * 100)) }
					$elapsedSeconds = ((Get-Date) - $startTime).TotalSeconds
					$bytesPerSecond = if ($elapsedSeconds -gt 0) { $deletedBytes / $elapsedSeconds } else { 0 }
					Write-Progress -Activity "Cleansing artefacts" -Status "$deletedCount / $totalCount files, $(Format-ByteSize $deletedBytes) / $totalFormatted, $(Format-ByteSize $bytesPerSecond)/s" -PercentComplete $percent
				}
				foreach ($size in (Receive-Job -Job $job -ErrorAction SilentlyContinue)) { $deletedBytes += [long]$size; $deletedCount++ }
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
			Write-Host -NoNewline "`r"  # -Completed doesn't reliably leave the cursor at column 0
			if (Test-Path -LiteralPath $artifactsDir) {
				Remove-Item -Recurse -Force -LiteralPath $artifactsDir -ErrorAction SilentlyContinue  # a transiently held lock (e.g. an antivirus scanner) can clear between the bulk delete and now -- retry once before reporting survivors
			}
			if (Test-Path -LiteralPath $artifactsDir) {
				$remainingStats = Get-DirStats $artifactsDir
				$deletedBytes = [Math]::Max(0L, $totalBytes - $remainingStats.Bytes)
				if ($remainingStats.Ok) {
					Write-Host "Cleansed $(Format-ByteSize $deletedBytes) of artefacts; $($remainingStats.Count) file(s) could not be removed." -ForegroundColor Yellow
				}
				else {
					Write-Host "Cleansed $(Format-ByteSize $deletedBytes) of artefacts; at least $($remainingStats.Count) file(s) could not be removed (some may be unreadable and not counted)." -ForegroundColor Yellow  # Get-ChildItem hit an error partway, so remainingStats.Count is a lower bound, not exact
				}
				exit 1  # folly.sh exits 1 whenever artifacts/ survives cleanup too, so scripting/CI around either tool can rely on the same exit code meaning the same thing
			}
			else {
				Write-Host "Cleansed $totalFormatted of artefacts." -ForegroundColor Green
			}
		}
		else {
			Write-Host "No artefacts to cleanse."
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
