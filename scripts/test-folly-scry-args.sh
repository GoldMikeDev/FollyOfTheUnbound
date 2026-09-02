#!/usr/bin/env bash
# Regression test for folly.sh's argument parsing (action, primary arg, --timeout -- including
# leading-zero normalization, missing/invalid values, and non-scry rejection -- plus --binaryLog
# and --verbosity, forwarded across every build-invoking action and rejected on 'cleanse' and
# 'scry reflection'), run against a mocked eng/build.sh so no real build/test happens. Bash counterpart to
# scripts/test-folly-scry-args.ps1 -- see the folly.sh/folly.ps1 parity rule in CONVENTIONS.md for
# why this needs to stay in lockstep with that harness rather than being PowerShell-only.
# Run by hand (or wire into CI) after touching folly.sh's argument parsing or scry action:
#   bash ./scripts/test-folly-scry-args.sh
set -euo pipefail

script_root="$(cd -P "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
folly_sh="$script_root/folly.sh"

work_root="$(mktemp -d)"
trap 'rm -rf "$work_root"' EXIT

if [[ -t 1 ]]; then  # matches folly.sh cleanse's own [[ -t 1 ]] check -- plain text when redirected/piped (e.g. a CI log), colored in an interactive terminal
  color_green=$'\033[32m'; color_red=$'\033[31m'; color_reset=$'\033[0m'
else
  color_green=''; color_red=''; color_reset=''
fi

pass_count=0
fail_count=0

test_pass() {
  echo "${color_green}PASS: $1${color_reset}"
  pass_count=$((pass_count + 1))
}

test_fail() {
  echo "${color_red}FAIL: $1${color_reset}"
  fail_count=$((fail_count + 1))
}

# A minimal stand-in for eng/build.sh: records every argument it was invoked with (one per line,
# so the harness can assert exactly what folly.sh forwarded -- e.g. that --testTimeout carries the
# normalized decimal value, not a leading-zero string) and always exits 0, without running any
# actual build or tests.
new_test_case() {
  local name="$1"
  local dir="$work_root/$name"
  rm -rf "$dir"
  mkdir -p "$dir/eng"
  cp "$folly_sh" "$dir/folly.sh"
  # Appends (not overwrites): folly.sh's 'scry' now invokes this mock more than once (a build-only
  # call, then one --test/--testDesktop call per leg), so every case that only cares whether some
  # switch was ever forwarded (the vast majority) still works unmodified against the concatenated
  # log; a "===call===" separator lets the newer --core/--framework cases below tell invocations
  # apart when they need to (e.g. asserting --testDesktop was never passed at all).
  # For the --test/--testDesktop calls, also writes the runtestsCore.log/runtestsFramework.log
  # RunTests now emits (see Program.WriteLogFile) with exactly one PASSED row so folly.sh's new
  # get_test_summary reader has something real to parse, without running any actual build or tests.
  cat > "$dir/eng/build.sh" <<'MOCK'
#!/usr/bin/env bash
{ printf '%s\n' "$@"; printf -- '===call===\n'; } >> "$(dirname "$0")/../build-args.log"
repo_root="$(cd -P "$(dirname "$0")/.." && pwd)"
config="Debug"
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
  if [[ "${args[$i]}" == "--configuration" ]]; then
    config="${args[$((i + 1))]}"
  fi
done
suffix="${FOTU_TEST_RESULTS_SUFFIX:-}"
log_dir="$repo_root/artifacts/log/$config-$suffix"
write_fake_log() {
  mkdir -p "$log_dir" "$repo_root/artifacts/TestResults/$config-$suffix"
  cat > "$log_dir/$1" <<'LOG'
================
Assembly.Fake.UnitTests_0   PASSED   00:01
================
Extra run diagnostics for logging, did not impact run results
LOG
}
for arg in "$@"; do
  if [[ "$arg" == "--test" ]]; then
    write_fake_log "runtestsCore.log"
    exit 0
  elif [[ "$arg" == "--testDesktop" ]]; then
    write_fake_log "runtestsFramework.log"
    exit 0
  fi
done
exit 0
MOCK
  chmod +x "$dir/eng/build.sh" "$dir/folly.sh"
  echo "$dir"
}

