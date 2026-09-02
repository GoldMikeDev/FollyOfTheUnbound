#!/usr/bin/env bash
# Copyright (c) .NET Foundation and contributors. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.

# Stop script if unbound variable found (use ${var:-} if intentional)
set -u

# Stop script if subcommand fails
set -e

usage()
{
  echo "Common settings:"
  echo "  --configuration <value>    Build configuration: 'Debug' or 'Release' (short: -c)"
  echo "  --verbosity <value>        Msbuild verbosity: q[uiet], m[inimal], n[ormal], d[etailed], and diag[nostic] (short: -v)"
  echo "  --binaryLog                Create MSBuild binary log (short: -bl)"
  echo ""
  echo "Actions:"
  echo "  --restore                  Restore projects required to build (short: -r)"
  echo "  --build                    Build all projects (short: -b)"
  echo "  --rebuild                  Rebuild all projects"
  echo "  --pack                     Build nuget packages"
  echo "  --publish                  Publish build artifacts"
  echo "  --sign                     Sign build artifacts"
  echo "  --help                     Print help and exit"
  echo ""
  echo "Test actions:"
  echo "  --testCoreClr              Run unit tests on .NET Core (short: --test, -t)"
  echo "  --testDesktop              Run unit tests on .NET Framework (Windows host only)"
  echo "  --testMono                 Run unit tests on Mono"
  echo "  --testCompilerOnly         Run only the compiler unit tests"
  echo "  --testFilter <value>       xUnit filter to pass to RunTests' --testfilter, e.g. FullyQualifiedName~TestClass1|Category=CategoryA"
  echo "  --testIOperation           Run unit tests with the IOperation test hook"
  echo "  --testSuppressConsoleSummary  Suppress only RunTests' own final PASSED/FAILED/TIMEOUT table"
  echo "                             from the console (still written to the log file); the live"
  echo "                             progress table is unaffected. For a caller building its own"
  echo "                             combined summary across multiple RunTests passes -- see folly.sh scry"
  echo "  --testRuntimeAsync         Run unit tests with runtime async validation enabled"
  echo "  --testTimeout <minutes>    Override RunTests' whole-run --timeout watchdog"
  echo "  --collectDumps             Collect dumps from test runs (genuine Windows host only)"
  echo ""
  echo "Advanced settings:"
  echo "  --ci                       Building in CI"
  echo "  --bootstrap                Build using a bootstrap compilers"
  echo "  --bootstrapDir <path>      Build using bootstrap compiler already built at the specified location (skips rebuilding it)"
  echo "  --runAnalyzers             Run analyzers during build operations"
  echo "  --skipDocumentation        Skip generation of XML documentation files"
  echo "  --prepareMachine           Prepare machine for CI run, clean up processes after build"
  echo "  --msbuildMultiThreaded <value> Sets MSBuild's multi-threaded mode, i.e. the -mt switch ('true' or 'false') (short: --mt)"
  echo "  --nodeReuse <value>        Sets nodereuse msbuild parameter ('true' or 'false')"
  echo "  --warnAsError              Treat all warnings as errors"
  echo "  --warnNotAsError <codes>   Suppress specific warnings from being treated as errors (semi-colon delimited)"
  echo "  --sourceBuild              Build the repository in source-only mode"
  echo "  --productBuild             Build the repository in product-build mode."
  echo "  --fromVMR                  Build the repository in product-build mode."
  echo "  --solution                 Solution to build (default is Roslyn.slnx)"
  echo ""
  echo "Command line arguments starting with '/p:' are passed through to MSBuild."
}

source="${BASH_SOURCE[0]}"

