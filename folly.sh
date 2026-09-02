#!/usr/bin/env bash
set -euo pipefail
if [[ -t 1 ]] && command -v tput >/dev/null 2>&1; then
  tput civis 2>/dev/null || true
  trap 'tput cnorm 2>/dev/null || true' EXIT
fi
action="${1:-}"
shift $(( $# < 1 ? $# : 1 )) || true
scriptroot="$(cd -P "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Git-for-Windows' bash (MSYS2, identified by $MSYSTEM -- e.g. "MINGW64") auto-converts any
# '/'-prefixed argument into a Windows path before it reaches a native (non-MSYS) executable, on the
# assumption it's a Unix path being handed to something that expects a Windows one. eng/common/tools.sh
# (Arcade-vendored -- never hand-edited, see .github/memory/KNOWN_ISSUES.md) invokes MSBuild.exe with
# classic single-slash switches (/m /nologo /clp:Summary /v:... /nr:... /warnaserror), which are exactly
# what that heuristic misfires on: '/nologo' has been observed mangled into 'C:/Program Files/Git/nologo'
# (the MSYS install root prepended, as if '/nologo' were a Unix path rooted there), producing MSBuild
# errors like "Only one project can be specified." from switches that arrived as extra positional
# arguments instead. This is a bash-on-Windows-specific problem -- WSL's bash is a real Linux userland
# with no such translation layer, and native Linux/macOS bash has no $MSYSTEM at all -- so it's scoped to
# only fire under real Git-Bash/MSYS2, never touching the WSL or Linux/macOS path this same script also
# runs. The exclusion list itself is scoped to just these switch prefixes, not '*' -- MSYS2_ARG_CONV_EXCL
# disables conversion for every native-process argument it matches, and eng/build.sh separately relies on
# that same auto-conversion to turn genuine POSIX paths (e.g. $toolset_build_proj, the value inside
# /p:Projects="$repo_root/$solution") into Windows paths MSBuild.exe can use; excluding everything with
# '*' would silently break those too instead of just fixing the misconverted switches.
if [[ -n "${MSYSTEM:-}" ]]; then
  export MSYS2_ARG_CONV_EXCL='/m;/nologo;/clp:;/v:;/nr:;/warnaserror'
fi
solution="FollyOfTheUnbound.slnx"
build_script="$scriptroot/eng/build.sh"
nupkg_root="$scriptroot/../.nupkg/FotU"
# Framework/.NET Framework (net472) tests only ever run on a genuine Windows host -- net472 has no
# cross-platform runtime, so eng/build.sh's --testDesktop rejects itself on any other host. 'scry'
# below defaults to running both Core and Framework on Windows (matching folly.ps1's own default),
# and Core-only elsewhere; '--framework' on a non-Windows host is a hard error, not a silent skip.
is_windows_host() {
  case "${OSTYPE:-}" in
    msys*|cygwin*|win32*) return 0 ;;
  esac
  case "$(uname -s 2>/dev/null)" in
    MINGW*|MSYS*|CYGWIN*) return 0 ;;
  esac
  return 1
}
if [[ -t 1 ]]; then  # plain text when redirected/piped (e.g. a CI log), colored in an interactive terminal -- matches scripts/test-folly-scry-args.sh's own convention
  color_reset=$'\033[0m'; color_red=$'\033[31m'; color_green=$'\033[32m'; color_yellow=$'\033[33m'; color_cyan=$'\033[36m'; color_gray=$'\033[90m'
else
  color_reset=''; color_red=''; color_green=''; color_yellow=''; color_cyan=''; color_gray=''
fi
colorize_grimoire_help() {
  if [[ ! -t 1 ]]; then
    cat
    return
  fi

  sed -E \
    -e "s/^([^[:space:]].*:)\$/${color_cyan}\1${color_reset}/" \
    -e "s/(<[^>]+>)/${color_yellow}\1${color_reset}/g" \
    -e "s/(\[switches\])/${color_gray}\1${color_reset}/g" \
    -e "/^[[:space:]]+'/ {" \
    -e "  s/(--[[:alnum:]]+)/${color_green}\1${color_reset}/g" \
    -e "  s/'(attune|bind|cleanse|grimoire|reweave|scry|weave)([[:space:]'])/'${color_green}\1${color_reset}\2/" \
    -e "  s/(reflection|research|truth)([[:space:]'])/${color_green}\1${color_reset}\2/" \
    -e "}"
}
# Bash port of folly.ps1's Get-TestSummary: tallies a completed leg's PASSED/FAILED/TIMEOUT counts
# (and keeps the raw table lines) from its already-written runtests*.log, the same way -- anchoring
# on the marker pair immediately before RunTests' exact footer line, not the first or last
# "================" pair in the file, since a failed test's own captured stdout/stderr can itself
# contain such a line (see scripts/test-folly-scry-args.ps1's New-FalseMarkerTestCase fixture, whose
# bash counterpart is exercised via scripts/test-folly-scry-args.sh).
# Unlike folly.ps1, this never needs to defer/capture a leg's own live RunTests output: eng/build.sh
# invokes RunTests.dll with a direct foreground `dotnet exec`, so it already writes straight to the
# inherited console (see the "-testInteractiveConsole" entry in .github/memory/KNOWN_ISSUES.md) --
# there is nothing to suppress here, only this final combined table to add on top.
# Sets: summary_found (0/1), summary_passed, summary_failed, summary_timeout, summary_lines_text.
get_test_summary() {
  local log_path="$1"
  summary_found=0
  summary_passed=0
  summary_failed=0
  summary_timeout=0
  summary_lines_text=""
  if [[ ! -f "$log_path" ]]; then
    return
  fi
  local footer_text="Extra run diagnostics for logging, did not impact run results"
  local footer_line
  footer_line="$(grep -n -F -x -- "$footer_text" "$log_path" | tail -1 | cut -d: -f1)"
  if [[ -z "$footer_line" ]]; then
    return
  fi
  local -a markers_before=()
  local ln
  while IFS=: read -r ln _; do
    [[ "$ln" -lt "$footer_line" ]] && markers_before+=("$ln")
  done < <(grep -n -x -- "================" "$log_path")
  local count=${#markers_before[@]}
  if [[ "$count" -lt 2 ]]; then
    return
  fi
  local end_line="${markers_before[$((count - 1))]}"
  local start_line="${markers_before[$((count - 2))]}"
  summary_lines_text="$(awk -v s="$start_line" -v e="$end_line" 'NR>s && NR<e' "$log_path")"
  # -c prints "0" but exits 1 on zero matches -- `|| true` so that doesn't trip `set -e` here.
  summary_passed="$(grep -cE '\bPASSED\b' <<<"$summary_lines_text" || true)"
  summary_failed="$(grep -cE '\bFAILED\b' <<<"$summary_lines_text" || true)"
  summary_timeout="$(grep -cE '\bTIMEOUT\b' <<<"$summary_lines_text" || true)"
  summary_found=1
}
if [[ -z "$action" || "$action" == "grimoire" ]]; then
  cat <<'EOF' | colorize_grimoire_help

Commands:
    'attune     <primary>   [switches]'
        Restore only.

    'bind       <primary>   [switches]'
        Restore, build & pack (nupkg files packed to ..\.nupkg\FotU\<config>\).

    'cleanse'
        Delete artefacts.

    'grimoire'
        Show this text (default when no action is given).

    'reweave    <primary>   [switches]'
        Restore & rebuild.

    'scry       <primary>   [switches]'
        Restore, build & run Core (and, on Windows, Framework) unit tests.

    'weave      <primary>   [switches]'
        Restore & build.

Primary args:
    '<scry>     reflection'
        Runs folly script test harnesses.

    '<command>  research    [switches]'
        Debug configuration.

    '<command>  truth       [switches]'
        Release configuration.

Switches:
    '<command>  <primary>   --binaryLog'
        Write MSBuild binary log to .\artifacts\log\<config>\Build.binlog.

    '<command>  <primary>   --bootstrap'
        Build/test using a locally-built bootstrap compiler.

    '<scry>     <primary>   --core'
        Run only the Core tests (skip Framework).

    '<scry>     <primary>   --framework'
        Run only the Framework tests (skip Core; Windows only).

    '<scry>     <primary>   --testCompilerOnly'
        Run only the compiler unit test assemblies.

    '<scry>     <primary>   --testFilter <xunit filter>'
        Filter tests to run, e.g. FullyQualifiedName~TestClass1|Category=CategoryA.

    '<scry>     <primary>   --testIOperation'
        Run tests with the IOperation test hook enabled.

    '<scry>     <primary>   --collectDumps'
        Enable RunTests' Windows-only crash/hang dump collection (opt-in: mutates a machine-wide
        WER registry key and its timeout-dump path can capture unrelated processes' memory -- see
        API_MAP.md for details).

    '<scry>     <primary>   --timeout <minutes>'
        Override RunTests' whole-run watchdog (default: 90).

    '<command>  <primary>   --verbosity <level>'
        MSBuild verbosity: quiet, minimal, normal, detailed, diagnostic.