invoke_folly() {
  local dir="$1"
  shift
  local output exit_code
  output="$(cd "$dir" && bash ./folly.sh "$@" 2>&1)"
  exit_code=$?
  printf '%s\x1e%s' "$exit_code" "$output"
}

run_case() {
  local dir="$1"
  shift
  invoke_folly "$dir" "$@"
}

# Same as invoke_folly, but forces is_windows_host() to true via $OSTYPE -- for exercising the
# both-legs (Core + Framework) path that this (non-Windows) sandbox would otherwise never reach.
invoke_folly_windows() {
  local dir="$1"
  shift
  local output exit_code
  output="$(cd "$dir" && OSTYPE=msys bash ./folly.sh "$@" 2>&1)"
  exit_code=$?
  printf '%s\x1e%s' "$exit_code" "$output"
}

# --- default: no --testTimeout forwarded ---
dir="$(new_test_case "default")"
result="$(run_case "$dir" scry research)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" && "$args_log" != *"--testTimeout"* ]]; then
  test_pass "default 'scry' forwards no --testTimeout"
else
  test_fail "default 'scry' (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --timeout is forwarded to eng/build.sh ---
dir="$(new_test_case "timeout-forwarded")"
result="$(run_case "$dir" scry research --timeout 180)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--testTimeout" <<<"$args_log" && grep -qx "180" <<<"$args_log"; then
  test_pass "'--timeout 180' is forwarded to ./eng/build.sh as --testTimeout 180"
else
  test_fail "timeout forwarding (exit=$exit_code): args='$args_log' output=$output"
fi

# --- positional primary arg alongside --timeout ---
dir="$(new_test_case "positional-config")"
result="$(run_case "$dir" scry truth --timeout 90)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx "Release" <<<"$args_log"; then
  test_pass "'scry truth --timeout 90' selects Release and still forwards the timeout"
else
  test_fail "positional config (exit=$exit_code): args='$args_log' output=$output"
fi

# --- leading-zero timeout values are normalized to decimal, not misparsed as octal ---
dir="$(new_test_case "timeout-leading-zero")"
result="$(run_case "$dir" scry research --timeout 08)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx "8" <<<"$args_log" && ! grep -qx "08" <<<"$args_log"; then
  test_pass "'--timeout 08' is normalized to decimal 8, not misparsed as octal"
else
  test_fail "leading-zero timeout (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --timeout with a missing value is rejected ---
dir="$(new_test_case "timeout-missing-value")"
result="$(run_case "$dir" scry --timeout)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"requires a"* ]]; then
  test_pass "'--timeout' with no value is rejected"
else
  test_fail "timeout missing value (exit=$exit_code): $output"
fi

# --- --timeout with a non-numeric value is rejected ---
dir="$(new_test_case "timeout-invalid-value")"
result="$(run_case "$dir" scry --timeout banana)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"positive integer minute count"* ]]; then
  test_pass "'--timeout banana' is rejected"
else
  test_fail "timeout invalid value (exit=$exit_code): $output"
fi

# --- --timeout rejected for non-scry actions ---
dir="$(new_test_case "timeout-on-non-scry")"
result="$(run_case "$dir" weave --timeout 180)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"only valid with the 'scry' action"* ]]; then
  test_pass "'--timeout' is rejected on a non-scry action"
else
  test_fail "timeout on non-scry action (exit=$exit_code): $output"
fi

