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

      # The actual removal is a single native `rm -rf`, which walks and
      # unlinks the tree directly in C. The previous implementation instead
      # enumerated every file into a bash array, `stat`-sized each one in
      # batches, and then `rm -f`'d and re-verified each batch from bash --
      # three full passes of interpreted per-file work before the real
      # `rm -rf` even ran, which is why it felt much slower than a plain
      # `rm -rf`/Explorer delete. Progress here is just a lightweight
      # count of remaining files, polled from a background sampler, so
      # showing it doesn't add per-file cost back in.
      total_count=$(find "$artifacts_dir" -type f 2>/dev/null | wc -l | tr -d ' ')
      [[ -z "$total_count" ]] && total_count=0

      start_time=$(date +%s)
      rm -rf "$artifacts_dir" &
      rm_pid=$!

      if (( interactive )); then
        spinner_frames=('|' '/' '-' '\')
        spinner_index=0
        last_second=-1
        while kill -0 "$rm_pid" 2>/dev/null; do
          if (( SECONDS != last_second )); then
            last_second=$SECONDS
            spinner_index=$(( (spinner_index + 1) % ${#spinner_frames[@]} ))
            remaining=$(find "$artifacts_dir" -type f 2>/dev/null | wc -l | tr -d ' ')
            [[ -z "$remaining" ]] && remaining=0
            deleted=$(( total_count > remaining ? total_count - remaining : 0 ))
            percent=$(( total_count > 0 ? deleted * 100 / total_count : 100 ))
            printf '\r\033[KCleansing artefacts %s %d / %d files (%d%%)' \
              "${spinner_frames[$spinner_index]}" "$deleted" "$total_count" "$percent"
          fi
          sleep 0.05
        done
        printf '\r\033[K'
      fi

      wait "$rm_pid" || true
      elapsed=$(( $(date +%s) - start_time ))

      if [[ -d "$artifacts_dir" ]]; then
        remaining=$(find "$artifacts_dir" -type f 2>/dev/null | wc -l | tr -d ' ')
        [[ -z "$remaining" ]] && remaining="some"
        echo "Cleansed artefacts in ${elapsed}s; $remaining file(s) could not be removed."
        exit 1
      else
        echo "Cleansed $total_count file(s) from artefacts in ${elapsed}s."
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