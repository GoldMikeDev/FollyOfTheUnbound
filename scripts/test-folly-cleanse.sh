#!/bin/bash
# Manual test harness for `folly.sh cleanse` (and `folly.ps1 cleanse`'s bash
# counterpart). Not wired into CI -- run by hand after touching the cleanse
# implementation:
#   ./scripts/test-folly-cleanse.sh
#
# Covers: empty artifacts/, a populated tree, redirected (non-TTY) output
# staying free of escape codes, a permission failure reporting an accurate
# count and a nonzero exit code, a file vanishing mid-enumeration, an
# unreadable subtree during the background scan reporting an honest
# uncertain remainder (not a false "0 files could not be removed"), and
# artifacts/ as a non-directory. These exercise the background bulk-delete
# path (single `rm -rf` + background scan/poll, not the old per-file loop),
# so they're also regression coverage for that rewrite.
set -uo pipefail

script_root="$(cd -P "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
folly_sh="$script_root/folly.sh"
work_root="$(mktemp -d)"
synthetic_pids=""  # PIDs of any detached background process this harness spawns (e.g. the synthetic build-server case below) -- appended to as each is launched, so the EXIT trap can reap them even if the harness is interrupted or dies before its own explicit cleanup runs
trap 'for _p in $synthetic_pids; do kill -9 "$_p" 2>/dev/null; done; chmod -R u+rwX "$work_root" 2>/dev/null; chattr -R -i "$work_root" 2>/dev/null; rm -rf "$work_root"' EXIT

if [[ -t 1 ]]; then  # matches folly.sh cleanse's own [[ -t 1 ]] check -- plain text when redirected/piped (e.g. a CI log), colored in an interactive terminal
  color_reset=$'\033[0m'; color_red=$'\033[31m'; color_green=$'\033[32m'; color_yellow=$'\033[33m'; color_purple=$'\033[35m'
else
  color_reset=''; color_red=''; color_green=''; color_yellow=''; color_purple=''
fi

pass_count=0
fail_count=0

pwsh_invoke() {
  echo "${color_purple}PWSH: $1${color_reset}"
}

fail() {
  echo "${color_red}FAIL: $1${color_reset}"
  fail_count=$(( fail_count + 1 ))
}

pass() {
  echo "${color_green}PASS: $1${color_reset}"
  pass_count=$(( pass_count + 1 ))
}

skip() {
  echo "${color_yellow}SKIP: $1${color_reset}"
}

# Invoked immediately after a skip() whose cause is a genuine Git-Bash/MSYS2
# limitation (not present on real Windows) rather than a fundamental one --
# runs test-folly-cleanse.ps1's Windows-native equivalent of that single case
# via pwsh and folds its pass/fail into this script's own counts, prefixing
# its output so it's clearly a pwsh invocation and not bash's own. On
# anything but Windows-with-pwsh this is a silent no-op: no pwsh version, no
# equivalent case.
pwsh_crossover() {
  local case_name="$1"
  if [[ "${OS:-}" != "Windows_NT" ]] || ! command -v pwsh >/dev/null 2>&1; then
    return
  fi
  pwsh_invoke "invoking test-folly-cleanse.ps1 to run Windows-native equivalent of the above case"
  local output
  output=$(pwsh -NoProfile -File "$script_root/scripts/test-folly-cleanse.ps1" -Only "$case_name" 2>&1)
  local pwsh_ec=$?
  local line
  while IFS= read -r line; do
    line="${line%$'\r'}"  # pwsh emits CRLF -- without stripping this, the blank line below is "\r", not "", and slips past the "" case
    case "$line" in
      "") continue ;;                       # the blank line test-folly-cleanse.ps1 prints before its own summary
      *" passed, "*" failed") continue ;;    # and the summary itself -- we fold pass/fail into our own counts below instead
      PASS:*) echo "${color_purple}PWSH:${color_reset} ${color_green}${line}${color_reset}" ;;
      FAIL:*) echo "${color_purple}PWSH:${color_reset} ${color_red}${line}${color_reset}" ;;
      SKIP:*) echo "${color_purple}PWSH:${color_reset} ${color_yellow}${line}${color_reset}" ;;
      *)      echo "${color_purple}PWSH:${color_reset} ${line}" ;;
    esac
  done <<< "$output"
  if (( pwsh_ec == 0 )); then
    pass_count=$(( pass_count + 1 ))
  else
    fail_count=$(( fail_count + 1 ))
  fi
}

