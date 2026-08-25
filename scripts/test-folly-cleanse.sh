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

pass_count=0
fail_count=0

fail() {
  echo "FAIL: $1"
  fail_count=$(( fail_count + 1 ))
}

pass() {
  echo "PASS: $1"
  pass_count=$(( pass_count + 1 ))
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
  pass "empty artifacts/ directory removed cleanly"
else
  fail "empty artifacts/ directory (exit=$ec, output='$out')"
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
    echo "SKIP: permission-failure case (chattr +i not permitted in this environment)"
  fi
else
  echo "SKIP: permission-failure case (needs root + chattr)"
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
  pass "concurrent file removal during cleanse does not abort (output='$out')"
else
  fail "concurrent file removal (exit=$ec, output='$out')"
fi

# --- unreadable subtree during the scan: uncertain (not false-zero) remainder
if [[ "$(id -u)" == "0" ]]; then
  echo "SKIP: unreadable-subtree case (root bypasses directory read permissions)"
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
    echo "SKIP: unreadable-subtree case (this filesystem/shell does not enforce chmod as real access control)"
    chmod 755 "$dir/artifacts/locked" 2>/dev/null || true
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
  pass "artifacts/ as a regular file is removed directly"
else
  fail "artifacts/ as a regular file (exit=$ec, output='$out')"
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
disown
nohup "$foreign_script" >/dev/null 2>&1 &
disown
sleep 0.5
trapped_pid=$(ps -eo pid,command | grep 'pipename:test-trapped' | grep -v grep | awk '{print $1}')
foreign_pid=$(ps -eo pid,command | grep 'pipename:test-foreign' | grep -v grep | awk '{print $1}')
synthetic_pids="$synthetic_pids $trapped_pid $foreign_pid"  # register with the EXIT trap immediately -- before any assertion below might exit/interrupt the harness and leave these orphaned

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
  echo "SKIP: build-server force-kill case (couldn't spawn synthetic processes in this environment)"
  [[ -n "$trapped_pid" ]] && kill -9 "$trapped_pid" 2>/dev/null
  [[ -n "$foreign_pid" ]] && kill -9 "$foreign_pid" 2>/dev/null
fi

# --- no artifacts/ at all -------------------------------------------------
dir=$(new_case nothing)
out=$(cd "$dir" && bash folly.sh cleanse 2>&1)
ec=$?
if (( ec == 0 )) && [[ "$out" == "No artefacts to cleanse." ]]; then
  pass "missing artifacts/ reports nothing to cleanse"
else
  fail "missing artifacts/ (exit=$ec, output='$out')"
fi

echo ""
echo "$pass_count passed, $fail_count failed"
(( fail_count == 0 ))
