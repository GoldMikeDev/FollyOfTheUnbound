set -euo pipefail
if [[ -t 1 ]] && command -v tput >/dev/null 2>&1; then
  tput civis 2>/dev/null || true
  trap 'tput cnorm 2>/dev/null || true' EXIT
fi
action="${1:-}"
shift $(( $# < 1 ? $# : 1 )) || true
scriptroot="$(cd -P "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
solution="FollyOfTheUnbound.slnx"
build_script="$scriptroot/eng/build.sh"
nupkg_root="$scriptroot/../.nupkg/FotU"
if [[ -z "$action" || "$action" == "grimoire" ]]; then
  cat <<'EOF'
folly.sh <action> [config] [switches]

Actions (positional only -- no --action flag; unlike folly.ps1, bash's
positional-only $1/$2 parsing here has no named-parameter equivalent):
  attune    Restore only [config]
  weave     Restore + build [config]
  reweave   Restore + rebuild [config]
  bind      Restore + build + pack [config] (copies .nupkg output to ../.nupkg/FotU)
  scry      Restore + build + run CoreCLR unit tests [config] (Desktop/Framework
            tests are Windows-only -- there is no --desktop/--core switch here)
  cleanse   Delete artifacts/ (ignores config)
  grimoire  Show this text (default when no action is given; ignores config)

[config] (optional, positional only -- no --config flag; defaults to Research):
  research  Debug
  truth     Release

scry-only switch (not positional -- always passed by name, after [config]):
  --timeout <minutes>  Override RunTests' whole-run watchdog (default: 90)

Example: folly.sh scry truth --timeout 180

EOF
  exit 0
fi
config=""
test_timeout=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --timeout)
      test_timeout="${2:-}"
      # The digit-count cap (9 digits, so at most 999999999) matters as much as the regex itself:
      # without it, a huge-enough digits-only value (e.g. 2^64+1) would silently wrap around
      # inside bash's 64-bit $(( )) arithmetic below into some unrelated small positive number,
      # turning an invalid request into a request that looks valid but isn't the one asked for.
      # Checking the string length first rejects that before arithmetic ever sees it.
      if [[ -z "$test_timeout" || ! "$test_timeout" =~ ^[0-9]{1,9}$ ]]; then
        echo "'--timeout' requires a positive integer minute count (up to 999999999), got '${2:-}'." >&2
        exit 1
      fi
      # Strip leading zeros before any arithmetic use: bash's [[ ... -le ]] and $(( )) both
      # interpret a leading-zero operand as octal (e.g. "08"/"09" are invalid octal digits and
      # error out with "value too great for base"), even though the regex above already confirmed
      # it's a valid decimal integer.
      test_timeout=$((10#$test_timeout))
      # Upper bound matches RunTests' own limit: Program.RunCoreAsync passes this straight to
      # Task.Delay, whose millisecond timer argument maxes out at 4294967294 (~71582.79 minutes) --
      # anything larger throws ArgumentOutOfRangeException before a single test runs, so reject it
      # here with a clear message instead of forwarding it and letting RunTests crash on it.
      if [[ "$test_timeout" -le 0 || "$test_timeout" -gt 71582 ]]; then
        echo "'--timeout' requires a positive integer minute count, up to 71582 (Task.Delay's supported maximum), got '${2:-}'." >&2
        exit 1
      fi
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
if [[ "$test_timeout" -gt 0 && "$action" != "scry" ]]; then
  echo "'--timeout' is only valid with the 'scry' action." >&2
  exit 1
fi
if [[ -z "$config" || "$config" == "research" ]]; then
  configuration="Debug"
  nupkg_dir="$nupkg_root/Debug"
elif [[ "$config" == "truth" ]]; then
  configuration="Release"
  nupkg_dir="$nupkg_root/Release"
else
  echo "Unrecognized configuration '$config'. Expected 'Debug', 'Release', or omitted (defaults to Debug)." >&2
  exit 1
fi
case "$action" in
  attune)
    "$build_script" --restore --solution "$solution" --configuration "$configuration"
    ;;
  weave)
    "$build_script" --restore --build --solution "$solution" --configuration "$configuration"
    ;;
  reweave)
    "$build_script" --restore --rebuild --solution "$solution" --configuration "$configuration"
    ;;
  bind)
    "$build_script" --restore --build --pack --solution "$solution" --configuration "$configuration"
    ;;
  scry)
    scry_args=(--restore --build --test --solution "$solution" --configuration "$configuration")
    if [[ "$test_timeout" -gt 0 ]]; then
      scry_args+=(--testTimeout "$test_timeout")
    fi
    "$build_script" "${scry_args[@]}"
    ;;
  cleanse)
    artifacts_dir="$scriptroot/artifacts"
    # VBCSCompiler / the MSBuild build server / the Razor build server keep
    # running between invocations and can hold an out-of-process BuildHost
    # alive with Microsoft.CodeAnalysis.Workspaces.MSBuild*.dll loaded from
    # artifacts/ -- on Windows that open handle blocks deleting the DLL
    # outright (Unix just unlinks it out from under the process, so this is
    # silent there). Shut the servers down first so cleanse never races a
    # locked file.
    #
    # attune/weave/etc. run through eng/common/tools.sh's
    # InitializeDotNetCli, which bootstraps a repo-local SDK under .dotnet/
    # and only puts it on PATH inside that child build process -- it never
    # updates this shell's PATH. A developer without a global `dotnet`
    # install would silently skip the shutdown and still hit the DLL lock,
    # so check the repo-local SDK first and only fall back to a global
    # `dotnet` on PATH.
    dotnet_exe="$scriptroot/.dotnet/dotnet"
    if [[ ! -x "$dotnet_exe" ]]; then
      # Git Bash on Windows runs against a bootstrapped SDK named
      # dotnet.exe, not the extensionless Unix name.
      dotnet_exe="$scriptroot/.dotnet/dotnet.exe"
    fi
    if [[ ! -x "$dotnet_exe" ]]; then
      dotnet_exe=$(command -v dotnet 2>/dev/null) || dotnet_exe=""
    fi
    [[ -n "$dotnet_exe" ]] && "$dotnet_exe" build-server shutdown >/dev/null 2>&1 || true
    if [[ -e "$artifacts_dir" || -L "$artifacts_dir" ]] && { [[ ! -d "$artifacts_dir" ]] || [[ -L "$artifacts_dir" ]]; }; then
      # A regular file, or a symlink (whether it points at a directory or
      # not), doesn't need the enumeration/progress machinery below -- just
      # remove the single entry.
      rm -rf -- "$artifacts_dir" || true
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

      # Background jobs started below (`rm -rf`, the scan subshell) run
      # with job control off (this is a non-interactive script) -- POSIX
      # has asynchronous commands ignore SIGINT/SIGQUIT in that case, so
      # Ctrl+C would otherwise kill this foreground script while leaving
      # `rm -rf` running orphaned, still deleting artifacts/ with no
      # visible progress. Explicitly forward INT/TERM to whichever
      # background job is currently running.
      #
      # The scan job is `( dir_stats "$artifacts_dir" > "$scan_tmp" ) &` --
      # $scan_pid is that wrapper subshell, not the `find`/`awk` pipeline it
      # runs internally. Killing only the wrapper leaves those two processes
      # orphaned, still traversing a potentially large artifact tree after
      # the prompt returns. Recursively kill each PID's children (via `ps`,
      # portable across GNU and BSD `ps -eo pid,ppid`) before the PID itself.
      # `rm -rf` doesn't spawn children, so this degrades to a plain single
      # kill for it.
      _cleanse_kill_tree() {
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
        return 0
      }
      trap '_cleanse_kill_bg; exit 130' INT
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

      # Report "bytes count ok" for every regular file under $1 in a single
      # `find` pass piped through one `awk`, not a bash loop stat-ing each
      # file -- that per-file bash overhead was what made cleanse feel much
      # slower than a plain `rm -rf`/Explorer delete. GNU find's -printf
      # gives sizes directly; BSD find (macOS) lacks -printf, so fall back
      # to piping filenames through one batched `stat` call instead. `ok` is
      # 1 only if `find` itself exited cleanly -- a permission-denied
      # subtree makes find exit nonzero while still printing what it could
      # read, and silently trusting that partial count as the true
      # remainder would let the final summary report "0 files could not be
      # removed" when files actually survived.
      if find "$scriptroot" -maxdepth 0 -printf '' >/dev/null 2>&1; then
        dir_stats() {
          local status
          # Pipe straight into awk rather than buffering into a bash
          # variable first -- on the large trees this is meant to help
          # with, `out=$(find ...)` would hold one size record per file in
          # memory (and repeat that O(file-count) allocation on every
          # progress refresh while `rm -rf` is running). Piping keeps this
          # streaming; awk only ever accumulates two running scalars.
          # This script sets `pipefail`, so the pipeline's own exit status
          # is nonzero whenever *any* stage fails, not just the last one --
          # a permission-denied subtree makes the whole pipeline "fail" even
          # though awk itself always exits 0 here. Run it as an `if`
          # condition so that failure can't trip `set -e`/errexit, and
          # capture `$PIPESTATUS[0]` (find's own code) in each branch,
          # before any other command has a chance to overwrite it.
          if find "$1" -type f -printf '%s\n' 2>/dev/null | awk '{s+=$1; n++} END{printf "%d %d", s+0, n+0}'; then
            status=0
          else
            status=${PIPESTATUS[0]}
          fi
          printf ' %d\n' "$(( status == 0 ? 1 : 0 ))"
        }
      else
        dir_stats() {
          local status
          # `find` failing (e.g. a permission-denied subtree) doesn't
          # necessarily fail `xargs` -- xargs happily stats whatever
          # filenames it was handed and exits 0. Checking only index 1
          # (xargs) would report ok=1 on a partial/truncated file list, so
          # both stages must succeed for this to count as a complete scan.
          if find "$1" -type f -print0 2>/dev/null | xargs -0 stat -f%z 2>/dev/null | awk '{s+=$1; n++} END{printf "%d %d", s+0, n+0}'; then
            status=0
          else
            status=$(( PIPESTATUS[0] != 0 || PIPESTATUS[1] != 0 ? 1 : 0 ))
          fi
          printf ' %d\n' "$(( status == 0 ? 1 : 0 ))"
        }
      fi

      # The actual removal is a single native `rm -rf`, which walks and
      # unlinks the tree directly in C -- far faster than looping per file
      # in bash. It runs in the background; progress is reported by
      # periodically re-running `dir_stats` on what's left, so the display
      # never adds per-file cost back into the deletion path itself.
      spinner_frames=('|' '/' '-' '\')
      spinner_index=0

      # `dir_stats` on the full tree can itself take a while on a large
      # build output -- run it in the background too and show a spinner
      # instead of leaving the terminal blank (with the cursor hidden)
      # until the scan finishes.
      scan_tmp=$(mktemp)
      ( dir_stats "$artifacts_dir" > "$scan_tmp" ) &
      scan_pid=$!
      if (( interactive )); then
        last_second=-1
        while kill -0 "$scan_pid" 2>/dev/null; do
          if (( SECONDS != last_second )); then
            last_second=$SECONDS
            spinner_index=$(( (spinner_index + 1) % ${#spinner_frames[@]} ))
            printf '\r\033[KScanning artefacts %s' "${spinner_frames[$spinner_index]}"
          fi
          sleep 0.05
        done
        printf '\r\033[K'
      fi
      wait "$scan_pid" || true
      read -r total_bytes total_count _ < "$scan_tmp"
      rm -f "$scan_tmp"
      total_formatted=$(format_bytes "$total_bytes")

      start_time=$(date +%s)
      rm -rf "$artifacts_dir" &
      rm_pid=$!

      deleted_bytes=$total_bytes
      deleted_count=$total_count
      if (( interactive )); then
        last_second=-1
        while kill -0 "$rm_pid" 2>/dev/null; do
          if (( SECONDS != last_second )); then
            spinner_index=$(( (spinner_index + 1) % ${#spinner_frames[@]} ))
            read -r remaining_bytes remaining_count _ <<< "$(dir_stats "$artifacts_dir")"
            # Stamp the throttle from *after* the scan, not before -- on the
            # large trees this is meant to help with, `dir_stats` can itself
            # take a second or more, and timestamping before it would let
            # the next loop iteration fire immediately, keeping a second
            # full-tree walker running continuously alongside `rm -rf` and
            # fighting it for the same filesystem I/O.
            last_second=$SECONDS
            deleted_bytes=$(( total_bytes > remaining_bytes ? total_bytes - remaining_bytes : 0 ))
            deleted_count=$(( total_count > remaining_count ? total_count - remaining_count : 0 ))
            if (( total_bytes > 0 )); then
              percent=$(( deleted_bytes * 100 / total_bytes ))
            else
              percent=$(( total_count > 0 ? deleted_count * 100 / total_count : 100 ))
            fi
            # `rm -rf` is still running at this point (the loop condition is
            # `kill -0 "$rm_pid"`) -- it may still be removing now-empty
            # directories even once every file is gone, so 100% here would
            # be a lie. Only the post-`wait` report below may claim 100%.
            (( percent > 99 )) && percent=99
            now=$(date +%s)
            elapsed=$(( now - start_time ))
            bytes_per_second=$(( elapsed > 0 ? deleted_bytes / elapsed : 0 ))
            printf '\r\033[KCleansing artefacts %s %d / %d files, %s / %s, %s/s (%d%%)' \
              "${spinner_frames[$spinner_index]}" "$deleted_count" "$total_count" \
              "$(format_bytes "$deleted_bytes")" "$total_formatted" "$(format_bytes "$bytes_per_second")" "$percent"
          fi
          sleep 0.05
        done
        printf '\r\033[K'
      fi

      wait "$rm_pid" || true

      if [[ -d "$artifacts_dir" ]]; then
        # A lock held only transiently (e.g. an antivirus scanner, or a
        # process still winding down) can clear between the first rm -rf and
        # now -- retry once before reporting survivors, the same second
        # chance the old per-file loop gave every file implicitly by
        # continuing past individual failures.
        rm -rf "$artifacts_dir" 2>/dev/null || true
      fi

      if [[ -d "$artifacts_dir" ]]; then
        read -r remaining_bytes remaining_count remaining_ok <<< "$(dir_stats "$artifacts_dir")"
        # A concurrent process (e.g. another build) can add bytes to the
        # tree after the initial snapshot, making remaining_bytes exceed
        # total_bytes -- clamp instead of printing a negative size.
        deleted_bytes=$(( total_bytes > remaining_bytes ? total_bytes - remaining_bytes : 0 ))
        if (( remaining_ok )); then
          echo "Cleansed $(format_bytes "$deleted_bytes") of artefacts; $remaining_count file(s) could not be removed."
        else
          # `find` itself failed partway (e.g. an unreadable subtree), so
          # remaining_count only reflects what it could see -- reporting it
          # as exact would understate (possibly to a false "0") how much is
          # actually left behind.
          echo "Cleansed $(format_bytes "$deleted_bytes") of artefacts; at least $remaining_count file(s) could not be removed (some may be unreadable and not counted)."
        fi
        exit 1
      else
        echo "Cleansed $total_formatted from artefacts."
      fi
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