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

      # Enumerate filenames only (no per-file stat/wc calls, which would spawn
      # a subprocess per artifact and make cleanup impractically slow on a
      # large build tree). Sizing is done once in bulk via `du` below.
      spinner_frames=('|' '/' '-' '\')
      spinner_index=0
      files=()
      scan_count=0
      while IFS= read -r -d '' file; do
        files+=("$file")
        scan_count=$(( scan_count + 1 ))
        if (( interactive )) && (( scan_count % 200 == 0 )); then
          spinner_index=$(( (spinner_index + 1) % ${#spinner_frames[@]} ))
          printf '\r\033[KEnumerating files %s %d file(s) found' "${spinner_frames[$spinner_index]}" "$scan_count"
        fi
      done < <(find "$artifacts_dir" -type f -print0)
      (( interactive )) && printf '\r\033[K'

      total_count=${#files[@]}
      total_bytes=$(du -sk "$artifacts_dir" 2>/dev/null | awk '{ print $1 * 1024 }')
      [[ -z "$total_bytes" ]] && total_bytes=0
      total_formatted=$(format_bytes "$total_bytes")

      # Delete in batches so cleanup spawns a handful of `rm` processes
      # instead of one per file.
      batch_size=500
      deleted_count=0
      start_time=$(date +%s)
      i=0
      while (( i < total_count )); do
        batch=("${files[@]:i:batch_size}")
        rm -f -- "${batch[@]}" 2>/dev/null || true
        deleted_count=$(( deleted_count + ${#batch[@]} ))
        i=$(( i + batch_size ))
        if (( interactive )); then
          percent=$(( total_count > 0 ? deleted_count * 100 / total_count : 100 ))
          now=$(date +%s)
          elapsed=$(( now - start_time ))
          approx_deleted_bytes=$(( total_count > 0 ? total_bytes * deleted_count / total_count : total_bytes ))
          bytes_per_second=$(( elapsed > 0 ? approx_deleted_bytes / elapsed : 0 ))
          printf '\r\033[KCleansing artefacts: %d / %d files, %s / %s, %s/s (%d%%)' \
            "$deleted_count" "$total_count" "$(format_bytes "$approx_deleted_bytes")" "$total_formatted" "$(format_bytes "$bytes_per_second")" "$percent"
        fi
      done
      (( interactive )) && printf '\r\033[K'

      rm -rf "$artifacts_dir" || true
      if [[ -d "$artifacts_dir" ]]; then
        remaining=$(find "$artifacts_dir" -type f 2>/dev/null | wc -l | tr -d ' ')
        echo "Cleansed $total_formatted of artefacts; $remaining file(s) could not be removed."
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