# Confirms $1 (a pid this harness just spawned) is actually the process it
# thinks it is, by checking its command line contains marker $2 -- a cheap
# sanity check before a test proceeds to run cleanse and assert on that pid.
# Plain `ps -eo pid,command` (procps/BSD syntax) is what these checks used
# unconditionally until a review round on PR #87 pointed out it silently
# fails under Git-Bash/MSYS2 (whose own `ps` has no `-o`/`-eo` support at
# all -- see folly.sh's own _cleanse_ps_snapshot, which this mirrors), so
# every one of these verification checks came back empty on that host and
# the build-server/ancestor-exclusion cases -- exactly the cases meant to
# exercise the new CIM/native-Win32 code path -- silently skipped instead of
# actually running, on the one platform that path exists for. There is no
# test-folly-cleanse.ps1 equivalent to pwsh_crossover into here (unlike the
# "locked"/"unreadable" cases below): TESTING_STRATEGY.md documents that
# harness as deliberately not having a build-server/TERM-then-KILL or
# ancestor-exclusion case of its own (Windows has no direct equivalent of a
# POSIX signal trap for a *native* process), so this bash-side coverage --
# spawning ordinary trap-catching bash scripts, which Git-Bash's own real
# bash runs identically to Linux/macOS -- is the only place either gets
# exercised at all, and it needs to actually run on Windows to mean anything.
_verify_pid_marker() {
  local pid="$1" marker="$2" pwsh_exe native_pid
  if [[ "${OS:-}" == "Windows_NT" ]]; then
    # $pid here is $! from this harness's own `nohup ... &`, an MSYS pid -- exactly like folly.sh's own
    # $$ (see its native_self_pid), not necessarily the native Win32 pid CIM's ProcessId/ps -W's WINPID
    # key off of. Translate the same way folly.sh does for itself: /proc/<pid>/winpid, read directly (no
    # $(...) subshell needed here since there's no risk of resolving the wrong process -- unlike
    # folly.sh's own self-lookup, this pid is a plain value already in hand, not implied by which
    # process happens to run the read).
    native_pid="$pid"
    if [[ -r "/proc/$pid/winpid" ]]; then
      read -r native_pid < "/proc/$pid/winpid" 2>/dev/null || native_pid="$pid"
    fi
    pwsh_exe=$(command -v pwsh 2>/dev/null) || pwsh_exe=$(command -v powershell.exe 2>/dev/null) || pwsh_exe=$(command -v powershell 2>/dev/null) || pwsh_exe=""
    if [[ -n "$pwsh_exe" ]]; then
      local cmdline
      cmdline=$("$pwsh_exe" -NoProfile -NonInteractive -Command "(Get-CimInstance Win32_Process -Filter \"ProcessId=$native_pid\").CommandLine" 2>/dev/null)
      [[ "$cmdline" == *"$marker"* ]] && return 0
    fi
    [[ -n "$(ps -W 2>/dev/null | tail -n +2 | awk -v p="$native_pid" '$4==p{print}')" ]] && return 0  # last resort, best-effort: -W's COMMAND column (see folly.sh's own fallback) is the bare exe path with no args, so this can only confirm the pid exists at all, not that it matches $marker -- still better than nothing when no PowerShell is on PATH
    return 1
  fi
  [[ -n "$(ps -eo pid,command 2>/dev/null | grep "^[[:space:]]*$pid[[:space:]]" | grep -F -- "$marker")" ]]
}

new_case() {
  local name="$1"
  local dir="$work_root/$name"
  rm -rf "$dir"
  mkdir -p "$dir/eng"
  cp "$folly_sh" "$dir/folly.sh"
  cat > "$dir/eng/build.sh" <<'EOF'
#!/bin/bash
echo fake
EOF
  chmod +x "$dir/eng/build.sh"
  echo "$dir"
}

# --- empty artifacts/ -------------------------------------------------
dir=$(new_case empty)
mkdir -p "$dir/artifacts"
out=$(cd "$dir" && bash folly.sh cleanse 2>&1)
ec=$?
if (( ec == 0 )) && [[ "$out" == "Cleansed 0 B of artefacts." ]] && [[ ! -e "$dir/artifacts" ]]; then
  pass "empty ./artifacts/ directory removed cleanly"
