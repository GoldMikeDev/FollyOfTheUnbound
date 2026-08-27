#!/usr/bin/env bash
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
  # No '--core'/'--framework' selectors here, unlike folly.ps1's grimoire: this is an intentional
  # limitation, not a parity gap. eng/build.sh has no Framework/.NET Framework (net472) test-running
  # support at all -- no --testDesktop, nothing -- since that only ever works on Windows regardless
  # of which shell invokes it, and 'scry' here always runs Core-only unconditionally (build.sh's
  # plain --test/-t already means test_core_clr=true, nothing else). Add real Framework-test support
  # to eng/build.sh itself first if that ever needs to change.
  cat <<'EOF'

Commands:
    'attune'                                            Restore only.
    'bind'                                              Restore, build & pack (nupkg files packed to ../.nupkg/FotU/).
    'cleanse'                                           Delete artefacts.
    'grimoire'                                          Show this text (default when no action is given).
    'reweave'                                           Restore & rebuild.
    'scry'                                              Restore, build & run Core unit tests.
    'weave'                                             Restore & build.
Primary args:
    '<scry> reflection'                                 Runs folly script test harnesses.
    '<command> research [switches]'                     Debug configuration.
    '<command> truth [switches]'                        Release configuration.
Switches:
    '<scry> <primary> --timeout <minutes>'              Override RunTests' whole-run watchdog (default: 90).
    '<command> <primary> --binaryLog'                   MSBuild binary log written to ./artifacts/log/<config>/Build.binlog.
    '<command> <primary> --verbosity <level>'           MSBuild console verbosity: quiet, minimal, normal, detailed, diagnostic.

EOF
  exit 0
fi
config=""
test_timeout=0
reflection=0
binary_log=0
verbosity=""
while [[ $# -gt 0 ]]; do
  case "$1" in
	reflection)
	  reflection=1
	  shift
	  ;;
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
	--binaryLog)
	  binary_log=1
	  shift
	  ;;
	--verbosity)
	  verbosity="${2:-}"
	  if [[ -z "$verbosity" ]]; then
		echo "'--verbosity' requires a value: quiet, minimal, normal, detailed, or diagnostic." >&2
		exit 1
	  fi
	  shopt -s nocasematch  # case-insensitive match, matching folly.ps1's -notin (PowerShell string comparisons are case-insensitive by default); toggled back off immediately after, not left on for the rest of the script
	  case "$verbosity" in  # full words only, not MSBuild's own q/m/n/d/diag shorthand -- explicit over terse
		quiet|minimal|normal|detailed|diagnostic) shopt -u nocasematch ;;
		*)
		  shopt -u nocasematch
		  echo "'--verbosity' requires one of: quiet, minimal, normal, detailed, diagnostic. Got '$verbosity'." >&2
		  exit 1
		  ;;
	  esac
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
# '--timeout'/reflection are scoped to 'scry' -- one combined check/message rather than one per
# selector, since they're all the same rule applied to different args.
if [[ ( "$test_timeout" -gt 0 || "$reflection" -eq 1 ) && "$action" != "scry" ]]; then
  echo "'--timeout'/'reflection' are only valid with the 'scry' action." >&2
  exit 1
fi
# By this point "$action" == "scry" is already guaranteed whenever reflection is set (the check
# above would have rejected it otherwise), so this doesn't need to re-check "$action" itself.
if [[ "$reflection" -eq 1 && ( -n "$config" || "$test_timeout" -gt 0 || "$binary_log" -eq 1 || -n "$verbosity" ) ]]; then
  echo "'reflection' doesn't take a primary arg or any switches -- it runs folly's own test harnesses, not a build/RunTests." >&2
  exit 1
fi
if [[ ( "$binary_log" -eq 1 || -n "$verbosity" ) && "$action" == "cleanse" ]]; then
  echo "'--binaryLog'/'--verbosity' aren't valid with 'cleanse' -- there's no build to log." >&2
  exit 1