# resolve $source until the file is no longer a symlink
while [[ -h "$source" ]]; do
  scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"
  source="$(readlink "$source")"
  # if $source was a relative symlink, we need to resolve it relative to the path where the
  # symlink file was located
  [[ $source != /* ]] && source="$scriptroot/$source"
done
scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"

restore=false
build=false
rebuild=false
pack=false
sign=false
publish=false
test_core_clr=false
test_desktop=false
test_mono=false
test_ioperation=false
test_runtime_async=false
test_compiler_only=false
test_filter=""
test_timeout=0
test_suppress_console_summary=false
collect_dumps=false

configuration="Debug"
verbosity='minimal'
binary_log=false
ci=false
helix=false
helix_queue_name=""
helix_api_access_token=""
bootstrap=false
bootstrap_dir_arg=""
run_analyzers=false
skip_documentation=false
prepare_machine=false
# Empty means "not specified"; tools.sh leaves it off unless it's explicitly requested.
msbuild_multi_threaded=''
warn_as_error=false
warn_not_as_error=""
properties=()
source_build=false
product_build=false
from_vmr=false
solution_to_build="Roslyn.slnx"

args=""

if [[ $# = 0 ]]
then
  usage
  exit 1
fi

while [[ $# > 0 ]]; do
  opt="$(echo "$1" | awk '{print tolower($0)}')"
  case "$opt" in
    --help|-h)
      usage
      exit 0
      ;;
    --configuration|-c)
      configuration=$2
      args="$args $1"
      shift
      ;;
    --verbosity|-v)
      verbosity=$2
      args="$args $1"
      shift
      ;;
    --binarylog|-bl)
      binary_log=true
      ;;
    --restore|-r)
      restore=true
      ;;
    --build|-b)
      build=true
      ;;
    --rebuild)
      rebuild=true
      ;;
    --pack)
      pack=true
      ;;
    --publish)
      publish=true
      ;;
    --sign)
      sign=true
      ;;
    --testcoreclr|--test|-t)
      test_core_clr=true
      ;;
    --testdesktop)
      test_desktop=true
      ;;
    --testmono)
      test_mono=true
      ;;
    --testcompileronly)
      test_compiler_only=true
      ;;
    --testfilter)
      if [[ -z "${2:-}" ]]; then
        echo "'--testFilter' requires a value." >&2
        exit 1
      fi
      test_filter="$2"
      args="$args $1"
      shift
      ;;
    --testioperation)
      test_ioperation=true
      ;;
    --testsuppressconsolesummary)
      # Suppresses only RunTests' own final PASSED/FAILED/TIMEOUT table from the console (still
      # written to the log file) -- never the live per-work-item progress table. Mirrors build.ps1's
      # own -testSuppressConsoleSummary; see Options.SuppressConsoleSummary/TestRunner.Print in
      # src/Tools/RunTests/. folly.sh scry passes this when both Core and Framework legs run
      # together on a Windows host, so it can print both legs' tables combined afterward instead of
      # each leg's table also printing live and getting duplicated by that combined block.
      test_suppress_console_summary=true
      ;;
    --testruntimeasync)
      test_runtime_async=true
      ;;
    --collectdumps)
      collect_dumps=true
      ;;
    --testtimeout)
      # Overrides RunTests' whole-run --timeout watchdog (minutes); see the -testTimeout parameter
      # in build.ps1 for why this exists as a call-site override instead of a hardcoded value.
      # Validated (and leading zeros normalized to decimal, matching folly.sh's own --timeout
      # parsing) here rather than left for the later "$test_timeout" -gt 0 arithmetic check: under
      # this script's `set -u`, a missing value or a non-numeric one (e.g. "banana") would abort
      # that check with an unrelated "unbound variable"/"value too great for base" shell error
      # instead of a controlled argument error.
      # The digit-count cap (9 digits, matching folly.sh's own --timeout parsing) matters as much
      # as the regex itself: without it, a huge-enough digits-only value would silently wrap
      # around inside bash's 64-bit $(( )) arithmetic below into some unrelated small positive
      # number instead of being rejected.
      if [[ -z "${2:-}" || ! "$2" =~ ^[0-9]{1,9}$ ]]; then
        echo "'--testTimeout' requires a positive integer minute count (up to 999999999), got '${2:-}'."
        exit 1
      fi
      test_timeout=$((10#$2))
      # Upper bound matches RunTests' own limit -- see folly.sh's identical check for why
      # (Task.Delay's supported millisecond range, ~71582.79 minutes).
      if [[ "$test_timeout" -le 0 || "$test_timeout" -gt 71582 ]]; then
        echo "'--testTimeout' requires a positive integer minute count, up to 71582 (Task.Delay's supported maximum), got '$2'."
        exit 1
      fi
      args="$args $1"
      shift
      ;;
    --ci)
      ci=true
      ;;
    --helix)
      helix=true
      ;;
    --helixqueuename)
      helix_queue_name=$2
      args="$args $1"
      shift
      ;;
    --helixapiaccesstoken)
      helix_api_access_token=$2
      args="$args $1"
      shift
      ;;
    --bootstrap)
      bootstrap=true
      # Bootstrap requires restore
      restore=true
      ;;
    --bootstrapdir)
      if [[ -z "${2:-}" ]]; then
        echo "'--bootstrapDir' requires a path." >&2
        exit 1
      fi
      bootstrap=true
      bootstrap_dir_arg="$2"
      restore=true
      args="$args $1"
      shift
      ;;
    --runanalyzers)
      run_analyzers=true
      ;;
    --skipdocumentation)
      skip_documentation=true
      ;;
    --preparemachine)
      prepare_machine=true
      ;;
    --msbuildmultithreaded|--mt)
      msbuild_multi_threaded=$2
      args="$args $1"
      shift
      ;;
    --nodereuse)
      node_reuse=$2
      args="$args $1"
      shift
      ;;
    --warnaserror)
      warn_as_error=true
      ;;
    --warnnotaserror)
      warn_not_as_error=$2
      args="$args $1"
      shift
      ;;
    --sourcebuild|--source-build|-sb)
      source_build=true
      product_build=true
      ;;
    --productbuild|--product-build|-pb)
      product_build=true
      ;;
    --fromvmr|--from-vmr)
      from_vmr=true
      ;;
    --solution)
      solution_to_build=$2
      args="$args $1"
      shift
      ;;
    /p:*)
      properties+=("$1")
      ;;
    /clp:*)
      properties+=("$1")
      ;;
    *)
      echo "Invalid argument: $1"
      usage
      exit 1
      ;;
  esac
  args="$args $1"
  shift
done

# .NET Framework test binaries only run on a genuine Windows host (net472 has no cross-platform
# runtime), so --testDesktop is rejected everywhere else -- matches build.ps1's -testDesktop, which
# only ever runs on Windows in the first place since it's a PowerShell script.
is_windows_host() {
  case "${OSTYPE:-}" in
    msys*|cygwin*|win32*) return 0 ;;
  esac
  case "$(uname -s 2>/dev/null)" in
    MINGW*|MSYS*|CYGWIN*) return 0 ;;
  esac
  return 1
}

if [[ "$test_desktop" == true ]] && ! is_windows_host; then
  echo "'--testDesktop' requires a Windows host (.NET Framework tests have no cross-platform runtime)." >&2
  exit 1
fi

if [[ "$test_desktop" == true && "$test_runtime_async" == true ]]; then
  echo "Cannot run desktop tests with runtime async validation enabled."
  exit 1
fi

# --collectDumps enables RunTests' Windows Error Reporting registry-based dump collection
# (DumpUtil.EnableRegistryDumpCollection in src/Tools/RunTests/ProcDumpUtil.cs, via the Windows
# registry's LocalDumps key), which only exists on a genuine Windows host -- unlike --testDesktop
# above, this isn't a hard requirement to satisfy the caller's request, so a non-Windows host just
# skips it with a note rather than failing the whole build; folly.sh's scry passes --collectDumps
# unconditionally on every host and relies on this to no-op safely off Windows.
if [[ "$collect_dumps" == true ]] && ! is_windows_host; then
  echo "Skipping '--collectDumps': Windows Error Reporting registry-based dump collection requires a genuine Windows host."
  collect_dumps=false
fi

# Import Arcade functions
. "$scriptroot/common/tools.sh"

# Mirrors build.ps1's Ensure-ProcDump: locates procdump.exe for RunTests' --procdumppath (currently
# only ever echoed back to the console by RunTests, not consumed for any actual dump-collection
# logic -- see Program.cs's "Proc dump location:" line -- but mirrored here anyway for output parity
# between folly.sh and folly.ps1). Only ever called after the is_windows_host gate above has already
# confirmed a genuine Windows host, so a Windows-style "C:\..." path here is safe to hand straight to
# RunTests (a .NET process on that same Windows host) without any bash-side path translation.
# Downloads Procdump.zip from sysinternals.com on a machine that doesn't already have procdump.exe
# cached -- a network failure here (offline machine, blocked download) shouldn't abort the whole
# `set -e` script over a value RunTests only ever echoes back to the console (see Program.cs's
# "Proc dump location:" line), so this reports failure via _EnsureProcDumpFailed instead of letting
# curl/wget/unzip's non-zero exit propagate; callers must check it before using _EnsureProcDump.
function EnsureProcDump {
  _EnsureProcDumpFailed=0

  # Jenkins images default to having procdump installed in the root -- use that if available to
  # avoid an unnecessary download, matching build.ps1's own check (and its directory-not-file-path
  # return value in this one case, which -- like the rest of this value -- is never consumed for
  # more than console output).
  if [[ -f "C:/SysInternals/procdump.exe" ]]; then
    _EnsureProcDump="C:\\SysInternals"
    return
  fi

  local out_dir="$tools_dir/ProcDump"
  local file_path="$out_dir/procdump.exe"
  if [[ ! -f "$file_path" ]]; then
    mkdir -p "$out_dir"
    local zip_file_path="$tools_dir/procdump.zip"
    echo "Downloading Procdump..."
    if command -v curl > /dev/null; then
      curl "https://download.sysinternals.com/files/Procdump.zip" -sSL --retry 10 --create-dirs -o "$zip_file_path" || { _EnsureProcDumpFailed=1; return; }
    else
      wget -v -O "$zip_file_path" "https://download.sysinternals.com/files/Procdump.zip" || { _EnsureProcDumpFailed=1; return; }
    fi
    unzip -o "$zip_file_path" -d "$out_dir" || { _EnsureProcDumpFailed=1; return; }
  fi

  # return value
  _EnsureProcDump="$file_path"
}

function MakeBootstrapBuild {
  echo "Building bootstrap compiler"

  local dir="$artifacts_dir/Bootstrap"

  rm -rf $dir
  mkdir -p $dir

  local package_name="Microsoft.Net.Compilers.Toolset"
  local project_path=src/NuGet/$package_name/AnyCpu/$package_name.Package.csproj

  # $dir/$log_dir stay POSIX-form everywhere else in this function (unzip/chmod/rm/mkdir below are
  # MSYS-side tools, not native ones) -- only the two operands actually consumed by native dotnet.exe
  # here need the ToNativePath conversion (see its own comment above).
  dotnet pack -nologo "$project_path" -p:ContinuousIntegrationBuild=$ci -p:DotNetUseShippingVersions=true -p:InitialDefineConstants=BOOTSTRAP -p:PackageOutputPath="$(ToNativePath "$dir")" -bl:"$(ToNativePath "$log_dir/Bootstrap.binlog")"
  unzip "$dir/$package_name.*.nupkg" -d "$dir"
  chmod -R 755 "$dir"

  echo "Cleaning Bootstrap compiler artifacts"
  dotnet clean "$project_path"

  if [[ "$node_reuse" == true ]]; then
    dotnet build-server shutdown
  fi

  # return value
  _MakeBootstrapBuild=$dir
}

# Converts a POSIX path to the native Windows form a native (non-MSYS) tool -- MSBuild.exe, or plain
# `dotnet`/`dotnet exec` -- actually needs, using cygpath (shipped with Git Bash/MSYS2). Git-for-
# Windows' bash (MSYS2) normally auto-converts a POSIX path handed to such a tool, but folly.sh's own
# `MSYS2_ARG_CONV_EXCL` (see the comment above that line in folly.sh and .github/memory/KNOWN_ISSUES.md)
# excludes specific MSBuild switch prefixes (like `/p:...`) from that conversion, since MSYS misreads
# an unrecognized `/`-prefixed switch as a Unix path and corrupts it. A path embedded inside an
# excluded switch (e.g. the value in `/p:Projects=...`), or inside an argument that was never
# `/`-prefixed to begin with (single-dash `dotnet` CLI syntax like `-p:PackageOutputPath=...`, or a
# plain `--flag value` pair), never gets MSYS's automatic conversion either way and needs this explicit
# one instead. A no-op everywhere else (WSL, native Linux/macOS have no $MSYSTEM and no such
# translation gap to begin with) or once already in native form (idempotent).
function ToNativePath {
  local posix_path=$1
  if [[ -z "$posix_path" ]]; then
    echo ""
  elif [[ -n "${MSYSTEM:-}" ]] && command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$posix_path"
  else
    echo "$posix_path"
  fi
}

function BuildSolution {
  local solution=$solution_to_build
  echo "$solution:"

  InitializeToolset
  local toolset_build_proj
  toolset_build_proj=$(ToNativePath "$_InitializeToolset")

  local bl=""
  if [[ "$binary_log" = true ]]; then
    bl="/bl:\"$(ToNativePath "$log_dir/Build.binlog")\""
    export RoslynCommandLineLogFile="$log_dir/vbcscompiler.log"
  fi

  local projects
  projects=$(ToNativePath "$repo_root/$solution")
  local repo_root_native
  repo_root_native=$(ToNativePath "$repo_root")

  UNAME="$(uname)"
  # NuGet often exceeds the limit of open files on Mac and Linux
  # https://github.com/NuGet/Home/issues/2163
  if [[ "$UNAME" == "Darwin" || "$UNAME" == "Linux" ]]; then
    ulimit -n 6500 || echo "Cannot change ulimit"
  fi

  if [[ "$test_ioperation" == true ]]; then
    export ROSLYN_TEST_IOPERATION="true"

    if [[ "$test_mono" != true && "$test_core_clr" != true ]]; then
      test_core_clr=true
    fi
  fi

  if [[ "$test_runtime_async" == true ]]; then
    export DOTNET_RuntimeAsync="1"

    if [[ "$test_mono" != true && "$test_core_clr" != true ]]; then
      test_core_clr=true
    fi
  fi

  local test=false
  local test_runtime=""
  local mono_tool=""
  local test_runtime_args=""
  if [[ "$test_mono" == true ]]; then
    mono_path=`command -v mono`
    # Echo out the mono version to the command line so it's visible in CI logs. It's not fixed
    # as we're using a feed vs. a hard coded package.
    if [[ "$ci" == true ]]; then
      mono --version
    fi

    test=true
    test_runtime="/p:TestRuntime=Mono"
    mono_tool="/p:MonoTool=\"$mono_path\""
    test_runtime_args="--debug"
  fi

  local msbuild_warn_as_error=""
  if [[ "$warn_as_error" == true ]]; then
    msbuild_warn_as_error="/warnAsError"
  fi

  local msbuild_warn_not_as_error=""
  if [[ "$warn_not_as_error" != "" && "$warn_as_error" == true ]]; then
    msbuild_warn_not_as_error="/warnNotAsError:$warn_not_as_error"
  fi

  local generate_documentation_file=""
  if [[ "$skip_documentation" == true ]]; then
    generate_documentation_file="/p:GenerateDocumentationFile=false"
  fi

  local roslyn_use_hard_links=""
  if [[ "$ci" == true ]]; then
    roslyn_use_hard_links="/p:ROSLYNUSEHARDLINKS=true"
  fi

  MSBuild $toolset_build_proj \
    $bl \
    /p:Configuration=$configuration \
    /p:Projects="$projects" \
    /p:RepoRoot="$repo_root_native" \
    /p:Restore=$restore \
    /p:Build=$build \
    /p:Rebuild=$rebuild \
    /p:Test=$test \
    /p:Pack=$pack \
    /p:Publish=$publish \
    /p:Sign=$sign \
    /p:RunAnalyzersDuringBuild=$run_analyzers \
    /p:BootstrapBuildPath="$(ToNativePath "$bootstrap_dir")" \
    /p:ContinuousIntegrationBuild=$ci \
    /p:TreatWarningsAsErrors=$warn_as_error \
    /p:TestRuntimeAdditionalArguments=$test_runtime_args \
    /p:DotNetBuildSourceOnly=$source_build \
    /p:DotNetBuild=$product_build \
    /p:DotNetBuildFromVMR=$from_vmr \
    $test_runtime \
    $mono_tool \
    $msbuild_warn_as_error \
    $msbuild_warn_not_as_error \
    $generate_documentation_file \
    $roslyn_use_hard_links \
    ${properties[@]+"${properties[@]}"}
}

function GetCompilerTestAssembliesIncludePaths {
  assemblies="--include '^Microsoft\.CodeAnalysis\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.CompilerServer\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.CSharp\.Syntax\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.CSharp\.Symbol\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.CSharp\.Semantic\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.CSharp\.Emit\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.CSharp\.Emit2\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.CSharp\.Emit3\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.CSharp\.CSharp15\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.CSharp\.IOperation\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.CSharp\.CommandLine\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.VisualBasic\.Syntax\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.VisualBasic\.Symbol\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.VisualBasic\.Semantic\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.VisualBasic\.Emit\.UnitTests$'"
  assemblies+=" --include '^Roslyn\.Compilers\.VisualBasic\.IOperation\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.CodeAnalysis\.VisualBasic\.CommandLine\.UnitTests$'"
  assemblies+=" --include '^Microsoft\.Build\.Tasks\.CodeAnalysis\.UnitTests$'"
  echo "$assemblies"
}

install=false
if [[ "$restore" == true || "$test_core_clr" == true || "$test_desktop" == true ]]; then
  install=true
fi
InitializeDotNetCli $install
# Source only builds would not have 'dotnet' ambiently available.
if [[ "$restore" == true && "$source_build" != true ]]; then
  dotnet tool restore
fi

bootstrap_dir=""
if [[ -n "$bootstrap_dir_arg" ]]; then
  # Reuse an already-built bootstrap compiler instead of rebuilding it -- matches build.ps1's own
  # -bootstrapDir, which exists so a caller invoking this script more than once in the same run
  # (e.g. folly.sh's 'scry', which builds once then runs each requested test leg as its own
  # invocation) only pays MakeBootstrapBuild's cost the first time.
  bootstrap_dir="$bootstrap_dir_arg"
elif [[ "$bootstrap" == true ]]; then
  MakeBootstrapBuild
  bootstrap_dir=$_MakeBootstrapBuild
fi

if [[ "$restore" == true || "$build" == true || "$rebuild" == true || "$test_mono" == true ]]; then
  BuildSolution
fi

# Folly.sh's 'scry' runs Core and Framework as two separate invocations of this script when both
# are requested; FOTU_TEST_RESULTS_SUFFIX lets the caller keep each leg's TestResults/log output in
# its own directory instead of one leg's output clobbering the other's. Mirrors build.ps1's own
# $env:FOTU_TEST_RESULTS_SUFFIX handling in RunTestsInternal.
runtests_log_dir="$log_dir"
runtests_out_dir="$artifacts_dir/TestResults/$configuration"
if [[ -n "${FOTU_TEST_RESULTS_SUFFIX:-}" ]]; then
  runtests_log_dir="${log_dir}-${FOTU_TEST_RESULTS_SUFFIX}"
  runtests_out_dir="${runtests_out_dir}-${FOTU_TEST_RESULTS_SUFFIX}"
fi

# RunTests.dll (a managed app run via native dotnet.exe under Git Bash, same as MSBuild.exe -- see
# ToNativePath's own comment above) needs these path arguments in native Windows form. $runtests_log_dir
# and $runtests_out_dir themselves stay POSIX-form -- everything else in this script (including the
# "See $runtests_out_dir..." messages below) keeps using them as bash-side paths, and the underlying
# file this dll actually writes to is the same location on disk either way, so folly.sh's own later
# POSIX-path reads of it are unaffected.
runtests_log_dir_native=$(ToNativePath "$runtests_log_dir")
runtests_out_dir_native=$(ToNativePath "$runtests_out_dir")
runtests_dll_path=$(ToNativePath "$scriptroot/../artifacts/bin/RunTests/${configuration}/net10.0/RunTests.dll")
dotnet_cli_native=$(ToNativePath "${_InitializeDotNetCli}/dotnet")

if [[ "$test_core_clr" == true ]]; then
  runtests_args="--out \"$runtests_out_dir_native\""

  if [[ "$test_compiler_only" == true ]]; then
    runtests_args="$runtests_args $(GetCompilerTestAssembliesIncludePaths)"
  fi

  if [[ -n "$test_filter" ]]; then
    runtests_args="$runtests_args --testfilter \"$test_filter\""
  fi

  if [[ -n "$helix_queue_name" ]]; then
    runtests_args="$runtests_args --helixQueueName $helix_queue_name"
  fi

  if [[ -n "$helix_api_access_token" ]]; then
    runtests_args="$runtests_args --helixApiAccessToken $helix_api_access_token"
  fi

  if [[ "$helix" == true ]]; then
    runtests_args="$runtests_args --helix"
  fi

  if [[ "$ci" != true ]]; then
    runtests_args="$runtests_args --html"
  fi

  if [[ "$test_suppress_console_summary" == true ]]; then
    runtests_args="$runtests_args --suppressConsoleSummary"
  fi

  # Matches build.ps1's own -testCoreClr default of 90 minutes for RunTests' whole-run watchdog;
  # --testTimeout overrides it, and (matching build.ps1) Helix runs skip the watchdog entirely since
  # Helix has its own external timeout management.
  if [[ "$helix" != true ]]; then
    if [[ "$test_timeout" -le 0 ]]; then
      test_timeout=90
    fi
    runtests_args="$runtests_args --timeout $test_timeout"
  fi

  # Matches build.ps1's own $collectDumps handling -- see EnsureProcDump above and the is_windows_host
  # gate that already turned $collect_dumps back off on a non-Windows host. --collectdumps and
  # --procdumppath are independent: --collectdumps alone is what enables RunTests' WER registry
  # collection; --procdumppath only ever feeds its console "Proc dump location:" line (see
  # Program.cs), nothing functional. So a failure acquiring ProcDump should only drop the cosmetic
  # --procdumppath, never --collectdumps itself.
  if [[ "$collect_dumps" == true ]]; then
    runtests_args="$runtests_args --collectdumps"
    EnsureProcDump
    if [[ "$_EnsureProcDumpFailed" == 1 ]]; then
      echo "Failed to acquire ProcDump; '--collectDumps' is still enabled, but 'Proc dump location:' will show as not configured."
    else
      runtests_args="$runtests_args --procdumppath \"$_EnsureProcDump\""
    fi
  fi

  if [[ "$ci" == true ]]; then
    dotnet exec "$runtests_dll_path" --runtime core --configuration ${configuration} --logs "$runtests_log_dir_native" --dotnet "$dotnet_cli_native" $runtests_args
  else
    # Locally, a non-zero exit from RunTests almost always just means some test suites had
    # failures (not that the build tooling itself broke), so report it concisely instead of
    # letting `set -e` exit silently. The HTML/xUnit failure logs under $log_dir already have
    # the actual details. Matches the equivalent local-only summary in build.ps1.
    if ! dotnet exec "$runtests_dll_path" --runtime core --configuration ${configuration} --logs "$runtests_log_dir_native" --dotnet "$dotnet_cli_native" $runtests_args; then
      echo "Not all test suites succeeded. See $runtests_out_dir and $runtests_log_dir for details."
      exit 1
    fi
  fi
elif [[ "$test_desktop" == true ]]; then
  # elif, not a separate 'if': matches build.ps1's own $testDesktop branch, which only runs when
  # $testCoreClr is false -- test_core_clr silently takes priority if a caller somehow sets both.
  runtests_args="--out \"$runtests_out_dir_native\""

  if [[ "$test_compiler_only" == true ]]; then
    runtests_args="$runtests_args $(GetCompilerTestAssembliesIncludePaths)"
  else
    runtests_args="$runtests_args --include '\.UnitTests'"
    runtests_args="$runtests_args --exclude '\.InteractiveHost'"
  fi

  if [[ -n "$test_filter" ]]; then
    runtests_args="$runtests_args --testfilter \"$test_filter\""
  fi

  if [[ -n "$helix_queue_name" ]]; then
    runtests_args="$runtests_args --helixQueueName $helix_queue_name"
  fi

  if [[ -n "$helix_api_access_token" ]]; then
    runtests_args="$runtests_args --helixApiAccessToken $helix_api_access_token"
  fi

  if [[ "$helix" == true ]]; then
    runtests_args="$runtests_args --helix"
  fi

  if [[ "$ci" != true ]]; then
    runtests_args="$runtests_args --html"
  fi

  if [[ "$test_suppress_console_summary" == true ]]; then
    runtests_args="$runtests_args --suppressConsoleSummary"
  fi

  # Matches build.ps1's own -testDesktop default of 90 minutes for RunTests' whole-run watchdog;
  # --testTimeout overrides it, and (matching build.ps1) Helix runs skip the watchdog entirely since
  # Helix has its own external timeout management.
  if [[ "$helix" != true ]]; then
    if [[ "$test_timeout" -le 0 ]]; then
      test_timeout=90
    fi
    runtests_args="$runtests_args --timeout $test_timeout"
  fi

  # Matches build.ps1's own $collectDumps handling -- see EnsureProcDump above and the is_windows_host
  # gate that already turned $collect_dumps back off on a non-Windows host. --collectdumps and
  # --procdumppath are independent: --collectdumps alone is what enables RunTests' WER registry
  # collection; --procdumppath only ever feeds its console "Proc dump location:" line (see
  # Program.cs), nothing functional. So a failure acquiring ProcDump should only drop the cosmetic
  # --procdumppath, never --collectdumps itself.
  if [[ "$collect_dumps" == true ]]; then
    runtests_args="$runtests_args --collectdumps"
    EnsureProcDump
    if [[ "$_EnsureProcDumpFailed" == 1 ]]; then
      echo "Failed to acquire ProcDump; '--collectDumps' is still enabled, but 'Proc dump location:' will show as not configured."
    else
      runtests_args="$runtests_args --procdumppath \"$_EnsureProcDump\""
    fi
  fi

  if [[ "$ci" == true ]]; then
    dotnet exec "$runtests_dll_path" --runtime framework --configuration ${configuration} --logs "$runtests_log_dir_native" --dotnet "$dotnet_cli_native" $runtests_args
  else
    if ! dotnet exec "$runtests_dll_path" --runtime framework --configuration ${configuration} --logs "$runtests_log_dir_native" --dotnet "$dotnet_cli_native" $runtests_args; then
      echo "Not all test suites succeeded. See $runtests_out_dir and $runtests_log_dir for details."
      exit 1
    fi
  fi
fi
ExitWithExitCode 0