else
  fail "empty ./artifacts/ directory (exit=$ec, output='$out')"
fi

# --- populated tree -----------------------------------------------------
dir=$(new_case populated)
mkdir -p "$dir/artifacts/sub"
for i in $(seq 1 20); do head -c 100 /dev/urandom > "$dir/artifacts/f_$i.bin"; done
head -c 50 /dev/urandom > "$dir/artifacts/sub/nested.bin"
out=$(cd "$dir" && bash folly.sh cleanse 2>&1)
ec=$?
if (( ec == 0 )) && [[ "$out" == "Cleansed 2.00 KiB of artefacts." ]] && [[ ! -e "$dir/artifacts" ]]; then
  pass "populated tree removed with correct byte total"
else
  fail "populated tree (exit=$ec, output='$out')"
fi

# --- redirected (non-TTY) output has no escape codes ---------------------
dir=$(new_case redirected)
mkdir -p "$dir/artifacts"
for i in $(seq 1 20); do : > "$dir/artifacts/f_$i.bin"; done
log="$work_root/redirected.log"
(cd "$dir" && bash folly.sh cleanse > "$log" 2>&1)
ec=$?
# Plain fixed-string search for a literal ESC/CR byte -- the shell expands
# $'\x1b'/$'\r' before grep ever sees them, so this needs no -P (BSD grep on
# macOS, the documented folly.sh platform, doesn't support -P).
if grep -qF $'\x1b' "$log" || grep -qF $'\r' "$log"; then
  fail "redirected output contains escape sequences or carriage returns; offending bytes: $(cat -A "$log" | head -3)"
elif (( ec != 0 )); then
  fail "redirected output (exit=$ec)"
else
  pass "redirected output has no carriage returns or escape sequences"
fi

# --- permission failure: accurate count and nonzero exit -----------------
if command -v chattr >/dev/null 2>&1 && [[ "$(id -u)" == "0" ]]; then
  dir=$(new_case permfail)
  mkdir -p "$dir/artifacts"
  head -c 1000 /dev/urandom > "$dir/artifacts/removable.bin"
  head -c 10 /dev/urandom > "$dir/artifacts/stuck.bin"
  if chattr +i "$dir/artifacts/stuck.bin" 2>/dev/null; then
    out=$(cd "$dir" && bash folly.sh cleanse 2>&1)
    ec=$?
    chattr -i "$dir/artifacts/stuck.bin" 2>/dev/null || true
    if (( ec == 1 )) && [[ "$out" == *"Cleansed 1000 B of artefacts; 1 file(s) could not be removed."* ]]; then
      pass "permission failure reports accurate count and exits nonzero"
    else
      fail "permission failure (exit=$ec, output='$out')"
    fi
  else
    skip "permission-failure case (chattr +i not permitted in this environment)"
  fi
else
  skip "permission-failure case (needs root + chattr)"
  pwsh_crossover locked
fi

# --- file vanishing mid-enumeration/sizing (concurrent writer) -----------
dir=$(new_case concurrent)
mkdir -p "$dir/artifacts"
for i in $(seq 1 50); do head -c 100 /dev/urandom > "$dir/artifacts/f_$i.bin"; done
# Race a background deletion against cleanse's own enumeration/sizing pass.
# A vanished file must not abort cleanse -- it should still finish
# successfully (exit 0), regardless of who actually removed each file.
(
  for i in $(seq 1 50); do
    rm -f "$dir/artifacts/f_$i.bin" 2>/dev/null
  done
) &
racer=$!
out=$(cd "$dir" && bash folly.sh cleanse 2>&1)
ec=$?
wait "$racer" 2>/dev/null || true
if (( ec == 0 )) && [[ ! -e "$dir/artifacts" ]]; then
  pass "concurrent file removal during cleanse does not abort"
else
  fail "concurrent file removal (exit=$ec, output='$out')"
fi

# --- unreadable subtree during the scan: uncertain (not false-zero) remainder
if [[ "$(id -u)" == "0" ]]; then
  skip "unreadable-subtree case (root bypasses directory read permissions)"