fi
if [[ "$action" == "cleanse" || ( "$action" == "scry" && "$reflection" -eq 1 ) ]]; then
  configuration=""
  nupkg_dir=""
elif [[ -z "$config" ]]; then
  echo "Primary arg is required for action '$action'. Expected 'research' or 'truth'." >&2
  exit 1
elif [[ "$config" == "research" ]]; then
  configuration="Debug"
  nupkg_dir="$nupkg_root/Debug"
elif [[ "$config" == "truth" ]]; then
  configuration="Release"
  nupkg_dir="$nupkg_root/Release"
else
  echo "Unrecognized configuration '$config'. Expected 'research' or 'truth'." >&2
  exit 1
fi
extra_build_args=()  # --binaryLog: forwarded as-is to eng/build.sh's own -binaryLog/-bl. --verbosity: already restricted to full words above (eng/build.sh's own -verbosity/-v itself still accepts MSBuild's q/m/n/d/diag shorthand too, but folly.sh only ever forwards the validated full-word form here)
if [[ "$binary_log" -eq 1 ]]; then
  extra_build_args+=(--binaryLog)
fi
if [[ -n "$verbosity" ]]; then
  extra_build_args+=(--verbosity "$verbosity")
fi
case "$action" in  # --nodeReuse false on every branch below: Arcade's tools.sh defaults nodeReuse true locally, leaving MSBuild worker nodes running after exit, still holding DLLs open under artifacts/ (`build-server shutdown` in cleanse only stops VBCSCompiler/Razor, not these)
  attune)
	"$build_script" --restore --nodeReuse false --solution "$solution" --configuration "$configuration" ${extra_build_args[@]+"${extra_build_args[@]}"}
	;;
  weave)
	"$build_script" --restore --build --nodeReuse false --solution "$solution" --configuration "$configuration" ${extra_build_args[@]+"${extra_build_args[@]}"}
	;;
  reweave)
	"$build_script" --restore --rebuild --nodeReuse false --solution "$solution" --configuration "$configuration" ${extra_build_args[@]+"${extra_build_args[@]}"}
	;;
  bind)
	"$build_script" --restore --build --pack --nodeReuse false --solution "$solution" --configuration "$configuration" ${extra_build_args[@]+"${extra_build_args[@]}"}
	;;
  scry)
	if [[ "$reflection" -eq 1 ]]; then
	  harness_fail=0
	  echo ""
	  for harness in test-folly-cleanse.sh test-folly-scry-args.sh; do
		echo "--- $harness ---"
		bash "$scriptroot/scripts/$harness" || harness_fail=1
		echo
	  done
	  exit "$harness_fail"
	fi
	scry_args=(--restore --build --test --nodeReuse false --solution "$solution" --configuration "$configuration")
	if [[ "$test_timeout" -gt 0 ]]; then
	  scry_args+=(--testTimeout "$test_timeout")
	fi
	scry_args+=(${extra_build_args[@]+"${extra_build_args[@]}"})
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
	_cleanse_ps_snapshot() {  # `ps -eo pid,command` with the header stripped and our own transient grep rows excluded -- otherwise a pattern like "VBCSCompiler" or "MSBuild.dll" matches the literal text of the very grep command searching for it
	  ps -eo pid,command 2>/dev/null | tail -n +2 | grep -v '^[[:space:]]*[0-9]\+[[:space:]]\+grep ' || true  # `|| true`: no matches is not an error here (grep exits 1), and must never abort the script under `set -e`/pipefail
	}
	_cleanse_pids_matching_regex() {  # PIDs of processes (from a snapshot already on stdin) whose command line matches an extended regex
	  grep -Ei -- "$1" | awk 'NF{print $1}' || true  # `|| true`: zero matches is the common case, not a failure
	}
	_cleanse_pids_matching_all() {  # PIDs of processes (from a snapshot already on stdin) whose command line contains every literal substring given as an argument (no regex escaping needed for paths)
	  local lines pat
	  lines=$(cat)
	  for pat in "$@"; do
		lines=$(printf '%s\n' "$lines" | grep -F -- "$pat" || true)
	  done
	  printf '%s\n' "$lines" | awk 'NF{print $1}'
	}
	build_server_pattern='VBCSCompiler|Microsoft\.CodeAnalysis\.Razor\.[A-Za-z.]*Server|[[:space:]]rzc(\.dll)?([[:space:]]|$)'
	_cleanse_pids_matching_regex_and_substring() {  # PIDs (from a snapshot already on stdin) whose command line matches an extended regex AND contains a literal substring -- used to scope the build-server name pattern to this checkout's own bootstrapped SDK, so a force-kill can never reach some other checkout's or tool's build server
	  local pattern="$1" substr="$2"
	  grep -Ei -- "$pattern" | grep -F -- "$substr" | awk 'NF{print $1}' || true
	}
	_cleanse_ancestor_pids() {  # walks the PPID chain from this script's own PID up to and including PID 1 (or as far as `ps` can resolve) -- kill candidates get filtered against this set so cleanse can never terminate its own invoking shell/CI agent, even if that ancestor's command line happens to match the build-server/node-worker patterns (e.g. an automation wrapper that embeds the search text in its own argv). PID 1 is deliberately included, not just a loop bound: in a container where the invoking CI agent *is* PID 1, leaving it unprotected would make the container's own init process a killable candidate.
	  local pid="$$" ppid seen=" "
	  while [[ -n "$pid" && "$pid" != "0" && "$seen" != *" $pid "* ]]; do
		printf '%s\n' "$pid"
		seen="$seen$pid "
		[[ "$pid" == "1" ]] && break
		ppid=$(ps -o ppid= -p "$pid" 2>/dev/null | tr -d '[:space:]')
		pid="$ppid"
	  done
	}
	_cleanse_kill_pid_tree() {  # kills a pid's children first (portable ps -eo pid,ppid) then the pid itself, TERM then escalating to KILL if it's still alive after a short wait -- only reports success once the pid is confirmed gone AND this call actually had to signal it, since a delivered signal (kill's own exit code) doesn't mean the process actually died (e.g. it traps/ignores TERM), and a candidate that exited on its own between snapshot and kill attempt (e.g. the shutdown RPC took effect a little late) was never force-killed by cleanse at all
	  local pid="$1" child deadline
	  [[ -z "$pid" ]] && return 1
	  for child in $(ps -eo pid,ppid 2>/dev/null | awk -v p="$pid" '$2==p{print $1}'); do
		_cleanse_kill_pid_tree "$child"
	  done
	  kill -0 "$pid" 2>/dev/null || return 1  # already gone on its own -- nothing for this call to count as killed
	  kill -TERM "$pid" 2>/dev/null
	  deadline=$((SECONDS + 5))
	  while kill -0 "$pid" 2>/dev/null && (( SECONDS < deadline )); do
		sleep 0.2
	  done
	  if kill -0 "$pid" 2>/dev/null; then
		kill -KILL "$pid" 2>/dev/null
		sleep 0.2
	  fi
	  ! kill -0 "$pid" 2>/dev/null
	}
	ancestor_pids=$(_cleanse_ancestor_pids)
	if [[ -n "$dotnet_exe" ]]; then  # `build-server shutdown` always reports success whether or not a server was actually running, so its own output can't say what happened -- diff the PIDs before/after instead
	  before_pids=$(_cleanse_ps_snapshot | _cleanse_pids_matching_regex "$build_server_pattern")
	  "$dotnet_exe" build-server shutdown >/dev/null 2>&1 || true
	  if [[ -n "$before_pids" ]]; then
		after_pids=$(_cleanse_ps_snapshot | _cleanse_pids_matching_regex "$build_server_pattern")
		stopped=$(comm -23 <(sort -u <<<"$before_pids") <(sort -u <<<"$after_pids") | grep -c '^[0-9]\+$' || true)
		if [[ "${stopped:-0}" -gt 0 ]]; then
		  echo "Stopped $stopped build server process(es) (VBCSCompiler/Razor) via 'dotnet build-server shutdown'."
		fi
		# `build-server shutdown` talks to the RPC pipe of servers registered by *this* SDK; a server started
		# by a different dotnet install (or one whose RPC pipe is already wedged/orphaned) doesn't respond to
		# it and survives silently. Force-killing is scoped tightly to avoid collateral damage: only a PID that
		# (a) was already alive in the *original* before_pids snapshot -- never one that merely appears in a
		# later snapshot, which could be an unrelated process that started in between -- (b) is still alive
		# after the shutdown call, and (c) belongs to this checkout's own bootstrapped `.dotnet` SDK (the same
		# scope MSBuild node-reuse workers below are held to) is unconditionally stale and gets force-killed.
		# A build server for a different repo/SDK is left alone even if its name matches the pattern. The
		# trailing "/" on the scope substring is required, not cosmetic: without it, a sibling directory whose
		# name merely starts with ".dotnet" (e.g. a ".dotnet-old" leftover from a prior bootstrap) would also
		# match, since "$scriptroot/.dotnet-old/..." contains "$scriptroot/.dotnet" as a plain substring.
		scoped_after_pids=$(_cleanse_ps_snapshot | _cleanse_pids_matching_regex_and_substring "$build_server_pattern" "$scriptroot/.dotnet/")
		survivor_pids=$(comm -12 <(sort -u <<<"$before_pids") <(sort -u <<<"$scoped_after_pids"))
		# Never kill this script's own invoking shell/CI agent, even if it happens to match the scoped
		# pattern above (e.g. an automation wrapper whose own command line embeds the search text).
		survivor_pids=$(comm -23 <(sort -u <<<"$survivor_pids") <(sort -u <<<"$ancestor_pids"))
		if [[ -n "$survivor_pids" ]]; then
		  force_killed=0
		  while IFS= read -r pid; do
			[[ -z "$pid" ]] && continue
			_cleanse_kill_pid_tree "$pid" && force_killed=$((force_killed + 1))
		  done <<< "$survivor_pids"
		  if [[ "$force_killed" -gt 0 ]]; then
			echo "Force-killed $force_killed build server process(es) that ignored 'dotnet build-server shutdown'."
		  fi
		fi
	  fi
	fi
	# Node-reuse MSBuild worker processes are a different mechanism from build servers above -- left behind by
	# any dotnet/MSBuild invocation that didn't pass --nodeReuse false (an IDE build, a bare `dotnet build`/
	# `dotnet test`, `dotnet run --file eng/generate-compiler-code.cs`, ...) -- and are never registered as
	# build servers, so `build-server shutdown` can't see or stop them. cleanse itself never launches a build,
	# so any live MSBuild.dll worker rooted at this repo's own bootstrapped SDK is unconditionally stale.
	# Trailing "/" on the scope substring is required for the same reason as the build-server scope above --
	# without it, a ".dotnet-old"-style sibling directory would falsely match too.
	node_worker_pids=$(_cleanse_ps_snapshot | _cleanse_pids_matching_all "$scriptroot/.dotnet/" "MSBuild.dll")
	node_worker_pids=$(comm -23 <(sort -u <<<"$node_worker_pids") <(sort -u <<<"$ancestor_pids"))  # never kill this script's own invoking shell/CI agent -- see the build-server exclusion above
	if [[ -n "$node_worker_pids" ]]; then
	  killed=0
	  while IFS= read -r pid; do
		[[ -z "$pid" ]] && continue
		_cleanse_kill_pid_tree "$pid" && killed=$((killed + 1))
	  done <<< "$node_worker_pids"
	  if [[ "$killed" -gt 0 ]]; then
		echo "Killed $killed leftover MSBuild node-reuse worker process(es)."
	  fi
	fi
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
		echo "Cleansed $total_formatted of artefacts."
	  fi
	else
	  echo "No artefacts to cleanse."
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