# --- default 'scry' on this (non-Windows) sandbox runs Core only, never --testDesktop ---
dir="$(new_test_case "default-core-only")"
result="$(run_case "$dir" scry research)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--test" <<<"$args_log" && ! grep -qx -- "--testDesktop" <<<"$args_log" \
  && ! grep -qx -- "--testSuppressConsoleSummary" <<<"$args_log" \
  && [[ "$output" == *"Core: 1 passed"* ]] && [[ "$output" != *"Framework:"* ]]; then
  test_pass "default 'scry' off-Windows runs Core only, never --testDesktop"
else
  test_fail "default core-only (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --core only: still just the Core leg ---
dir="$(new_test_case "core-only")"
result="$(run_case "$dir" scry research --core)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--test" <<<"$args_log" && ! grep -qx -- "--testDesktop" <<<"$args_log" \
  && ! grep -qx -- "--testSuppressConsoleSummary" <<<"$args_log" \
  && [[ "$output" == *"Core: 1 passed"* ]] && [[ "$output" != *"Framework:"* ]]; then
  test_pass "'scry --core' runs only Core"
else
  test_fail "'scry --core' (exit=$exit_code): args='$args_log' output=$output"
fi

# --- both legs (forced Windows host): combined table shows both, per-leg blocks, correct totals ---
dir="$(new_test_case "both-legs-windows")"
result="$(invoke_folly_windows "$dir" scry research)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" && "$output" == *"=== Core results ==="* && "$output" == *"=== Framework results ==="* \
  && "$output" == *"Core: 1 passed, 0 failed, 0 timeout"* && "$output" == *"Framework: 1 passed, 0 failed, 0 timeout"* \
  && "$output" == *"Overall: 2 passed, 0 failed, 0 timeout"* ]] \
  && grep -qx -- "--testSuppressConsoleSummary" <<<"$args_log"; then
  test_pass "default 'scry' on a forced-Windows host runs both legs with a combined summary"
else
  test_fail "both-legs-windows (exit=$exit_code): args='$args_log' output=$output"
fi