else
  dir=$(new_case unreadable)
  mkdir -p "$dir/artifacts/locked"
  head -c 100 /dev/urandom > "$dir/artifacts/locked/hidden.bin"
  head -c 100 /dev/urandom > "$dir/artifacts/visible.bin"
  chmod 000 "$dir/artifacts/locked"
  # Some filesystems/shells don't actually enforce chmod as real access control -- notably Git Bash
  # (MSYS2) on Windows, which sits on NTFS and only emulates the DOS read-only attribute, not POSIX
  # permission bits. On those, the directory stays fully readable despite chmod 000, so the rest of
  # this case can't exercise what it's meant to; skip rather than fail on an environment limitation.
  if ls "$dir/artifacts/locked" >/dev/null 2>&1; then
    skip "unreadable-subtree case (this filesystem/shell does not enforce chmod as real access control)"
    chmod 755 "$dir/artifacts/locked" 2>/dev/null || true
    pwsh_crossover unreadable
  else
    out=$(cd "$dir" && bash folly.sh cleanse 2>&1)
    ec=$?
    chmod 755 "$dir/artifacts/locked" 2>/dev/null || true
    if (( ec == 1 )) && [[ "$out" == *"at least"*"could not be removed (some may be unreadable and not counted)"* ]]; then
      pass "unreadable subtree reports an uncertain (not false-zero) remainder"
    else
      fail "unreadable subtree (exit=$ec, output='$out')"
    fi
  fi
fi

# --- artifacts/ as a non-directory (regular file) -------------------------
dir=$(new_case regfile)
echo "blocking entry" > "$dir/artifacts"
out=$(cd "$dir" && bash folly.sh cleanse 2>&1)
ec=$?
if (( ec == 0 )) && [[ ! -e "$dir/artifacts" ]]; then
  pass "./artifacts/ as a regular file is removed directly"
else
  fail "./artifacts/ as a regular file (exit=$ec, output='$out')"
fi

# --- build-server force-kill: scoped survivor gets killed, foreign one left alone
# Exercises the process-killing fallback itself (scoping the force-kill to this
# checkout's own .dotnet SDK root, and the TERM-then-KILL escalation with
# confirmed-exit counting), not just file deletion. A same-checkout build server
# that traps SIGTERM (simulating one that ignores the graceful stop) must still
# end up force-killed and confirmed dead; a foreign-checkout one matching the same
# name pattern must survive untouched.
dir=$(new_case buildserver)
mkdir -p "$dir/.dotnet/sdk"
cat > "$dir/.dotnet/dotnet" <<'EOF'
#!/bin/bash
exit 0
EOF
chmod +x "$dir/.dotnet/dotnet"

trapped_script="$work_root/trapped_vbcs.sh"
cat > "$trapped_script" <<EOF
#!/bin/bash
exec -a "dotnet exec $dir/.dotnet/sdk/VBCSCompiler.dll -pipename:test-trapped" bash -c 'trap "" TERM; while true; do sleep 1; done'
EOF
chmod +x "$trapped_script"

foreign_script="$work_root/foreign_vbcs.sh"
cat > "$foreign_script" <<'EOF'
#!/bin/bash
exec -a "dotnet exec /some/other/checkout/.dotnet/sdk/VBCSCompiler.dll -pipename:test-foreign" sleep 300
EOF
chmod +x "$foreign_script"

nohup "$trapped_script" >/dev/null 2>&1 &
trapped_pid=$!  # nohup execs straight into trapped_script, which itself `exec -a`s into bash -- exec replaces the process image without forking, so this $! is already the final PID we need, no ps lookup or race required
synthetic_pids="$synthetic_pids $trapped_pid"  # registered with the EXIT trap the instant the PID is known -- before disown, the second launch, or the sleep/ps-verify below can be interrupted and leave it orphaned
disown
nohup "$foreign_script" >/dev/null 2>&1 &
foreign_pid=$!
synthetic_pids="$synthetic_pids $foreign_pid"
disown
sleep 0.5
# Verify each PID is actually the process we think it is (matches its pipename marker), not just that nohup/exec succeeded -- cheap sanity check now that the PIDs themselves no longer depend on this lookup.
_verify_pid_marker "$trapped_pid" "pipename:test-trapped" || trapped_pid=""
_verify_pid_marker "$foreign_pid" "pipename:test-foreign" || foreign_pid=""

