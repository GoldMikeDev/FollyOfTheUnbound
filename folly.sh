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
      now_ms() {
        local t
        t=$(date +%s%N)
        echo $(( t / 1000000 ))
      }

      spinner_frames=('|' '/' '-' '\')
      spinner_index=0
      last_spinner_update=0
      files=()
      sizes=()
      total_bytes=0
      while IFS= read -r -d '' file; do
        files+=("$file")
        size=$(wc -c < "$file" 2>/dev/null || echo 0)
        sizes+=("$size")
        total_bytes=$(( total_bytes + size ))
        now=$(now_ms)
        if (( now - last_spinner_update >= 100 )); then
          last_spinner_update=$now
          spinner_index=$(( (spinner_index + 1) % ${#spinner_frames[@]} ))
          printf '\r\033[KEnumerating files %s %d file(s) found' "${spinner_frames[$spinner_index]}" "${#files[@]}"
        fi
      done < <(find "$artifacts_dir" -type f -print0)
      printf '\r\033[K'

      total_count=${#files[@]}
      total_formatted=$(format_bytes "$total_bytes")
      deleted_bytes=0
      deleted_count=0
      failed_count=0
      start_time=$(now_ms)
      last_update=0
      for i in "${!files[@]}"; do
        file="${files[$i]}"
        size="${sizes[$i]}"
        if rm -f -- "$file" 2>/dev/null; then
          deleted_bytes=$(( deleted_bytes + size ))
          deleted_count=$(( deleted_count + 1 ))
        else
          failed_count=$(( failed_count + 1 ))
        fi
        now=$(now_ms)
        if (( now - last_update >= 100 )); then
          last_update=$now
          if (( total_bytes > 0 )); then
            percent=$(( deleted_bytes * 100 / total_bytes ))
          else
            percent=$(( total_count > 0 ? deleted_count * 100 / total_count : 0 ))
          fi
          (( percent > 99 )) && percent=99
          elapsed_ms=$(( now - start_time ))
          if (( elapsed_ms > 0 )); then
            bytes_per_second=$(( deleted_bytes * 1000 / elapsed_ms ))
          else
            bytes_per_second=0
          fi
          printf '\r\033[KCleansing artefacts: %d / %d files, %s / %s, %s/s (%d%%)' \
            "$deleted_count" "$total_count" "$(format_bytes "$deleted_bytes")" "$total_formatted" "$(format_bytes "$bytes_per_second")" "$percent"
        fi
      done
      printf '\r\033[K'
      rm -rf "$artifacts_dir"
      if [[ -d "$artifacts_dir" ]]; then
        echo "Cleansed $(format_bytes "$deleted_bytes") of artefacts; $failed_count file(s) could not be removed."
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