# --- both legs, unequal name-column widths: combined tables realign to a shared Status/Elapsed
# column position (see folly.sh's result_row_pattern realignment block) instead of each leg's
# already-formatted table keeping its own leg-local width (TestRunner.Print sizes each leg's name
# column to that leg's own longest name, so a longer Framework name would otherwise push its
# Status/Elapsed columns further right than Core's). Rows built with the same
# FitName/CenterPad-equivalent formatting RunTests itself writes to the log, one narrow (Core,
# floored at the real MinSummaryNameColumnWidth=75) and one wide (Framework, past that floor).
dir="$(new_test_case "realign-unequal-widths")"
center_pad() {
  local text="$1" width="$2" pad left right
  pad=$((width - ${#text}))
  left=$((pad / 2))
  right=$((pad - left))
  printf '%*s%s%*s' "$left" "" "$text" "$right" ""
}
fit_name() {
  printf '%-*s' "$2" "$1"
}
core_name="Short.Fake.UnitTests_0"
framework_name="Very.Long.Namespace.That.Pushes.Well.Past.The.Seventyfive.Character.Floor.Fake.UnitTests_0"
core_width=75
framework_width=${#framework_name}
core_row="$(fit_name "$core_name" "$core_width") $(center_pad "PASSED" 10) $(center_pad "00:01" 10)"
framework_row="$(fit_name "$framework_name" "$framework_width") $(center_pad "PASSED" 10) $(center_pad "00:02" 10)"
cat > "$dir/eng/build.sh" <<MOCK
#!/usr/bin/env bash
repo_root="\$(cd -P "\$(dirname "\$0")/.." && pwd)"
for arg in "\$@"; do
  if [[ "\$arg" == "--test" ]]; then
    log_dir="\$repo_root/artifacts/log/Debug-Core"
    mkdir -p "\$log_dir" "\$repo_root/artifacts/TestResults/Debug-Core"
    cat > "\$log_dir/runtestsCore.log" <<LOG
================
$core_row
================
Extra run diagnostics for logging, did not impact run results
LOG
    exit 0
  elif [[ "\$arg" == "--testDesktop" ]]; then
    log_dir="\$repo_root/artifacts/log/Debug-Framework"
    mkdir -p "\$log_dir" "\$repo_root/artifacts/TestResults/Debug-Framework"
    cat > "\$log_dir/runtestsFramework.log" <<LOG
================
$framework_row
================
Extra run diagnostics for logging, did not impact run results
LOG
    exit 0
  fi
done
exit 0
MOCK
chmod +x "$dir/eng/build.sh"
result="$(invoke_folly_windows "$dir" scry research)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
core_status_col="$(grep -F "$core_name" <<<"$output" | grep -boE 'PASSED' | head -1 | cut -d: -f1)"
framework_status_col="$(grep -F "$framework_name" <<<"$output" | grep -boE 'PASSED' | head -1 | cut -d: -f1)"
if [[ "$exit_code" == "0" && -n "$core_status_col" && "$core_status_col" == "$framework_status_col" ]]; then
  test_pass "combined Core/Framework tables realign to the same Status column despite unequal name widths"
else
  test_fail "realign-unequal-widths (exit=$exit_code): core_col=$core_status_col framework_col=$framework_status_col output=$output"
fi

# --- stray "================" lines in captured failure output don't fool the summary parser ---
dir="$(new_test_case "false-marker")"
cat > "$dir/eng/build.sh" <<'MOCK'
#!/usr/bin/env bash
repo_root="$(cd -P "$(dirname "$0")/.." && pwd)"
for arg in "$@"; do
  if [[ "$arg" == "--test" ]]; then
    log_dir="$repo_root/artifacts/log/Debug-Core"
    mkdir -p "$log_dir" "$repo_root/artifacts/TestResults/Debug-Core"
    cat > "$log_dir/runtestsCore.log" <<'LOG'
Errors Assembly.CoreClr.UnitTests_0
some test printed a divider as part of its own diagnostic output:
================
unrelated captured text that happens to be between two stray markers
================
Command: dotnet test ...
================
Assembly.CoreClr.UnitTests_0                                                FAILED       00:34
Assembly.CoreClr.UnitTests_1                                                PASSED       00:12
================
Extra run diagnostics for logging, did not impact run results
### Begin logging executed process details
### Standard Output
================
raw xunit console output that also happens to contain a divider line
================
### End logging executed process details
LOG
    exit 1
  fi
done
exit 0
MOCK
chmod +x "$dir/eng/build.sh"
result="$(run_case "$dir" scry research --core)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"Core: 1 passed, 1 failed, 0 timeout"* ]]; then
  test_pass "stray markers in captured failure output are not mistaken for the summary table"
else
  test_fail "false-marker log (exit=$exit_code): $output"
fi

# --- --framework only: rejected off-Windows before any build/test call happens ---
dir="$(new_test_case "framework-only")"
result="$(run_case "$dir" scry research --framework)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"requires a Windows host"* && ! -e "$dir/build-args.log" ]]; then
  test_pass "'scry --framework' is rejected off-Windows before any build starts"
else
  test_fail "'scry --framework' off-Windows (exit=$exit_code): $output"
fi

# --- --core/--framework rejected for non-scry actions ---
dir="$(new_test_case "selector-on-non-scry")"
result="$(run_case "$dir" weave --core)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"only valid with the 'scry' action"* ]]; then
  test_pass "'--core' is rejected on a non-scry action"
else
  test_fail "selector on non-scry action (exit=$exit_code): $output"
fi

# --- --testCompilerOnly is forwarded to eng/build.sh's test call ---
dir="$(new_test_case "test-compiler-only-forwarded")"
result="$(run_case "$dir" scry research --testCompilerOnly)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--testCompilerOnly" <<<"$args_log"; then
  test_pass "'--testCompilerOnly' is forwarded to ./eng/build.sh"
else
  test_fail "testCompilerOnly forwarding (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --testFilter is forwarded to eng/build.sh's test call with its value ---
dir="$(new_test_case "test-filter-forwarded")"
result="$(run_case "$dir" scry research --testFilter "FullyQualifiedName~Foo")"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--testFilter" <<<"$args_log" && grep -qx "FullyQualifiedName~Foo" <<<"$args_log"; then
  test_pass "'--testFilter' is forwarded to ./eng/build.sh as FullyQualifiedName~Foo"
else
  test_fail "testFilter forwarding (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --testFilter with a missing value is rejected ---
dir="$(new_test_case "test-filter-missing-value")"
result="$(run_case "$dir" scry --testFilter)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"requires a value"* ]]; then
  test_pass "'--testFilter' with no value is rejected"
else
  test_fail "testFilter missing value (exit=$exit_code): $output"
fi

# --- --testCompilerOnly/--testFilter rejected for non-scry actions ---
dir="$(new_test_case "test-compiler-only-on-non-scry")"
result="$(run_case "$dir" weave --testCompilerOnly)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"only valid with the 'scry' action"* ]]; then
  test_pass "'--testCompilerOnly' is rejected on a non-scry action"
else
  test_fail "testCompilerOnly on non-scry action (exit=$exit_code): $output"
fi

# --- --testIOperation is forwarded to eng/build.sh's test call ---
dir="$(new_test_case "test-ioperation-forwarded")"
result="$(run_case "$dir" scry research --testIOperation)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--testIOperation" <<<"$args_log"; then
  test_pass "'--testIOperation' is forwarded to ./eng/build.sh"
else
  test_fail "testIOperation forwarding (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --testIOperation rejected for non-scry actions ---
dir="$(new_test_case "test-ioperation-on-non-scry")"
result="$(run_case "$dir" weave --testIOperation)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"only valid with the 'scry' action"* ]]; then
  test_pass "'--testIOperation' is rejected on a non-scry action"
else
  test_fail "testIOperation on non-scry action (exit=$exit_code): $output"
fi

# --- --collectDumps is opt-in: absent by default, forwarded to eng/build.sh's test call only when
# requested (mutates a machine-wide WER registry key and its timeout-dump path can capture unrelated
# processes, so it isn't safe as scry's silent default -- see folly.sh's own comment at the
# --collectDumps case) ---
dir="$(new_test_case "collectdumps-not-forwarded-by-default")"
result="$(run_case "$dir" scry research)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && ! grep -qx -- "--collectDumps" <<<"$args_log"; then
  test_pass "'scry' does not forward '--collectDumps' to ./eng/build.sh by default"
else
  test_fail "collectDumps default (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --collectDumps is forwarded when explicitly requested ---
dir="$(new_test_case "collectdumps-forwarded-when-requested")"
result="$(run_case "$dir" scry research --collectDumps)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--collectDumps" <<<"$args_log"; then
  test_pass "'--collectDumps' is forwarded to ./eng/build.sh when requested"
else
  test_fail "collectDumps forwarding (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --collectDumps rejected for non-scry actions ---
dir="$(new_test_case "collectdumps-on-non-scry")"
result="$(run_case "$dir" weave --collectDumps)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"only valid with the 'scry' action"* ]]; then
  test_pass "'--collectDumps' is rejected on a non-scry action"
else
  test_fail "collectDumps on non-scry action (exit=$exit_code): $output"
fi

# --- --bootstrap: the initial build call gets --bootstrap, each test leg gets --bootstrapDir
# pointing at the same deterministic artifacts/Bootstrap dir instead of rebuilding it ---
dir="$(new_test_case "bootstrap-forwarded")"
result="$(run_case "$dir" scry research --bootstrap)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
build_call="${args_log%%===call===*}"
test_call="${args_log#*===call===}"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--bootstrap" <<<"$build_call" && ! grep -qx -- "--bootstrapDir" <<<"$build_call" \
  && grep -qx -- "--bootstrapDir" <<<"$test_call" && grep -qx -- "$dir/artifacts/Bootstrap" <<<"$test_call" && ! grep -qx -- "--bootstrap" <<<"$test_call"; then
  test_pass "'--bootstrap' builds once and is reused via --bootstrapDir for the test leg"
else
  test_fail "bootstrap forwarding (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --bootstrap is rejected on 'cleanse' ---
dir="$(new_test_case "bootstrap-on-cleanse")"
result="$(run_case "$dir" cleanse --bootstrap)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"aren't valid with 'cleanse'"* ]]; then
  test_pass "'--bootstrap' is rejected on 'cleanse'"
else
  test_fail "bootstrap on cleanse (exit=$exit_code): $output"
fi

# --- --bootstrap is forwarded on a non-scry action too (not scoped to 'scry') ---
dir="$(new_test_case "bootstrap-on-weave")"
result="$(run_case "$dir" weave research --bootstrap)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--bootstrap" <<<"$args_log"; then
  test_pass "'--bootstrap' is forwarded to ./eng/build.sh on 'weave'"
else
  test_fail "bootstrap on weave (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --binaryLog is forwarded to eng/build.sh ---
dir="$(new_test_case "binarylog-forwarded")"
result="$(run_case "$dir" weave research --binaryLog)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--binaryLog" <<<"$args_log"; then
  test_pass "'--binaryLog' is forwarded to ./eng/build.sh"
else
  test_fail "binaryLog forwarding (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --verbosity is forwarded to eng/build.sh with its value ---
dir="$(new_test_case "verbosity-forwarded")"
result="$(run_case "$dir" scry research --verbosity diagnostic)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--verbosity" <<<"$args_log" && grep -qx "diagnostic" <<<"$args_log"; then
  test_pass "'--verbosity diagnostic' is forwarded to ./eng/build.sh"
else
  test_fail "verbosity forwarding (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --verbosity with a missing value is rejected ---
dir="$(new_test_case "verbosity-missing-value")"
result="$(run_case "$dir" weave --verbosity)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"requires a value"* ]]; then
  test_pass "'--verbosity' with no value is rejected"
else
  test_fail "verbosity missing value (exit=$exit_code): $output"
fi

# --- --verbosity rejects MSBuild's own single-letter/abbreviated shorthand (e.g. 'diag'): full words only ---
dir="$(new_test_case "verbosity-shorthand-rejected")"
result="$(run_case "$dir" weave --verbosity diag)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"requires one of"* && "$output" == *"Got 'diag'"* ]]; then
  test_pass "'--verbosity diag' (shorthand) is rejected"
else
  test_fail "verbosity shorthand rejected (exit=$exit_code): $output"
fi

# --- --verbosity accepts a full word case-insensitively ---
dir="$(new_test_case "verbosity-case-insensitive")"
result="$(run_case "$dir" weave research --verbosity DIAGNOSTIC)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx "DIAGNOSTIC" <<<"$args_log"; then
  test_pass "'--verbosity DIAGNOSTIC' is accepted case-insensitively"
else
  test_fail "verbosity case-insensitive (exit=$exit_code): args='$args_log' output=$output"
fi

# --- --binaryLog is rejected on 'cleanse' ---
dir="$(new_test_case "binarylog-on-cleanse")"
result="$(run_case "$dir" cleanse --binaryLog)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"aren't valid with 'cleanse'"* ]]; then
  test_pass "'--binaryLog' is rejected on 'cleanse'"
else
  test_fail "binaryLog on cleanse (exit=$exit_code): $output"
fi

# --- --verbosity is rejected alongside 'scry reflection' ---
dir="$(new_test_case "verbosity-on-reflection")"
result="$(run_case "$dir" scry reflection --verbosity diagnostic)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"doesn't take a primary arg or any switches"* ]]; then
  test_pass "'--verbosity' is rejected alongside 'scry reflection'"
else
  test_fail "verbosity on reflection (exit=$exit_code): $output"
fi

# --- rejected argument ---
dir="$(new_test_case "rejected-arg")"
result="$(run_case "$dir" scry --bogus)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"Unrecognized argument"* ]]; then
  test_pass "unknown argument is rejected"
else
  test_fail "rejected argument (exit=$exit_code): $output"
fi

# --- an overflowing --timeout value is rejected, not silently wrapped by 64-bit arithmetic ---
dir="$(new_test_case "timeout-overflow")"
result="$(run_case "$dir" scry --timeout 18446744073709551617)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"up to 999999999"* ]]; then
  test_pass "'--timeout 18446744073709551617' (overflows 64-bit arithmetic) is rejected"
else
  test_fail "timeout overflow (exit=$exit_code): $output"
fi

# --- a --timeout past Task.Delay's supported maximum is rejected, not forwarded to crash RunTests ---
dir="$(new_test_case "timeout-exceeds-task-delay-max")"
result="$(run_case "$dir" scry --timeout 100000)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"71582"* ]]; then
  test_pass "'--timeout 100000' (exceeds Task.Delay's supported maximum) is rejected"
else
  test_fail "timeout exceeds Task.Delay max (exit=$exit_code): $output"
fi

# --- 'reflection' is rejected on a non-scry action ---
dir="$(new_test_case "reflection-non-scry")"
result="$(run_case "$dir" weave reflection)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"only valid with the 'scry' action"* ]]; then
  test_pass "'reflection' is rejected on a non-scry action"
else
  test_fail "reflection on non-scry action (exit=$exit_code): $output"
fi

# --- 'reflection' rejects a primary arg alongside it ---
dir="$(new_test_case "reflection-with-config")"
result="$(run_case "$dir" scry reflection truth)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"doesn't take a primary arg or any switches"* ]]; then
  test_pass "'reflection' rejects a primary arg alongside it"
else
  test_fail "reflection with config (exit=$exit_code): $output"
fi

# --- 'reflection' rejects '--timeout' alongside it ---
dir="$(new_test_case "reflection-with-timeout")"
result="$(run_case "$dir" scry reflection --timeout 5)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"doesn't take a primary arg or any switches"* ]]; then
  test_pass "'reflection' rejects '--timeout' alongside it"
else
  test_fail "reflection with timeout (exit=$exit_code): $output"
fi

# --- 'scry reflection' runs folly's own test harnesses instead of the (mocked) build ---
dir="$(new_test_case "reflection-runs-harnesses")"
mkdir -p "$dir/scripts"
cat > "$dir/scripts/test-folly-cleanse.sh" <<'MOCK'
#!/usr/bin/env bash
echo "cleanse harness ran"
exit 0
MOCK
cat > "$dir/scripts/test-folly-scry-args.sh" <<'MOCK'
#!/usr/bin/env bash
echo "scry-args harness ran"
exit 0
MOCK
chmod +x "$dir/scripts/test-folly-cleanse.sh" "$dir/scripts/test-folly-scry-args.sh"
result="$(run_case "$dir" scry reflection)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "0" && "$output" == *"cleanse harness ran"* && "$output" == *"scry-args harness ran"* && ! -e "$dir/build-args.log" ]]; then
  test_pass "'scry reflection' runs both harnesses instead of building"
else
  test_fail "scry reflection runs harnesses (exit=$exit_code): $output"
fi

# --- grimoire ignores a trailing config, matching its documented "ignores config" contract ---
dir="$(new_test_case "grimoire-ignores-config")"
result="$(run_case "$dir" grimoire anything)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "0" && "$output" == *"Commands:"* ]]; then
  test_pass "'grimoire anything' still prints help instead of rejecting the trailing arg"
else
  test_fail "grimoire ignores config (exit=$exit_code): $output"
fi

echo ""
echo "$pass_count passed, $fail_count failed"
if [[ "$fail_count" -gt 0 ]]; then
  exit 1
fi
exit 0
