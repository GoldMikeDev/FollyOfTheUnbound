#!/usr/bin/env bash
# Regression test for folly.sh scry's argument parsing (action, [config], --timeout -- including
# leading-zero normalization, missing/invalid values, and non-scry rejection), run against a mocked
# eng/build.sh so no real build/test happens. Bash counterpart to
# scripts/test-folly-scry-args.ps1 -- see the folly.sh/folly.ps1 parity rule in CONVENTIONS.md for
# why this needs to stay in lockstep with that harness rather than being PowerShell-only.
# Run by hand (or wire into CI) after touching folly.sh's argument parsing or scry action:
#   bash ./scripts/test-folly-scry-args.sh
set -euo pipefail

script_root="$(cd -P "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
folly_sh="$script_root/folly.sh"

work_root="$(mktemp -d)"
trap 'rm -rf "$work_root"' EXIT

pass_count=0
fail_count=0

test_pass() {
  echo "PASS: $1"
  pass_count=$((pass_count + 1))
}

test_fail() {
  echo "FAIL: $1"
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
  cat > "$dir/eng/build.sh" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$@" > "$(dirname "$0")/../build-args.log"
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

# --- default: no --testTimeout forwarded ---
dir="$(new_test_case "default")"
result="$(run_case "$dir" scry)"
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
result="$(run_case "$dir" scry --timeout 180)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
args_log="$(cat "$dir/build-args.log" 2>/dev/null || echo "")"
if [[ "$exit_code" == "0" ]] && grep -qx -- "--testTimeout" <<<"$args_log" && grep -qx "180" <<<"$args_log"; then
  test_pass "'--timeout 180' is forwarded to eng/build.sh as --testTimeout 180"
else
  test_fail "timeout forwarding (exit=$exit_code): args='$args_log' output=$output"
fi

# --- positional [config] alongside --timeout ---
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
result="$(run_case "$dir" scry --timeout 08)"
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

# --- 'reflection' rejects a [config] alongside it ---
dir="$(new_test_case "reflection-with-config")"
result="$(run_case "$dir" scry reflection truth)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"doesn't take a [config]"* ]]; then
  test_pass "'reflection' rejects a [config] alongside it"
else
  test_fail "reflection with config (exit=$exit_code): $output"
fi

# --- 'reflection' rejects '--timeout' alongside it ---
dir="$(new_test_case "reflection-with-timeout")"
result="$(run_case "$dir" scry reflection --timeout 5)"
exit_code="${result%%$'\x1e'*}"
output="${result#*$'\x1e'}"
if [[ "$exit_code" == "1" && "$output" == *"doesn't take '--timeout'"* ]]; then
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
if [[ "$exit_code" == "0" && "$output" == *"folly.sh <action>"* ]]; then
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