EOF
  exit 0
fi
config=""
test_timeout=0
reflection=0
binary_log=0
verbosity=""
core=0
framework=0
test_compiler_only=0
test_filter=""
test_ioperation=0
collect_dumps=0
bootstrap=0
while [[ $# -gt 0 ]]; do
  case "$1" in
	reflection)
	  reflection=1
	  shift
	  ;;
	--core)
	  core=1
	  shift
	  ;;
	--framework)
	  framework=1
	  shift
	  ;;
	--testCompilerOnly)
	  test_compiler_only=1
	  shift
	  ;;
	--testFilter)
	  test_filter="${2:-}"
	  if [[ -z "$test_filter" ]]; then
		echo "'--testFilter' requires a value." >&2
		exit 1
	  fi
	  shift 2
	  ;;
	--testIOperation)
	  test_ioperation=1
	  shift
	  ;;
	--collectDumps)
	  # Opt-in, not unconditional: RunTests' Windows Error Reporting registry-based dump collection
	  # (DumpUtil in src/Tools/RunTests/ProcDumpUtil.cs) mutates a single machine-wide HKLM key with
	  # no cross-process coordination, and its own timeout-dump path
	  # (Program.HandleTimeout/ProcessUtil.GetTestHostProcesses) sweeps every testhost-like process
	  # on the machine, not just this run's -- both are safe for a caller who explicitly asked for
	  # dump collection and knows the tradeoff, but not as scry's silent default for every ordinary
	  # local run.
	  collect_dumps=1
	  shift
	  ;;
	--bootstrap)
	  bootstrap=1
	  shift
	  ;;
	--timeout)
	  test_timeout="${2:-}"
	  if [[ -z "$test_timeout" || ! "$test_timeout" =~ ^[0-9]{1,9}$ ]]; then  # digit-count cap first: an all-digits value past bash's 64-bit $(( )) range would otherwise silently wrap into an unrelated small number below
		echo "'--timeout' requires a positive integer minute count (up to 999999999), got '${2:-}'." >&2
		exit 1
	  fi
	  test_timeout=$((10#$test_timeout))  # force base-10: bash treats a leading-zero operand as octal, and "08"/"09" aren't valid octal digits
	  if [[ "$test_timeout" -le 0 || "$test_timeout" -gt 71582 ]]; then  # 71582 min = Task.Delay's ms-argument ceiling (4294967294ms), which is what RunTests.Program.RunCoreAsync forwards this straight into
		echo "'--timeout' requires a positive integer minute count, up to 71582 (Task.Delay's supported maximum), got '${2:-}'." >&2
		exit 1
	  fi
	  shift 2
	  ;;
	--binaryLog)
	  binary_log=1
	  shift
	  ;;
	--verbosity)
	  verbosity="${2:-}"
	  if [[ -z "$verbosity" ]]; then
		echo "'--verbosity' requires a value: quiet, minimal, normal, detailed, or diagnostic." >&2
		exit 1
	  fi
	  shopt -s nocasematch  # case-insensitive match, matching folly.ps1's -notin (PowerShell string comparisons are case-insensitive by default); toggled back off immediately after, not left on for the rest of the script
	  case "$verbosity" in  # full words only, not MSBuild's own q/m/n/d/diag shorthand -- explicit over terse
		quiet|minimal|normal|detailed|diagnostic) shopt -u nocasematch ;;
		*)
		  shopt -u nocasematch
		  echo "'--verbosity' requires one of: quiet, minimal, normal, detailed, diagnostic. Got '$verbosity'." >&2
		  exit 1
		  ;;
	  esac
	  shift 2
	  ;;
	research|truth)
	  if [[ -n "$config" ]]; then
		echo "Unrecognized argument '$1' (config already set to '$config')." >&2
		exit 1
	  fi
	  config="$1"
	  shift
	  ;;
	*)
	  echo "Unrecognized argument '$1'." >&2
	  exit 1
	  ;;
  esac
done
# '--core'/'--framework'/'--testCompilerOnly'/'--testFilter'/'--testIOperation'/'--timeout'/reflection
# are scoped to 'scry' -- one combined check/message rather than one per selector, since they're all
# the same rule applied to different args. '--bootstrap' is NOT scoped to 'scry': like '--binaryLog'/
# '--verbosity' it's valid on any build-invoking action, just rejected on 'cleanse' below.
if [[ ( "$core" -eq 1 || "$framework" -eq 1 || "$test_compiler_only" -eq 1 || -n "$test_filter" || "$test_ioperation" -eq 1 || "$collect_dumps" -eq 1 || "$test_timeout" -gt 0 || "$reflection" -eq 1 ) && "$action" != "scry" ]]; then
  echo "'--core'/'--framework'/'--testCompilerOnly'/'--testFilter'/'--testIOperation'/'--collectDumps'/'--timeout'/'reflection' are only valid with the 'scry' action." >&2
  exit 1
fi
# By this point "$action" == "scry" is already guaranteed whenever reflection is set (the check
# above would have rejected it otherwise), so this doesn't need to re-check "$action" itself.
if [[ "$reflection" -eq 1 && ( -n "$config" || "$core" -eq 1 || "$framework" -eq 1 || "$test_compiler_only" -eq 1 || -n "$test_filter" || "$test_ioperation" -eq 1 || "$collect_dumps" -eq 1 || "$test_timeout" -gt 0 || "$binary_log" -eq 1 || -n "$verbosity" || "$bootstrap" -eq 1 ) ]]; then
  echo "'reflection' doesn't take a primary arg or any switches -- it runs folly's own test harnesses, not a build/RunTests." >&2
  exit 1
fi
# '--framework' requires a Windows host regardless of which selector combination was given -- checked
# unconditionally here (not deferred into the scry branch below) so the error surfaces before any
# build/restore work starts.
if [[ "$framework" -eq 1 ]] && ! is_windows_host; then
  echo "'--framework' requires a Windows host (.NET Framework tests have no cross-platform runtime)." >&2
  exit 1
fi
if [[ ( "$binary_log" -eq 1 || -n "$verbosity" || "$bootstrap" -eq 1 ) && "$action" == "cleanse" ]]; then
  echo "'--binaryLog'/'--verbosity'/'--bootstrap' aren't valid with 'cleanse' -- there's no build to log or bootstrap." >&2
  exit 1
fi
if [[ "$action" == "cleanse" || ( "$action" == "scry" && "$reflection" -eq 1 ) ]]; then
  configuration=""
  nupkg_dir=""
