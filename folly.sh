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
case "$action" in  # --nodeReuse false on every branch below: Arcade's tools.sh defaults nodeReuse true locally, leaving MSBuild worker nodes running after exit, still holding DLLs open under artifacts/ (`build-server shutdown` in cleanse only stops VBCSCompiler/Razor, not these)
  attune)
    "$build_script" --restore --nodeReuse false --solution "$solution" --configuration "$configuration"
    ;;
  weave)
    "$build_script" --restore --build --nodeReuse false --solution "$solution" --configuration "$configuration"
    ;;
  reweave)
    "$build_script" --restore --rebuild --nodeReuse false --solution "$solution" --configuration "$configuration"
    ;;
  bind)
    "$build_script" --restore --build --pack --nodeReuse false --solution "$solution" --configuration "$configuration"
    ;;
  scry)
    scry_args=(--restore --build --test --nodeReuse false --solution "$solution" --configuration "$configuration")
    if [[ "$test_timeout" -gt 0 ]]; then
      scry_args+=(--testTimeout "$test_timeout")
    fi
    "$build_script" "${scry_args[@]}"
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
    [[ -n "$dotnet_exe" ]] && "$dotnet_exe" build-server shutdown >/dev/null 2>&1 || true
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
      _cleanse_kill_tree() {  # kills a pid's children first (portable ps -eo pid,ppid) then the pid itself, so Ctrl+C can't orphan a still-traversing find/awk pipeline behind a killed wrapper subshell
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
            percent=$(( total_bytes > 0 ? deleted_bytes * 100 / total_bytes : (total_count > 0 ? deleted_count * 100 / total_count : 100) ))
            (( percent > 99 )) && percent=99  # find is still running here -- only the post-wait report below may claim 100%
            elapsed=$(( $(date +%s) - start_time ))
            bytes_per_second=$(( elapsed > 0 ? deleted_bytes / elapsed : 0 ))
            printf '\r\033[KCleansing artefacts %d / %d files, %s / %s, %s/s (%d%%)' "$deleted_count" "$total_count" "$(format_bytes "$deleted_bytes")" "$total_formatted" "$(format_bytes "$bytes_per_second")" "$percent"
          fi
        done < "$rm_fifo"
        rm -f "$rm_fifo"
        (( interactive )) && printf '\r\033[K'
        wait "$rm_pid" || true
      else
        rm -rf "$artifacts_dir" &  # BSD find has no -printf, so it can't report a deleted file's size in the same pass as -delete -- fall back to scan + rm -rf + periodic rescan (the exact contention the GNU branch above eliminates)
        rm_pid=$!
        deleted_bytes=$total_bytes
        deleted_count=$total_count
        if (( interactive )); then
          printf '\r\033[KCleansing artefacts %d / %d files, %s / %s' "$deleted_count" "$total_count" "$(format_bytes "$deleted_bytes")" "$total_formatted"
          while kill -0 "$rm_pid" 2>/dev/null; do
            sleep 0.15
            read -r remaining_bytes remaining_count _ <<< "$(dir_stats "$artifacts_dir")"
            deleted_bytes=$(( total_bytes > remaining_bytes ? total_bytes - remaining_bytes : 0 ))
            deleted_count=$(( total_count > remaining_count ? total_count - remaining_count : 0 ))
            percent=$(( total_bytes > 0 ? deleted_bytes * 100 / total_bytes : (total_count > 0 ? deleted_count * 100 / total_count : 100) ))
            (( percent > 99 )) && percent=99  # rm -rf is still running here -- only the post-wait report below may claim 100%
            elapsed=$(( $(date +%s) - start_time ))
            bytes_per_second=$(( elapsed > 0 ? deleted_bytes / elapsed : 0 ))
            printf '\r\033[KCleansing artefacts %d / %d files, %s / %s, %s/s (%d%%)' "$deleted_count" "$total_count" "$(format_bytes "$deleted_bytes")" "$total_formatted" "$(format_bytes "$bytes_per_second")" "$percent"
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
