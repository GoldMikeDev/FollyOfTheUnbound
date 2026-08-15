#!/bin/bash
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "$CLAUDE_PROJECT_DIR"

# Bootstraps the .NET SDK into .dotnet/ and restores NuGet packages for
# FollyOfTheUnbound.slnx, so `dotnet` and the build/test scripts work
# without a manual `./folly.sh attune` first. Runs in the background so
# SessionStart doesn't block on the restore; check attune.log for progress.
log="$CLAUDE_PROJECT_DIR/.claude/hooks/attune.log"
nohup ./folly.sh attune >"$log" 2>&1 &
disown
echo "Started './folly.sh attune' in the background (PID $!); progress logged to $log"
