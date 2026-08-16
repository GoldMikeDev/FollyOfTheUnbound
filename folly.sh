set -euo pipefail
if [[ -t 1 ]] && command -v tput >/dev/null 2>&1; then
  tput civis 2>/dev/null || true
  trap 'tput cnorm 2>/dev/null || true' EXIT
fi
action="${1:-}"
config="${2:-}"
scriptroot="$(cd -P "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
solution="FollyOfTheUnbound.slnx"
build_script="$scriptroot/eng/build.sh"
nupkg_root="$scriptroot/../.nupkg/FotU"
if [[ -z "$action" || "$action" == "grimoire" ]]; then
  cat <<'EOF'
folly.sh <action> [config]

Actions:
  attune    Restore only [config]
  weave     Restore + build [config]
  reweave   Restore + rebuild [config]
  bind      Restore + build + pack [config] (copies .nupkg output to ../.nupkg/FotU)
  scry      Restore + build + run CoreCLR unit tests [config]
  cleanse   Delete artifacts/ (ignores config)
  grimoire  Show this text (default when no action is given; ignores config)

[config] (optional, defaults to Research):
  research  Debug
  truth     Release

EOF
  exit 0
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
    "$build_script" --restore --build --test --solution "$solution" --configuration "$configuration"
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
    command -v dotnet >/dev/null 2>&1 && dotnet build-server shutdown >/dev/null 2>&1 || true
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

      # Report (bytes, count) for every regular file under $1 in a single
      # `find` pass piped through one `awk`, not a bash loop stat-ing each
      # file -- that per-file bash overhead was what made cleanse feel much
      # slower than a plain `rm -rf`/Explorer delete. GNU find's -printf
      # gives sizes directly; BSD find (macOS) lacks -printf, so fall back
      # to piping filenames through one batched `stat` call instead.
      if find "$scriptroot" -maxdepth 0 -printf '' >/dev/null 2>&1; then
        dir_stats() {
          find "$1" -type f -printf '%s\n' 2>/dev/null | awk '{s+=$1; n++} END{printf "%d %d", s+0, n+0}'
        }
      else
        dir_stats() {
          find "$1" -type f -print0 2>/dev/null | xargs -0 stat -f%z 2>/dev/null | awk '{s+=$1; n++} END{printf "%d %d", s+0, n+0}'
        }
      fi

      # The actual removal is a single native `rm -rf`, which walks and
      # unlinks the tree directly in C -- far faster than looping per file
      # in bash. It runs in the background; progress is reported by
      # periodically re-running `dir_stats` on what's left, so the display
      # never adds per-file cost back into the deletion path itself.
      read -r total_bytes total_count <<< "$(dir_stats "$artifacts_dir")"
      total_formatted=$(format_bytes "$total_bytes")

      start_time=$(date +%s)
      rm -rf "$artifacts_dir" &
      rm_pid=$!

      deleted_bytes=$total_bytes
      deleted_count=$total_count
      if (( interactive )); then
        spinner_frames=('|' '/' '-' '\')
        spinner_index=0
        last_second=-1
        while kill -0 "$rm_pid" 2>/dev/null; do
          if (( SECONDS != last_second )); then
            last_second=$SECONDS
            spinner_index=$(( (spinner_index + 1) % ${#spinner_frames[@]} ))
            read -r remaining_bytes remaining_count <<< "$(dir_stats "$artifacts_dir")"
            deleted_bytes=$(( total_bytes > remaining_bytes ? total_bytes - remaining_bytes : 0 ))
            deleted_count=$(( total_count > remaining_count ? total_count - remaining_count : 0 ))
            if (( total_bytes > 0 )); then
              percent=$(( deleted_bytes * 100 / total_bytes ))
            else
              percent=$(( total_count > 0 ? deleted_count * 100 / total_count : 100 ))
            fi
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
        read -r remaining_bytes remaining_count <<< "$(dir_stats "$artifacts_dir")"
        deleted_bytes=$(( total_bytes - remaining_bytes ))
        echo "Cleansed $(format_bytes "$deleted_bytes") of artefacts; $remaining_count file(s) could not be removed."
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