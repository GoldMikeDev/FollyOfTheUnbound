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
          local out status
          out=$(find "$1" -type f -printf '%s\n' 2>/dev/null)
          status=$?
          printf '%s' "$out" | awk -v ok="$(( status == 0 ? 1 : 0 ))" '{s+=$1; n++} END{printf "%d %d %d\n", s+0, n+0, ok}'
        }
      else
        dir_stats() {
          local out status
          out=$(find "$1" -type f -print0 2>/dev/null | xargs -0 stat -f%z 2>/dev/null)
          status=$?
          printf '%s' "$out" | awk -v ok="$(( status == 0 ? 1 : 0 ))" '{s+=$1; n++} END{printf "%d %d %d\n", s+0, n+0, ok}'
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
            last_second=$SECONDS
            spinner_index=$(( (spinner_index + 1) % ${#spinner_frames[@]} ))
            read -r remaining_bytes remaining_count _ <<< "$(dir_stats "$artifacts_dir")"
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