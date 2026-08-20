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

# Installs PowerShell (pwsh) via Microsoft's apt repo, so `folly.ps1` and its
# `scripts/test-folly-*.ps1` harnesses (e.g. `folly.sh scry reflection`) can
# actually be run and verified in this sandbox, not just read. No snapd here,
# so `snap install powershell --classic` isn't an option. Runs in the
# background alongside attune; check pwsh-install.log for progress.
if ! command -v pwsh >/dev/null 2>&1; then
  pwsh_log="$CLAUDE_PROJECT_DIR/.claude/hooks/pwsh-install.log"
  (
    curl -sSL -o /tmp/packages-microsoft-prod.deb https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb
    sudo dpkg -i /tmp/packages-microsoft-prod.deb
    sudo apt-get update -qq
    sudo apt-get install -y powershell
  ) >"$pwsh_log" 2>&1 &
  disown
  echo "Started PowerShell install in the background (PID $!); progress logged to $pwsh_log"
fi
