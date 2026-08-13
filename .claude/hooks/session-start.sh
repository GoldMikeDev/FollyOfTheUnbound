#!/bin/bash
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "$CLAUDE_PROJECT_DIR"

# Bootstraps the .NET SDK into .dotnet/ and restores NuGet packages for
# FollyOfTheUnbound.slnx, so `dotnet` and the build/test scripts work
# without a manual `./folly.sh attune` first.
./folly.sh attune