elif [[ -z "$config" ]]; then
  echo "Primary arg is required for action '$action'. Expected 'research' or 'truth'." >&2
  exit 1
elif [[ "$config" == "research" ]]; then
  configuration="Debug"
  nupkg_dir="$nupkg_root/Debug"
elif [[ "$config" == "truth" ]]; then
  configuration="Release"
  nupkg_dir="$nupkg_root/Release"
else
  echo "Unrecognized configuration '$config'. Expected 'research' or 'truth'." >&2
  exit 1
fi
extra_build_args=()  # --binaryLog: forwarded as-is to eng/build.sh's own -binaryLog/-bl. --verbosity: already restricted to full words above (eng/build.sh's own -verbosity/-v itself still accepts MSBuild's q/m/n/d/diag shorthand too, but folly.sh only ever forwards the validated full-word form here)
if [[ "$binary_log" -eq 1 ]]; then
  extra_build_args+=(--binaryLog)
fi
if [[ -n "$verbosity" ]]; then
  extra_build_args+=(--verbosity "$verbosity")
fi
# --bootstrap gets two different forwarded forms, not just one flag folded into extra_build_args
# above: a "build" invocation (attune/weave/reweave/bind, and scry's own initial restore+build call)
# passes plain --bootstrap, which builds the bootstrap compiler fresh into a deterministic
# artifacts/Bootstrap dir (see eng/build.sh's MakeBootstrapBuild). scry then runs each requested test
# leg as its own separate eng/build.sh invocation -- passing --bootstrap again there would rebuild it
# from scratch per leg, so those instead pass --bootstrapDir pointing at the same dir to reuse it.
bootstrap_build_args=()
bootstrap_test_args=()
if [[ "$bootstrap" -eq 1 ]]; then
  bootstrap_build_args+=(--bootstrap)
  bootstrap_test_args+=(--bootstrapDir "$scriptroot/artifacts/Bootstrap")
