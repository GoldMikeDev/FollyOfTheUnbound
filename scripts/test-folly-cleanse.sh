#!/bin/bash
# Manual test harness for `folly.sh cleanse` (and `folly.ps1 cleanse`'s bash
# counterpart). Not wired into CI -- run by hand after touching the cleanse
# implementation:
#   ./scripts/test-folly-cleanse.sh
#
# Covers: empty artifacts/, a populated tree, redirected (non-TTY) output
# staying free of escape codes, a permission failure reporting an accurate
# count and a nonzero exit code, and a file vanishing mid-enumeration.
set -uo pipefail

script_root="$(cd -P "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
folly_sh="$script_root/folly.sh"
work_root="$(mktemp -d)"
trap 'chmod -R u+rwX "$work_root" 2>/dev/null; chattr -R -i "$work_root" 2>/dev/null; rm -rf "$work_root"' EXIT

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
if (( ec == 0 )) && [[ "$out" == "Cleansed 0 B from artefacts." ]] && [[ ! -e "$dir/artifacts" ]]; then
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
if (( ec == 0 )) && [[ "$out" == "Cleansed 2.00 KiB from artefacts." ]] && [[ ! -e "$dir/artifacts" ]]; then
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
if (( ec == 0 )) && ! grep -qP '\x1b|\r' "$log"; then
  pass "redirected output has no carriage returns or escape sequences"
else
  fail "redirected output (exit=$ec); offending bytes: $(cat -A "$log" | head -3)"
fi

# --- permission failure: accurate count and nonzero exit -----------------
if command -v chattr >/dev/null 2>&1 && [[ "$(id -u)" == "0" ]]; then
  dir=$(new_case permfail)
  mkdir -p "$dir/artifacts"
  head -c 1000 /dev/urandom > "$dir/artifacts/removable.bin"
  head -c 10 /dev/urandom > "$dir/artifacts/stuck.bin"
  chattr +i "$dir/artifacts/stuck.bin"
  out=$(cd "$dir" && bash folly.sh cleanse 2>&1)
  ec=$?
  chattr -i "$dir/artifacts/stuck.bin" 2>/dev/null || true
  if (( ec == 1 )) && [[ "$out" == *"Cleansed 1000 B of artefacts; 1 file(s) could not be removed."* ]]; then
    pass "permission failure reports accurate count and exits nonzero"
  else
    fail "permission failure (exit=$ec, output='$out')"
  fi
else
  echo "SKIP: permission-failure case (needs root + chattr)"
fi

# --- file vanishing mid-enumeration/sizing (concurrent writer) -----------
dir=$(new_case concurrent)
mkdir -p "$dir/artifacts"
for i in $(seq 1 50); do head -c 100 /dev/urandom > "$dir/artifacts/f_$i.bin"; done
# Race a background deletion against cleanse's own enumeration/sizing pass;
# whichever file "loses" the race should not abort the script.
(
  for i in $(seq 1 50); do
    rm -f "$dir/artifacts/f_$i.bin" 2>/dev/null
  done
) &
racer=$!
out=$(cd "$dir" && bash folly.sh cleanse 2>&1)
ec=$?
wait "$racer" 2>/dev/null || true
if (( ec == 0 || ec == 1 )) && [[ ! -e "$dir/artifacts" || -z "$(find "$dir/artifacts" -type f 2>/dev/null)" ]]; then
  pass "concurrent file removal during cleanse does not abort (output='$out')"
else
  fail "concurrent file removal (exit=$ec, output='$out')"
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

echo ""
echo "$pass_count passed, $fail_count failed"
(( fail_count == 0 ))