if [[ -n "$trapped_pid" && -n "$foreign_pid" ]]; then
  out=$(cd "$dir" && bash folly.sh cleanse 2>&1)
  ec=$?
  sleep 0.3
  trapped_alive=0; kill -0 "$trapped_pid" 2>/dev/null && trapped_alive=1
  foreign_alive=0; kill -0 "$foreign_pid" 2>/dev/null && foreign_alive=1
  kill -9 "$trapped_pid" 2>/dev/null
  kill -9 "$foreign_pid" 2>/dev/null
  if (( ec == 0 )) && [[ "$out" == *"Force-killed 1 build server"* ]] && (( trapped_alive == 0 )) && (( foreign_alive == 1 )); then
    pass "build-server force-kill escalates a same-checkout trapped survivor and leaves a foreign-checkout one alone"
  else
    fail "build-server force-kill scoping/escalation (exit=$ec, output='$out', trapped_alive=$trapped_alive, foreign_alive=$foreign_alive)"
  fi
else
  skip "build-server force-kill case (couldn't spawn synthetic processes in this environment)"
  [[ -n "$trapped_pid" ]] && kill -9 "$trapped_pid" 2>/dev/null
  [[ -n "$foreign_pid" ]] && kill -9 "$foreign_pid" 2>/dev/null
fi

# --- ancestor exclusion: a wrapper matching the scoped pattern must survive
# cleanse running beneath it -----------------------------------------------
# The case above proves scoping/escalation on *sibling* processes, not the
# ancestor-exclusion path itself: it never puts a matching process in
# cleanse's own parent chain. This launches cleanse as a child of a wrapper
# process whose own command line matches the build-server pattern and this
# checkout's .dotnet path -- exactly the self-kill scenario a prior review
# round caught -- and asserts the wrapper (cleanse's own ancestor) survives.
dir=$(new_case ancestor)
mkdir -p "$dir/.dotnet"
cat > "$dir/.dotnet/dotnet" <<'EOF'
#!/bin/bash
exit 0
EOF
chmod +x "$dir/.dotnet/dotnet"

done_marker="$work_root/ancestor_cleanse_done"
wrapper_script="$work_root/ancestor_wrapper.sh"
cat > "$wrapper_script" <<EOF
#!/bin/bash
exec -a "dotnet exec $dir/.dotnet/sdk/VBCSCompiler.dll -pipename:test-ancestor" bash -c '
  sleep 1 &          # keeps this exec-ed bash -c process itself (not yet its final command) foregrounded/observable under its custom name for a moment, so the check below cannot race a tail-call exec optimization into replacing it early
  wait
  cd "$dir"
  bash folly.sh cleanse >/dev/null 2>&1
  touch "$done_marker"
  sleep 5
'
EOF
chmod +x "$wrapper_script"

nohup "$wrapper_script" >/dev/null 2>&1 &
wrapper_pid=$!
synthetic_pids="$synthetic_pids $wrapper_pid"  # registered with the EXIT trap immediately, before either check below can be interrupted
disown
sleep 0.5
if _verify_pid_marker "$wrapper_pid" "pipename:test-ancestor"; then
  # Wait for cleanse (running as this wrapper's own child) to actually finish, rather than guessing a fixed delay.
  for _i in $(seq 1 50); do
    [[ -f "$done_marker" ]] && break
    sleep 0.1
  done
  wrapper_alive=0; kill -0 "$wrapper_pid" 2>/dev/null && wrapper_alive=1
  kill -9 "$wrapper_pid" 2>/dev/null
  if [[ -f "$done_marker" ]] && (( wrapper_alive == 1 )); then
    pass "ancestor exclusion: a wrapper whose own command line matches the scoped pattern survives cleanse running beneath it"
  else
    fail "ancestor exclusion: wrapper matching the scoped pattern did not survive cleanse running beneath it (cleanse_ran=$([[ -f "$done_marker" ]] && echo yes || echo no), wrapper_alive=$wrapper_alive)"
  fi
else
  skip "ancestor exclusion case (couldn't spawn synthetic wrapper process in this environment)"
  kill -9 "$wrapper_pid" 2>/dev/null
fi

# --- no artifacts/ at all -------------------------------------------------
dir=$(new_case nothing)
out=$(cd "$dir" && bash folly.sh cleanse 2>&1)
ec=$?
if (( ec == 0 )) && [[ "$out" == "No artefacts to cleanse." ]]; then
  pass "missing ./artifacts/ reports nothing to cleanse"
else
  fail "missing ./artifacts/ (exit=$ec, output='$out')"
fi

echo ""
echo "$pass_count passed, $fail_count failed"
(( fail_count == 0 ))