fi
# Passed as a raw MSBuild property (not one of eng/build.sh's own named switches, so via its
# "properties" passthrough, not extra_build_args above) on every build this script runs:
# eng/build.sh's BuildSolution invokes MSBuild on Arcade's toolset Build.proj, passing the .slnx only
# via /p:Projects=..., so the built-in $(SolutionName) is never actually "FollyOfTheUnbound" here --
# see the matching comment in Microsoft.CodeAnalysis.Analyzer.Testing.csproj for the RoslynSdk
# collision this was added to fix.
identity_args=(/p:FollyOfTheUnboundBuild=true)
case "$action" in  # --nodeReuse false on every branch below: Arcade's tools.sh defaults nodeReuse true locally, leaving MSBuild worker nodes running after exit, still holding DLLs open under artifacts/ (`build-server shutdown` in cleanse only stops VBCSCompiler/Razor, not these)
  attune)
	"$build_script" --restore --nodeReuse false --solution "$solution" --configuration "$configuration" ${extra_build_args[@]+"${extra_build_args[@]}"} ${bootstrap_build_args[@]+"${bootstrap_build_args[@]}"} "${identity_args[@]}"
	;;
  weave)
	"$build_script" --restore --build --nodeReuse false --solution "$solution" --configuration "$configuration" ${extra_build_args[@]+"${extra_build_args[@]}"} ${bootstrap_build_args[@]+"${bootstrap_build_args[@]}"} "${identity_args[@]}"
	;;
  reweave)
	"$build_script" --restore --rebuild --nodeReuse false --solution "$solution" --configuration "$configuration" ${extra_build_args[@]+"${extra_build_args[@]}"} ${bootstrap_build_args[@]+"${bootstrap_build_args[@]}"} "${identity_args[@]}"
	;;
  bind)
	"$build_script" --restore --build --pack --nodeReuse false --solution "$solution" --configuration "$configuration" ${extra_build_args[@]+"${extra_build_args[@]}"} ${bootstrap_build_args[@]+"${bootstrap_build_args[@]}"} "${identity_args[@]}"
	;;
  scry)
	if [[ "$reflection" -eq 1 ]]; then
	  harness_fail=0
	  echo ""
	  for harness in test-folly-cleanse.sh test-folly-scry-args.sh; do
		echo "--- $harness ---"
		bash "$scriptroot/scripts/$harness" || harness_fail=1
		echo
	  done
	  exit "$harness_fail"
	fi
	# Default to both Core and Framework on Windows when neither switch is given (matching
	# folly.ps1); elsewhere Framework can never run (rejected above), so only Core ever does.
	run_core=1
	run_framework=0
	if is_windows_host; then
	  if [[ "$core" -eq 0 && "$framework" -eq 0 ]]; then
		run_core=1
		run_framework=1
	  else
		run_core=$core
		run_framework=$framework
	  fi
	fi
	build_args=(--restore --build --nodeReuse false --solution "$solution" --configuration "$configuration")
	build_args+=(${extra_build_args[@]+"${extra_build_args[@]}"})
	build_args+=(${bootstrap_build_args[@]+"${bootstrap_build_args[@]}"})
	build_args+=("${identity_args[@]}")
	"$build_script" "${build_args[@]}"
	# Suffixed TestResults/log dirs, always -- matching folly.ps1, which sets
	# $env:FOTU_TEST_RESULTS_SUFFIX for each requested leg unconditionally, not only when both run.
	core_test_results_dir="$scriptroot/artifacts/TestResults/$configuration-Core"
	core_log_dir="$scriptroot/artifacts/log/$configuration-Core"
	framework_test_results_dir="$scriptroot/artifacts/TestResults/$configuration-Framework"
	framework_log_dir="$scriptroot/artifacts/log/$configuration-Framework"
	rm -rf "$core_test_results_dir" "$core_log_dir" "$framework_test_results_dir" "$framework_log_dir"
	both_legs=0
	[[ "$run_core" -eq 1 && "$run_framework" -eq 1 ]] && both_legs=1
	test_args=(--nodeReuse false --solution "$solution" --configuration "$configuration")
	if [[ "$test_timeout" -gt 0 ]]; then
	  test_args+=(--testTimeout "$test_timeout")
	fi
	if [[ "$test_compiler_only" -eq 1 ]]; then
	  test_args+=(--testCompilerOnly)
	fi
	if [[ -n "$test_filter" ]]; then
	  test_args+=(--testFilter "$test_filter")
	fi
	if [[ "$test_ioperation" -eq 1 ]]; then
	  test_args+=(--testIOperation)
	fi
	# Opt-in, not unconditional (see the --collectDumps arg-parsing comment above for why): only
	# forwarded when the caller explicitly asked for it. build.sh's own is_windows_host gate still
	# silently no-ops this on a non-Windows host either way.
	if [[ "$collect_dumps" -eq 1 ]]; then
	  test_args+=(--collectDumps)
	fi
	if [[ "$both_legs" -eq 1 ]]; then
	  # Suppress each leg's own live final PASSED/FAILED/TIMEOUT table (still written to its log
	  # file) so the combined-summary block below is the only place it prints -- otherwise it prints
	  # once live per leg and again in the combined block. See Options.SuppressConsoleSummary /
	  # TestRunner.Print in src/Tools/RunTests/; mirrors folly.ps1's -testSuppressConsoleSummary:$bothLegs.
	  test_args+=(--testSuppressConsoleSummary)
	fi
	test_args+=(${extra_build_args[@]+"${extra_build_args[@]}"})
	test_args+=(${bootstrap_test_args[@]+"${bootstrap_test_args[@]}"})
	test_args+=("${identity_args[@]}")
	core_exit=0
	if [[ "$run_core" -eq 1 ]]; then
	  FOTU_TEST_RESULTS_SUFFIX=Core "$build_script" --test "${test_args[@]}" || core_exit=$?
	fi
	framework_exit=0
	if [[ "$run_framework" -eq 1 ]]; then
	  FOTU_TEST_RESULTS_SUFFIX=Framework "$build_script" --testDesktop "${test_args[@]}" || framework_exit=$?
	fi
	# --- combined test summary (bash port of folly.ps1's post-legs summary block) ---
	summary_labels=()
	summary_founds=()
	summary_passeds=()
	summary_faileds=()
	summary_timeouts=()
	summary_exitcodes=()
	summary_texts=()
	if [[ "$run_core" -eq 1 ]]; then
	  get_test_summary "$core_log_dir/runtestsCore.log"
	  summary_labels+=("Core")
	  summary_founds+=("$summary_found")
	  summary_passeds+=("$summary_passed")
	  summary_faileds+=("$summary_failed")
	  summary_timeouts+=("$summary_timeout")
	  summary_exitcodes+=("$core_exit")
	  summary_texts+=("$summary_lines_text")
	fi
	if [[ "$run_framework" -eq 1 ]]; then
	  get_test_summary "$framework_log_dir/runtestsFramework.log"
	  summary_labels+=("Framework")
	  summary_founds+=("$summary_found")
	  summary_passeds+=("$summary_passed")
	  summary_faileds+=("$summary_failed")
	  summary_timeouts+=("$summary_timeout")
	  summary_exitcodes+=("$framework_exit")
	  summary_texts+=("$summary_lines_text")
	fi
	# Print each requested leg's own PASSED/FAILED/TIMEOUT list here, together, once every leg has
	# finished -- rather than letting each leg's own RunTests process print its list live the moment
	# that leg completes, which (when both --core and --framework run) buries the first leg's list
	# under the second leg's own subsequent build/live-table output instead of leaving both visible
	# together at the end. Each leg was run with --testSuppressConsoleSummary above (see test_args),
	# so this is the only place that table prints -- it isn't a duplicate of anything already shown
	# live. Matches folly.ps1's own $bothLegs block.
	if [[ "$both_legs" -eq 1 ]]; then
	  for i in "${!summary_labels[@]}"; do
		echo ""
		echo "${color_cyan}=== ${summary_labels[$i]} results ===${color_reset}"
		if [[ "${summary_founds[$i]}" -eq 0 ]]; then
		  echo "${color_yellow}summary unavailable (no runtests.log found)${color_reset}"
		else
		  # Colored per-line to match RunTests' own live-console table (TestRunner.Print) and the
		  # scry live table (LiveTestProgressDisplay) -- PASSED/FAILED/TIMEOUT are exactly the
		  # same three tokens get_test_summary above already tallies each line by.
		  while IFS= read -r result_line; do
			if grep -qE '\bTIMEOUT\b' <<<"$result_line"; then
			  echo "${color_yellow}${result_line}${color_reset}"
			elif grep -qE '\bFAILED\b' <<<"$result_line"; then
			  echo "${color_red}${result_line}${color_reset}"
			elif grep -qE '\bPASSED\b' <<<"$result_line"; then
			  echo "${color_green}${result_line}${color_reset}"
			else
			  echo "$result_line"
			fi
		  done <<<"${summary_texts[$i]}"
		fi
	  done
	fi
	echo ""
	echo "${color_cyan}=== Test summary ===${color_reset}"
	total_passed=0
	total_failed=0
	total_timeout=0
	any_leg_failed_exit=0
	missing_summaries=0
	for i in "${!summary_labels[@]}"; do
	  if [[ "${summary_exitcodes[$i]}" -ne 0 ]]; then
		any_leg_failed_exit=1
	  fi
	  if [[ "${summary_founds[$i]}" -eq 1 ]]; then
		total_passed=$((total_passed + summary_passeds[i]))
		total_failed=$((total_failed + summary_faileds[i]))
		total_timeout=$((total_timeout + summary_timeouts[i]))
		leg_color="$color_green"
		if [[ "${summary_faileds[$i]}" -gt 0 || "${summary_timeouts[$i]}" -gt 0 || "${summary_exitcodes[$i]}" -ne 0 ]]; then
		  leg_color="$color_red"
		fi
		echo "${leg_color}${summary_labels[$i]}: ${summary_passeds[$i]} passed, ${summary_faileds[$i]} failed, ${summary_timeouts[$i]} timeout${color_reset}"
	  else
		missing_summaries=$((missing_summaries + 1))
		echo "${color_yellow}${summary_labels[$i]}: summary unavailable (no runtests.log found)${color_reset}"
	  fi
	done
	# Green requires every requested leg to have exited 0 AND produced a readable summary with no
	# failures/timeouts -- matches folly.ps1's own $overallSuccess.
	overall_success=1
	[[ "$any_leg_failed_exit" -eq 1 || "$missing_summaries" -gt 0 || "$total_failed" -gt 0 || "$total_timeout" -gt 0 ]] && overall_success=0
	overall_color="$color_green"
	[[ "$overall_success" -eq 0 ]] && overall_color="$color_red"
	echo "${overall_color}Overall: $total_passed passed, $total_failed failed, $total_timeout timeout${color_reset}"
	echo ""
	if [[ "$run_core" -eq 1 ]]; then
	  echo "Core test results: $core_test_results_dir (logs: $core_log_dir)"
	fi
	if [[ "$run_framework" -eq 1 ]]; then
	  echo "Framework test results: $framework_test_results_dir (logs: $framework_log_dir)"
	fi
	if [[ "$core_exit" -ne 0 ]]; then
	  exit "$core_exit"
	fi
	if [[ "$framework_exit" -ne 0 ]]; then
	  exit "$framework_exit"
	fi
	if [[ "$overall_success" -eq 0 ]]; then
	  exit 1  # every requested leg exited 0, but the summary itself says otherwise -- don't let that read as success to automation
	fi
	exit 0
	;;
  cleanse)
	artifacts_dir="$scriptroot/artifacts"
	dotnet_exe="$scriptroot/.dotnet/dotnet"  # VBCSCompiler/MSBuild/the Razor server keep DLLs open under artifacts/ between invocations -- shut them down first so cleanse never races a locked file
	if [[ ! -x "$dotnet_exe" ]]; then
	  dotnet_exe="$scriptroot/.dotnet/dotnet.exe"  # Git Bash on Windows runs a bootstrapped SDK named dotnet.exe, not the extensionless Unix name
	fi
	if [[ ! -x "$dotnet_exe" ]]; then
	  dotnet_exe=$(command -v dotnet 2>/dev/null) || dotnet_exe=""  # fall back to a global dotnet only if this repo's own bootstrapped SDK under .dotnet/ isn't there
	fi
	scriptroot_dotnet_scope="$scriptroot/.dotnet/"  # substring scope used to confine a build-server/node-reuse match to this checkout's own bootstrapped SDK
	artifacts_scope="$artifacts_dir/"  # same, for a BuildHost match against this checkout's own artifacts/
	if is_windows_host && command -v cygpath >/dev/null 2>&1; then
	  # `Get-CimInstance Win32_Process`'s CommandLine is always a native Win32 path (e.g. "C:\repo\.dotnet\dotnet.exe"),
	  # never the MSYS-style "/c/repo/.dotnet/..." $scriptroot/$artifacts_dir already are on this host -- a plain-text
	  # substring match against the MSYS form never matches the native one, silently finding nothing to scope against
	  # (the exact "locked artifacts survive cleanse" symptom this fix chases). cygpath -w converts once, up front.
	  scriptroot_dotnet_scope="$(cygpath -w "$scriptroot/.dotnet" 2>/dev/null)\\"
	  artifacts_scope="$(cygpath -w "$artifacts_dir" 2>/dev/null)\\"
	  [[ "$scriptroot_dotnet_scope" == '\' ]] && scriptroot_dotnet_scope="$scriptroot/.dotnet/"  # cygpath failed -- fall back rather than scope against a useless bare backslash
	  [[ "$artifacts_scope" == '\' ]] && artifacts_scope="$artifacts_dir/"
	fi
	native_self_pid="$$"  # bash's own $$ is MSYS/Cygwin's emulated pid, which the CIM queries this script otherwise uses (native Win32_Process.ProcessId) can't be trusted to match. Computed here, directly in this process via a plain redirect (`read ... < file`) rather than any `$(...)` command substitution -- command substitution always forks a subshell to run its command, so a value read *inside* one (including inside a shell function called as `$(fn)`) would give that transient subshell's own pid, not this persistent script process's, one hop short of where the ancestor walk below needs to start. MSYS/Cygwin's /proc exposes the real native pid directly per-process as /proc/<pid>/winpid, no spawned helper needed.
	if is_windows_host && [[ -r /proc/self/winpid ]]; then
	  read -r native_self_pid < /proc/self/winpid 2>/dev/null || native_self_pid="$$"
	fi
	_cleanse_pwsh_exe() {  # resolves whichever PowerShell this Windows host actually has, once per call site -- pwsh (PS7+) preferred over the inbox powershell.exe
	  command -v pwsh 2>/dev/null || command -v powershell.exe 2>/dev/null || command -v powershell 2>/dev/null || true
	}
	_cleanse_exclude_self_matchers() {  # strips rows whose own command is this pipeline's own transient grep matcher -- otherwise a pattern like "VBCSCompiler" or "MSBuild.dll" matches the literal text of the very grep command line searching for it. Matches a bare "grep"/"grep.exe" name optionally preceded by a path (native CommandLine gives a full path like "C:\...\grep.exe -Ei ...", not just "grep ..." the way Unix ps's COMMAND field does) -- including a quoted path, which is how CIM reports an executable under a directory containing spaces (e.g. Git for Windows' default "C:\Program Files\Git\..."); the unquoted alternative can't contain spaces itself, since nothing then marks where the path ends and the arguments begin
	  grep -v -E '^[[:space:]]*[0-9]+[[:space:]]+("[^"]*[\\/]grep(\.exe)?"|([^[:space:]]*[\\/])?grep(\.exe)?)([[:space:]]|$)' || true  # `|| true`: no matches is not an error here (grep exits 1), and must never abort the script under `set -e`/pipefail
	}
	_cleanse_ps_snapshot() {  # `ps -eo pid,command` with the header stripped and our own transient grep rows excluded
	  if is_windows_host; then
		# Git-for-Windows' bash runs on MSYS2/Cygwin's own `ps`, which is NOT procps -- it has no `-eo`/`-o`
		# custom-field support at all, and by default only lists processes reachable through the MSYS/Cygwin
		# runtime's own pid table, not arbitrary native Windows processes. A BuildHost `dotnet <dll>` child
		# spawned by .NET's own Process.Start (BuildHostProcessManager, see KNOWN_ISSUES.md) never touches
		# msys-2.0.dll, so it's invisible to plain `ps` here even though it's a perfectly live Windows
		# process -- this is what made cleanse silently fail to find/kill it on bash while folly.ps1 (which
		# uses Win32_Process via CIM, see below) always could. Route through the same CIM query folly.ps1
		# uses instead, via whichever PowerShell this host has. CIM's ProcessId/CommandLine are native Win32
		# values throughout -- callers must scope substring matches against native (backslash) paths, not
		# the MSYS-style ($scriptroot/$artifacts_dir) forms this script otherwise uses everywhere else.
		local pwsh_exe
		pwsh_exe=$(_cleanse_pwsh_exe)
		if [[ -n "$pwsh_exe" ]]; then
		  "$pwsh_exe" -NoProfile -NonInteractive -Command 'Get-CimInstance Win32_Process | ForEach-Object { "$($_.ProcessId) $($_.CommandLine)" }' 2>/dev/null | _cleanse_exclude_self_matchers && return
		fi
		# No usable PowerShell found -- fall back to MSYS/Cygwin's own `-W` (include native Windows processes,
		# not just MSYS ones), best-effort. Cygwin/MSYS ps has no -o/-eo custom-field support at all (unlike
		# procps), so -W's fixed columns (PID PPID PGID WINPID TTY UID STIME COMMAND) have to be parsed
		# directly; WINPID (not the leading PID column) is the native Win32 id CIM-style scoping expects
		# elsewhere. COMMAND here is the bare executable path with no arguments, so substring matches against
		# a DLL/switch name (e.g. "MSBuild.BuildHost.dll") won't work through this path -- only the plain
		# regex name matches (VBCSCompiler et al.) will.
		ps -W 2>/dev/null | tail -n +2 | awk '{winpid=$4; cmd=$8; for (i=9;i<=NF;i++) cmd=cmd" "$i; if (winpid!="") print winpid, cmd}' | _cleanse_exclude_self_matchers
		return
	  fi
	  ps -eo pid,command 2>/dev/null | tail -n +2 | _cleanse_exclude_self_matchers
	}
	_cleanse_pids_matching_regex() {  # PIDs of processes (from a snapshot already on stdin) whose command line matches an extended regex
	  grep -Ei -- "$1" | awk 'NF{print $1}' || true  # `|| true`: zero matches is the common case, not a failure
	}
	_cleanse_pids_matching_all() {  # PIDs of processes (from a snapshot already on stdin) whose command line contains every literal substring given as an argument (no regex escaping needed for paths). Case-insensitive on Windows: NTFS paths are case-insensitive at the OS level (matches folly.ps1's Get-PidsMatchingAll), while two Unix checkouts can legitimately differ only by path casing so stay case-sensitive there
	  local lines pat grep_flags="-F"
	  is_windows_host && grep_flags="-Fi"
	  lines=$(cat)
	  for pat in "$@"; do
		lines=$(printf '%s\n' "$lines" | grep $grep_flags -- "$pat" || true)
	  done
	  printf '%s\n' "$lines" | awk 'NF{print $1}'
	}
	build_server_pattern='VBCSCompiler|Microsoft\.CodeAnalysis\.Razor\.[A-Za-z.]*Server|[[:space:]]rzc(\.dll)?([[:space:]]|$)'
	_cleanse_pids_matching_regex_and_substring() {  # PIDs (from a snapshot already on stdin) whose command line matches an extended regex AND contains a literal substring -- used to scope the build-server name pattern to this checkout's own bootstrapped SDK, so a force-kill can never reach some other checkout's or tool's build server. Same Windows case-insensitivity as _cleanse_pids_matching_all
	  local pattern="$1" substr="$2" grep_flags="-F"
	  is_windows_host && grep_flags="-Fi"
	  grep -Ei -- "$pattern" | grep $grep_flags -- "$substr" | awk 'NF{print $1}' || true
	}
	_cleanse_get_ppid() {  # `ps -o ppid=` is a procps custom-field lookup -- MSYS/Cygwin's `ps` doesn't support it (see _cleanse_ps_snapshot), so the ancestor walk below would silently stop after this script's own PID on Git-Bash and lose its self-protection past that point; ask Windows itself via CIM there instead, same as _cleanse_ps_snapshot's fallback
	  local pid="$1" pwsh_exe
	  if is_windows_host; then
		pwsh_exe=$(_cleanse_pwsh_exe)
		if [[ -n "$pwsh_exe" ]]; then
		  "$pwsh_exe" -NoProfile -NonInteractive -Command "(Get-CimInstance Win32_Process -Filter \"ProcessId=$pid\").ParentProcessId" 2>/dev/null | tr -d '[:space:]'
		  return
		fi
		# No usable PowerShell -- same ps -W fallback as _cleanse_ps_snapshot/_cleanse_get_children:
		# `ps -o ppid= -p` is unsupported syntax here too. Find the row whose WINPID (native id, column 4)
		# matches $pid and print its PPID (column 2), best-effort like the rest of this fallback.
		ps -W 2>/dev/null | tail -n +2 | awk -v p="$pid" '$4==p{print $2; exit}'
		return
	  fi
	  ps -o ppid= -p "$pid" 2>/dev/null | tr -d '[:space:]'
	}
	_cleanse_ancestor_pids() {  # walks the PPID chain from this script's own native PID ($native_self_pid, computed once in the main shell -- not via any $(...) command substitution, see its definition) up to and including PID 1 (or as far as it can resolve) -- kill candidates get filtered against this set so cleanse can never terminate its own invoking shell/CI agent, even if that ancestor's command line happens to match the build-server/node-worker patterns (e.g. an automation wrapper that embeds the search text in its own argv). PID 1 is deliberately included, not just a loop bound: in a container where the invoking CI agent *is* PID 1, leaving it unprotected would make the container's own init process a killable candidate.
	  local pid="$native_self_pid" ppid seen=" "
	  while [[ -n "$pid" && "$pid" != "0" && "$seen" != *" $pid "* ]]; do
		printf '%s\n' "$pid"
		seen="$seen$pid "
		[[ "$pid" == "1" ]] && break
		ppid=$(_cleanse_get_ppid "$pid")
		pid="$ppid"
	  done
	}
	_cleanse_get_children() {  # PIDs of the direct children of $1 -- `ps -eo pid,ppid | awk` is procps/BSD syntax and, even patched, MSYS/Cygwin ps only sees processes in its own runtime's pid table (see _cleanse_ps_snapshot); a build-server/node-worker/BuildHost child spawned via .NET's own Process.Start is invisible to it just like the parent PIDs those found before this fix. Same CIM fallback as _cleanse_get_ppid.
	  local pid="$1" pwsh_exe
	  if is_windows_host; then
		pwsh_exe=$(_cleanse_pwsh_exe)
		if [[ -n "$pwsh_exe" ]]; then
		  # Windows PowerShell (not pwsh 7+) writes CRLF line endings here; command substitution/piping only
		  # strips a *trailing* LF, leaving a stray \r on each pid this multi-line output returns (unlike
		  # _cleanse_get_ppid's single-value `tr -d '[:space:]'`, which can't be reused as-is here since it
		  # would also delete the newlines separating multiple children into one unusable blob). A pid of
		  # "1234\r" fails every subsequent `tasklist`/`taskkill` match by a hair, silently leaving that
		  # child unsignaled. `tr -d '\r'` strips only the carriage return, keeping one pid per line intact.
		  "$pwsh_exe" -NoProfile -NonInteractive -Command "(Get-CimInstance Win32_Process -Filter \"ParentProcessId=$pid\").ProcessId" 2>/dev/null | tr -d '\r' && return
		fi
		# No usable PowerShell -- same MSYS/Cygwin `-W` fallback as _cleanse_ps_snapshot: `ps -eo pid,ppid`
		# isn't supported syntax here either. -W's fixed columns are PID PPID PGID WINPID TTY UID STIME
		# COMMAND; WINPID (column 4) is the native id everything else in this Windows-host path keys off of,
		# matched against PPID (column 2) to find $1's children, best-effort like the rest of this fallback.
		ps -W 2>/dev/null | tail -n +2 | awk -v p="$pid" '{if ($2==p) print $4}'
		return
	  fi
	  ps -eo pid,ppid 2>/dev/null | awk -v p="$pid" '$2==p{print $1}'
	}
	_cleanse_native_alive() {  # MSYS/Cygwin's `kill -0` only resolves PIDs known to its own runtime's process table -- a native Win32 PID discovered via CIM (e.g. a BuildHost that never touched msys-2.0.dll) isn't in it, so liveness/signals have to go through Windows' own tools instead
	  tasklist //FI "PID eq $1" //NH 2>/dev/null | grep -q -- "$1"
	}
	_cleanse_native_term() {  # graceful stop -- taskkill without //F, mirroring kill -TERM's "ask nicely first"
	  taskkill //PID "$1" >/dev/null 2>&1
	}
	_cleanse_native_kill() {  # forceful stop -- taskkill //F, mirroring kill -KILL
	  taskkill //F //PID "$1" >/dev/null 2>&1
	}
	_cleanse_kill_pid_tree() {  # kills a pid's children first (via _cleanse_get_children) then the pid itself, TERM then escalating to KILL if it's still alive after a short wait -- only reports success once the pid is confirmed gone AND this call actually had to signal it, since a delivered signal (kill's own exit code) doesn't mean the process actually died (e.g. it traps/ignores TERM), and a candidate that exited on its own between snapshot and kill attempt (e.g. the shutdown RPC took effect a little late) was never force-killed by cleanse at all. On a Windows host this all goes through tasklist/taskkill instead of bash's kill builtin/`kill -0`, which -- like the ps-based PID discovery above -- can't see or signal a native Win32 PID CIM found but MSYS never tracked
	  local pid="$1" child deadline
	  [[ -z "$pid" ]] && return 1
	  for child in $(_cleanse_get_children "$pid"); do
		_cleanse_kill_pid_tree "$child"
	  done
	  if is_windows_host; then
		local signaled=0
		_cleanse_native_alive "$pid" || return 1
		_cleanse_native_term "$pid" && signaled=1  # taskkill itself reports failure (e.g. "not found") when the target already exited between the alive-check above and this call -- track that rather than assuming our own signal is what did it
		deadline=$((SECONDS + 5))
		while _cleanse_native_alive "$pid" && (( SECONDS < deadline )); do
		  sleep 0.2
		done
		if _cleanse_native_alive "$pid"; then
		  _cleanse_native_kill "$pid" && signaled=1
		  sleep 0.2
		fi
		_cleanse_native_alive "$pid" && return 1  # still alive after both attempts -- not killed, whatever signaled says
		(( signaled ))  # gone, but only count it if one of our own taskkill calls actually reported hitting it -- otherwise it exited on its own and cleanse never force-killed it at all, matching the Unix branch's "actually had to signal it" invariant below
		return
	  fi
	  kill -0 "$pid" 2>/dev/null || return 1  # already gone on its own -- nothing for this call to count as killed
	  kill -TERM "$pid" 2>/dev/null
	  deadline=$((SECONDS + 5))
	  while kill -0 "$pid" 2>/dev/null && (( SECONDS < deadline )); do
		sleep 0.2
	  done
	  if kill -0 "$pid" 2>/dev/null; then
		kill -KILL "$pid" 2>/dev/null
		sleep 0.2
	  fi
	  ! kill -0 "$pid" 2>/dev/null
	}
	ancestor_pids=$(_cleanse_ancestor_pids)
	if [[ -n "$dotnet_exe" ]]; then  # `build-server shutdown` always reports success whether or not a server was actually running, so its own output can't say what happened -- diff the PIDs before/after instead
	  before_pids=$(_cleanse_ps_snapshot | _cleanse_pids_matching_regex "$build_server_pattern")
	  "$dotnet_exe" build-server shutdown >/dev/null 2>&1 || true
	  if [[ -n "$before_pids" ]]; then
		after_pids=$(_cleanse_ps_snapshot | _cleanse_pids_matching_regex "$build_server_pattern")
		stopped=$(comm -23 <(sort -u <<<"$before_pids") <(sort -u <<<"$after_pids") | grep -c '^[0-9]\+$' || true)
		if [[ "${stopped:-0}" -gt 0 ]]; then
		  echo "Stopped $stopped build server process(es) (VBCSCompiler/Razor) via 'dotnet build-server shutdown'."
		fi
		# `build-server shutdown` talks to the RPC pipe of servers registered by *this* SDK; a server started
		# by a different dotnet install (or one whose RPC pipe is already wedged/orphaned) doesn't respond to
		# it and survives silently. Force-killing is scoped tightly to avoid collateral damage: only a PID that
		# (a) was already alive in the *original* before_pids snapshot -- never one that merely appears in a
		# later snapshot, which could be an unrelated process that started in between -- (b) is still alive
		# after the shutdown call, and (c) belongs to this checkout's own bootstrapped `.dotnet` SDK (the same
		# scope MSBuild node-reuse workers below are held to) is unconditionally stale and gets force-killed.
		# A build server for a different repo/SDK is left alone even if its name matches the pattern. The
		# trailing "/" on the scope substring is required, not cosmetic: without it, a sibling directory whose
		# name merely starts with ".dotnet" (e.g. a ".dotnet-old" leftover from a prior bootstrap) would also
		# match, since "$scriptroot/.dotnet-old/..." contains "$scriptroot/.dotnet" as a plain substring.
		scoped_after_pids=$(_cleanse_ps_snapshot | _cleanse_pids_matching_regex_and_substring "$build_server_pattern" "$scriptroot_dotnet_scope")
		survivor_pids=$(comm -12 <(sort -u <<<"$before_pids") <(sort -u <<<"$scoped_after_pids"))
		# Never kill this script's own invoking shell/CI agent, even if it happens to match the scoped
		# pattern above (e.g. an automation wrapper whose own command line embeds the search text).
		survivor_pids=$(comm -23 <(sort -u <<<"$survivor_pids") <(sort -u <<<"$ancestor_pids"))
		if [[ -n "$survivor_pids" ]]; then
		  force_killed=0
		  while IFS= read -r pid; do
			[[ -z "$pid" ]] && continue
			_cleanse_kill_pid_tree "$pid" && force_killed=$((force_killed + 1))
		  done <<< "$survivor_pids"
		  if [[ "$force_killed" -gt 0 ]]; then
			echo "Force-killed $force_killed build server process(es) that ignored 'dotnet build-server shutdown'."
		  fi
		fi
	  fi
	fi
	# Node-reuse MSBuild worker processes are a different mechanism from build servers above -- left behind by
	# any dotnet/MSBuild invocation that didn't pass --nodeReuse false (an IDE build, a bare `dotnet build`/
	# `dotnet test`, `dotnet run --file eng/generate-compiler-code.cs`, ...) -- and are never registered as
	# build servers, so `build-server shutdown` can't see or stop them. cleanse itself never launches a build,
	# so any live MSBuild.dll worker rooted at this repo's own bootstrapped SDK is unconditionally stale.
	# Trailing "/" on the scope substring is required for the same reason as the build-server scope above --
	# without it, a ".dotnet-old"-style sibling directory would falsely match too.
	node_worker_pids=$(_cleanse_ps_snapshot | _cleanse_pids_matching_all "$scriptroot_dotnet_scope" "MSBuild.dll")
	node_worker_pids=$(comm -23 <(sort -u <<<"$node_worker_pids") <(sort -u <<<"$ancestor_pids"))  # never kill this script's own invoking shell/CI agent -- see the build-server exclusion above
	if [[ -n "$node_worker_pids" ]]; then
	  killed=0
	  while IFS= read -r pid; do
		[[ -z "$pid" ]] && continue
		_cleanse_kill_pid_tree "$pid" && killed=$((killed + 1))
	  done <<< "$node_worker_pids"
	  if [[ "$killed" -gt 0 ]]; then
		echo "Killed $killed leftover MSBuild node-reuse worker process(es)."
	  fi
	fi
	# BuildHost processes (Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll, launched by
	# BuildHostProcessManager as a plain `dotnet <dll>` child -- shown as ".NET Host" in Task Manager on
	# Windows) are a third, separate mechanism from both above: they're spawned on demand by MSBuildWorkspace
	# consumers (e.g. the LanguageServer's ProcessHost tests) to load projects, never register as build
	# servers, and don't match the MSBuild.dll node-reuse pattern either (their own assembly is
	# MSBuild.BuildHost.dll, loaded from this checkout's own artifacts/bin/.../BuildHost-netcore/ output). A
	# host whose test process was killed/debugged-out from under it can survive indefinitely holding that
	# output's DLLs open, which cleanse can otherwise never explain (build-server shutdown doesn't see it, and
	# it isn't a node-reuse worker). cleanse itself never launches one, so any live match rooted at this
	# checkout's own artifacts/ is unconditionally stale.
	buildhost_pids=$(_cleanse_ps_snapshot | _cleanse_pids_matching_all "$artifacts_scope" "MSBuild.BuildHost.dll")
	buildhost_pids=$(comm -23 <(sort -u <<<"$buildhost_pids") <(sort -u <<<"$ancestor_pids"))  # never kill this script's own invoking shell/CI agent -- see the build-server exclusion above
	if [[ -n "$buildhost_pids" ]]; then
	  killed=0
	  while IFS= read -r pid; do
		[[ -z "$pid" ]] && continue
		_cleanse_kill_pid_tree "$pid" && killed=$((killed + 1))
	  done <<< "$buildhost_pids"
	  if [[ "$killed" -gt 0 ]]; then
		echo "Killed $killed leftover MSBuild BuildHost process(es)."
	  fi
	fi
	if [[ -e "$artifacts_dir" || -L "$artifacts_dir" ]] && { [[ ! -d "$artifacts_dir" ]] || [[ -L "$artifacts_dir" ]]; }; then
	  rm -rf -- "$artifacts_dir" || true  # a regular file or a symlink doesn't need the enumeration/progress machinery below -- just remove the single entry
	  if [[ -e "$artifacts_dir" || -L "$artifacts_dir" ]]; then
		echo "Failed to remove '$artifacts_dir'." >&2
		exit 1
	  fi
	  echo "Cleansed artefacts."
	  exit 0
	fi
	if [[ -d "$artifacts_dir" ]]; then
	  interactive=0
	  [[ -t 1 ]] && interactive=1
	  _cleanse_kill_tree() {  # kills a pid's children first then the pid itself, so Ctrl+C can't orphan a still-traversing find/awk pipeline behind a killed wrapper subshell. Deliberately NOT routed through _cleanse_get_children/CIM the way _cleanse_kill_pid_tree is: scan_pid/rm_pid and their descendants are this bash process's own job-control children (`$!`), always tracked by the MSYS/Cygwin runtime regardless of host -- unlike a CIM-discovered build-server/node-worker/BuildHost survivor, they were never invisible to plain `ps` in the first place, and querying CIM with an MSYS pid it doesn't recognize (a different pid namespace) would just find nothing
		local pid="$1" child
		[[ -z "$pid" ]] && return 0
		for child in $(ps -eo pid,ppid 2>/dev/null | awk -v p="$pid" '$2==p{print $1}'); do
		  _cleanse_kill_tree "$child"
		done
		kill "$pid" 2>/dev/null
		return 0
	  }
	  _cleanse_kill_bg() {
		_cleanse_kill_tree "${scan_pid:-}"
		_cleanse_kill_tree "${rm_pid:-}"
		[[ -n "${scan_tmp:-}" && -e "${scan_tmp:-}" ]] && rm -f "$scan_tmp"
		[[ -n "${rm_fifo:-}" && -e "${rm_fifo:-}" ]] && rm -f "$rm_fifo"
		return 0
	  }
	  trap '_cleanse_kill_bg; exit 130' INT  # background jobs run with job control off (non-interactive script), so POSIX has them ignore SIGINT/SIGQUIT -- forward it explicitly or Ctrl+C would kill only this foreground script
	  trap '_cleanse_kill_bg; exit 143' TERM
	  format_bytes() {
		local bytes=$1
		if (( bytes >= 1073741824 )); then
		  awk -v b="$bytes" 'BEGIN { printf "%.2f GiB", b / 1073741824 }'
		elif (( bytes >= 1048576 )); then
		  awk -v b="$bytes" 'BEGIN { printf "%.2f MiB", b / 1048576 }'
		elif (( bytes >= 1024 )); then
		  awk -v b="$bytes" 'BEGIN { printf "%.2f KiB", b / 1024 }'
		else
		  printf "%d B" "$bytes"
		fi
	  }
	  gnu_find=0
	  find "$scriptroot" -maxdepth 0 -printf '' >/dev/null 2>&1 && gnu_find=1  # GNU find's -printf gives file sizes directly; BSD find (macOS) lacks it, so dir_stats below falls back to a batched stat call
	  if (( gnu_find )); then
		dir_stats() {  # one find|awk pipeline, not a bash loop stat-ing each file -- that per-file overhead was what made cleanse feel slower than plain rm -rf/Explorer
		  local status
		  if find "$1" -type f -printf '%s\n' 2>/dev/null | awk '{s+=$1; n++} END{printf "%d %d", s+0, n+0}'; then  # pipefail means this "fails" whenever find hits a permission-denied subtree even though awk itself always exits 0 -- run as an if so that can't trip set -e
			status=0
		  else
			status=${PIPESTATUS[0]}  # find's own exit code, captured before anything else can overwrite it
		  fi
		  printf ' %d\n' "$(( status == 0 ? 1 : 0 ))"  # trailing "ok" flag: a partial/truncated scan must never be trusted as if it were exact
		}
	  else
		dir_stats() {
		  local status
		  if find "$1" -type f -print0 2>/dev/null | xargs -0 stat -f%z 2>/dev/null | awk '{s+=$1; n++} END{printf "%d %d", s+0, n+0}'; then  # both find and xargs must succeed -- xargs happily stats whatever partial filename list a failing find handed it
			status=0
		  else
			status=$(( PIPESTATUS[0] != 0 || PIPESTATUS[1] != 0 ? 1 : 0 ))
		  fi
		  printf ' %d\n' "$(( status == 0 ? 1 : 0 ))"
		}
	  fi
	  spinner_frames=('|' '/' '-' '\')
	  spinner_index=0
	  scan_tmp=$(mktemp)
	  ( dir_stats "$artifacts_dir" > "$scan_tmp" ) &
	  scan_pid=$!
	  if (( interactive )); then
		printf '\r\033[KScanning artefacts %s' "${spinner_frames[$spinner_index]}"
		while kill -0 "$scan_pid" 2>/dev/null; do
		  sleep 0.15
		  spinner_index=$(( (spinner_index + 1) % ${#spinner_frames[@]} ))
		  printf '\r\033[KScanning artefacts %s' "${spinner_frames[$spinner_index]}"
		done
	  fi
	  wait "$scan_pid" || true
	  read -r total_bytes total_count _ < "$scan_tmp"
	  rm -f "$scan_tmp"
	  total_formatted=$(format_bytes "$total_bytes")
	  deleted_bytes=0
	  deleted_count=0
	  start_time=$(date +%s)
	  if (( gnu_find )); then
		rm_fifo=$(mktemp -u)
		mkfifo "$rm_fifo"  # single traversal that both deletes and reports each file's size as it goes -- no second, concurrently racing rescan (that contention, not live progress itself, was the source of the old jumpy redraw)
		( find "$artifacts_dir" -depth -type f -printf '%s\n' -delete 2>/dev/null || true; find "$artifacts_dir" -depth -type d -empty -delete 2>/dev/null || true; printf 'DONE\n' ) > "$rm_fifo" &
		rm_pid=$!
		redraw_every=$(( total_count / 100 > 0 ? total_count / 100 : 1 ))  # ~100 redraws over the run, not one per file (format_bytes spawns awk -- exactly the per-file cost this exists to avoid)
		since_redraw=0
		(( interactive )) && printf '\r\033[KCleansing artefacts %d / %d files, %s / %s' "$deleted_count" "$total_count" "$(format_bytes "$deleted_bytes")" "$total_formatted"
		while IFS= read -r line; do
		  [[ "$line" == DONE ]] && break
		  deleted_bytes=$(( deleted_bytes + line ))
		  deleted_count=$(( deleted_count + 1 ))
		  since_redraw=$(( since_redraw + 1 ))
		  if (( interactive )) && (( since_redraw >= redraw_every )); then
			since_redraw=0
			elapsed=$(( $(date +%s) - start_time ))
			bytes_per_second=$(( elapsed > 0 ? deleted_bytes / elapsed : 0 ))
			printf '\r\033[KCleansing artefacts %d / %d files, %s / %s, %s/s' "$deleted_count" "$total_count" "$(format_bytes "$deleted_bytes")" "$total_formatted" "$(format_bytes "$bytes_per_second")"
		  fi
		done < "$rm_fifo"
		rm -f "$rm_fifo"
		(( interactive )) && printf '\r\033[K'
		wait "$rm_pid" || true
	  else
		rm -rf "$artifacts_dir" &  # BSD find has no -printf, so it can't report a deleted file's size in the same pass as -delete -- fall back to a plain rm -rf with a spinner, not a periodic full-tree rescan (that rescan-while-deleting contention was what made cleanse slow on macOS/BSD)
		rm_pid=$!
		deleted_bytes=$total_bytes
		deleted_count=$total_count
		if (( interactive )); then
		  spinner_index=0
		  printf '\r\033[KCleansing artefacts %s' "${spinner_frames[$spinner_index]}"
		  while kill -0 "$rm_pid" 2>/dev/null; do
			sleep 0.15
			spinner_index=$(( (spinner_index + 1) % ${#spinner_frames[@]} ))
			printf '\r\033[KCleansing artefacts %s' "${spinner_frames[$spinner_index]}"
		  done
		  printf '\r\033[K'
		fi
		wait "$rm_pid" || true
	  fi
	  if [[ -d "$artifacts_dir" ]]; then
		rm -rf "$artifacts_dir" 2>/dev/null || true  # a transiently held lock (e.g. an antivirus scanner) can clear between the first pass and now -- retry once before reporting survivors
	  fi
	  if [[ -d "$artifacts_dir" ]]; then
		read -r remaining_bytes remaining_count remaining_ok <<< "$(dir_stats "$artifacts_dir")"
		deleted_bytes=$(( total_bytes > remaining_bytes ? total_bytes - remaining_bytes : 0 ))  # clamp: a concurrent process (e.g. another build) can add bytes back after the initial snapshot
		if (( remaining_ok )); then
		  echo "Cleansed $(format_bytes "$deleted_bytes") of artefacts; $remaining_count file(s) could not be removed."
		else
		  echo "Cleansed $(format_bytes "$deleted_bytes") of artefacts; at least $remaining_count file(s) could not be removed (some may be unreadable and not counted)."  # find failed partway, so remaining_count is a lower bound, not exact
		fi
		exit 1
	  else
		echo "Cleansed $total_formatted of artefacts."
	  fi
	else
	  echo "No artefacts to cleanse."
	fi
	exit 0
	;;
  *)
	echo "Unrecognized action '$action'. Expected 'attune', 'weave', 'reweave', 'bind', 'scry', 'cleanse', or 'grimoire'." >&2
	exit 1
	;;
esac
if [[ "$action" == "bind" ]]; then
  packages_dir="$scriptroot/artifacts/packages/$configuration"
  if [[ ! -d "$packages_dir" ]]; then
	echo "Package output directory '$packages_dir' does not exist." >&2
	exit 1
  fi
  mkdir -p "$nupkg_dir"
  rsync -a --delete "$packages_dir"/ "$nupkg_dir"/
fi
