#!/usr/bin/env bash
set -euo pipefail

# Bash port of folly.ps1 for non-Windows environments. Keep behavior in sync with folly.ps1.
#
# FollyOfTheUnbound.slnx is Roslyn.slnx with the RoslynAnalyzers projects removed. Those projects
# build the shared Formatting/Extensions files against an older, released Microsoft.CodeAnalysis
# reference (by design, since an analyzer needs a stable host) and don't know about C#Unbound's new
# SyntaxKinds, so they fail whenever the language grows. They're Roslyn's own dogfooding lint tools
# anyway - not needed to build or use C#Unbound. Kept as its own file (not an edit to Roslyn.slnx)
# so merging from upstream dotnet/roslyn doesn't conflict here.

action="${1:-}"
config="${2:-}"

scriptroot="$(cd -P "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
solution="FollyOfTheUnbound.slnx"
build_script="$scriptroot/eng/build.sh"
nupkg_root="$scriptroot/../.nupkg/FotU"

if [[ -z "$config" || "$config" == "Debug" ]]; then
  configuration="Debug"
  nupkg_dir="$nupkg_root/Debug"
elif [[ "$config" == "Release" ]]; then
  configuration="Release"
  nupkg_dir="$nupkg_root/Release"
else
  echo "Unrecognized configuration '$config'. Expected 'Debug', 'Release', or omitted (defaults to Debug)." >&2
  exit 1
fi

# Plain `dotnet build`/`dotnet pack` bypass this repo's SDK bootstrap and Arcade toolset (the thing
# that made build.sh succeed earlier when a bare `dotnet build <csproj>` failed with an SDK-not-found
# error), so both actions go through eng/build.sh instead.

case "$action" in
  attune)
    "$build_script" --restore --solution "$solution" --configuration "$configuration"
    ;;
  weave)
    "$build_script" --restore --build --solution "$solution" --configuration "$configuration"
    ;;
  bind)
    "$build_script" --restore --build --pack --solution "$solution" --configuration "$configuration"
    ;;
  *)
    echo "Unrecognized action '$action'. Expected 'attune', 'weave', or 'bind'." >&2
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
