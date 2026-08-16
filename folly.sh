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
      # large build tree). Sizing is done in batches via `stat` below.
      spinner_frames=('|' '/' '-' '\')
      spinner_index=0
      files=()
      scan_count=0
      # Redraw at most once per wall-clock second (checked via the `SECONDS`
      # builtin -- no subprocess) rather than every Nth file. Gating on file
      # count alone let the spinner and its counter flicker by faster than a
      # human eye can track on large, fast-enumerating trees.
      spinner_last_second=-1
      while IFS= read -r -d '' file; do
        files+=("$file")
        scan_count=$(( scan_count + 1 ))
        if (( interactive )) && (( SECONDS != spinner_last_second )); then
          spinner_last_second=$SECONDS
          spinner_index=$(( (spinner_index + 1) % ${#spinner_frames[@]} ))
          printf '\r\033[KEnumerating files %s %d file(s) found' "${spinner_frames[$spinner_index]}" "$scan_count"
        fi
      done < <(find "$artifacts_dir" -type f -print0)
      (( interactive )) && printf '\r\033[K'

      # `${#files[@]}` on a still-empty array trips "unbound variable" under
      # `set -u` on bash 3.2 (macOS's system bash), so nounset is relaxed
      # just for this reference.
      set +u
      total_count=${#files[@]}
      set -u

      stat_flag="-c%s"
      stat -c%s "$scriptroot" >/dev/null 2>&1 || stat_flag="-f%z"

      # Bound each batch by encoded argument size (not just file count): a
      # fixed count like 500 can still overflow a constrained ARG_MAX on
      # systems with long artifact paths or a lowered command-line limit.
      arg_max=$(getconf ARG_MAX 2>/dev/null || echo 0)
      (( arg_max > 0 )) || arg_max=131072
      byte_budget=$(( arg_max / 4 ))
      (( byte_budget >= 4096 )) || byte_budget=4096
      max_batch_files=1000

      batch_starts=()
      batch_lens=()
      if (( total_count > 0 )); then
        # `${#var}` counts characters, not bytes, under a multibyte locale,
        # which would undercount the actual argv size exec() enforces for
        # non-ASCII paths. Force LC_ALL=C for this loop so it counts bytes.
        prior_lc_all="${LC_ALL:-}"
        export LC_ALL=C
        bstart=0
        bcount=0
        bbytes=0
        for (( idx = 0; idx < total_count; idx++ )); do
          flen=$(( ${#files[idx]} + 1 ))
          if (( bcount > 0 )) && (( bbytes + flen > byte_budget || bcount >= max_batch_files )); then
            batch_starts+=("$bstart")
            batch_lens+=("$bcount")
            bstart=$idx
            bcount=0
            bbytes=0
          fi
          bcount=$(( bcount + 1 ))
          bbytes=$(( bbytes + flen ))
        done
        batch_starts+=("$bstart")
        batch_lens+=("$bcount")
        if [[ -n "$prior_lc_all" ]]; then
          export LC_ALL="$prior_lc_all"
        else
          unset LC_ALL
        fi
      fi
      # Same bash 3.2 empty-array caveat as `files` above.
      set +u
      batch_count=${#batch_starts[@]}
      set -u

      # Per-file sizes, parallel to `files`, gathered once up front so byte
      # accounting always reflects this one snapshot. Re-`stat`ing a
      # survivor later would report its *current* size, which can differ
      # from what was actually removed if it was concurrently modified.
      sizes=()
      total_bytes=0
      for (( b = 0; b < batch_count; b++ )); do
        bstart=${batch_starts[b]}
        blen=${batch_lens[b]}
        batch=("${files[@]:bstart:blen}")
        # `|| true`: a file can vanish between enumeration and sizing (e.g. a
        # concurrent build process cleaning its own temp output), which
        # makes stat exit nonzero -- don't let that abort cleanup.
        batch_out=$(stat "$stat_flag" -- "${batch[@]}" 2>/dev/null) || true
        line_vals=()
        line_count=0
        if [[ -n "$batch_out" ]]; then
          while IFS= read -r sz; do
            line_vals+=("$sz")
            line_count=$(( line_count + 1 ))
          done <<< "$batch_out"
        fi
        idx=$bstart
        if (( line_count == blen )); then
          # Common case: every operand succeeded, so stat's output is one
          # line per file in the same order they were passed -- safe to
          # assign positionally.
          for sz in "${line_vals[@]}"; do
            sizes[idx]=$sz
            total_bytes=$(( total_bytes + sz ))
            idx=$(( idx + 1 ))
          done
        else
          # Rare: one or more files vanished mid-batch, breaking positional
          # alignment. Fall back to stat'ing this batch one file at a time
          # so each size still lands on the correct file.
          for f in "${batch[@]}"; do
            sz=$(stat "$stat_flag" -- "$f" 2>/dev/null) || sz=0
            [[ -z "$sz" ]] && sz=0
            sizes[idx]=$sz
            total_bytes=$(( total_bytes + sz ))
            idx=$(( idx + 1 ))
          done
        fi
      done
      total_formatted=$(format_bytes "$total_bytes")

      deleted_bytes=0
      deleted_count=0
      start_time=$(date +%s)
      # Same once-per-second throttle as the enumeration spinner above: batches
      # can be up to 1000 files and complete in a flash, which previously made
      # the bar jump straight from 0% to 100% in a single redraw. Always draw
      # the last batch so the final numbers shown are accurate.
      progress_last_second=-1
      for (( batch_idx = 0; batch_idx < batch_count; batch_idx++ )); do
        bstart=${batch_starts[batch_idx]}
        blen=${batch_lens[batch_idx]}
        batch=("${files[@]:bstart:blen}")
        rm -f -- "${batch[@]}" 2>/dev/null || true

        # Verify what actually disappeared (cheap builtin checks, no
        # subprocess) instead of assuming the whole batch succeeded -- rm -f
        # swallows per-file failures (e.g. an unwritable parent dir). Bytes
        # come from the original sizing snapshot above, never re-stat'd.
        idx=$bstart
        for f in "${batch[@]}"; do
          if [[ ! -e "$f" && ! -L "$f" ]]; then
            deleted_count=$(( deleted_count + 1 ))
            deleted_bytes=$(( deleted_bytes + sizes[idx] ))
          fi
          idx=$(( idx + 1 ))
        done

        if (( interactive )) && { (( SECONDS != progress_last_second )) || (( batch_idx == batch_count - 1 )); }; then
          progress_last_second=$SECONDS
          if (( total_bytes > 0 )); then
            percent=$(( deleted_bytes * 100 / total_bytes ))
          else
            percent=$(( total_count > 0 ? deleted_count * 100 / total_count : 100 ))
          fi
          now=$(date +%s)
          elapsed=$(( now - start_time ))
          bytes_per_second=$(( elapsed > 0 ? deleted_bytes / elapsed : 0 ))
          printf '\r\033[KCleansing artefacts: %d / %d files, %s / %s, %s/s (%d%%)' \
            "$deleted_count" "$total_count" "$(format_bytes "$deleted_bytes")" "$total_formatted" "$(format_bytes "$bytes_per_second")" "$percent"
        fi
      done
      (( interactive )) && printf '\r\033[K'

      rm -rf "$artifacts_dir" || true
      if [[ -d "$artifacts_dir" ]]; then
        # `find` can fail partway (e.g. an unreadable subtree) while `wc -l`
        # still happily prints "0" for whatever it received, silently
        # masking the failure -- so check find's own status via the `if`,
        # which set -e exempts from triggering on a nonzero condition.
        if remaining_list=$(find "$artifacts_dir" -type f 2>/dev/null); then
          if [[ -z "$remaining_list" ]]; then
            remaining=0
          else
            remaining=$(printf '%s\n' "$remaining_list" | wc -l | tr -d ' ')
          fi
        else
          remaining="some"
        fi
        echo "Cleansed $(format_bytes "$deleted_bytes") of artefacts; $remaining file(s) could not be removed."
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