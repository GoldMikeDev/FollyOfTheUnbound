---
coverage: Repo-wide build/test entry points and PublicAPI tracking; layer-specific surfaces live in the instruction files
---

# API Map

Repo-wide entry points and the formal public-API tracking rules. Layer-specific surfaces (compiler error codes, IDE diagnostic IDs, extensibility patterns) live in the path-scoped instruction files:
- Compiler error codes, `MessageID`, Syntax/BoundNodes, codegen, `csc`/`vbc` → `.github/instructions/Compiler.instructions.md`
- IDE diagnostic IDs, MEF service/analyzer/code-fix exports, LSP → `.github/instructions/IDE.instructions.md`

## Build & Test Entry Points

| Command | Purpose |
|---------|---------|
| `build.sh` / `Build.cmd` | Full solution build (Arcade). |
| `dotnet build Compilers.slnf` | Compiler-only build. |
| `dotnet build Ide.slnf` | IDE-only build. |
| `dotnet build Razor.slnf` | Razor compiler & tooling-only build. |
| `test.sh` / `Test.cmd` | Full test run. |
| `dotnet test <test.csproj>` | Run a specific test project. |
| `dotnet run --file eng/generate-compiler-code.cs` | Regenerate Syntax/BoundNodes code. |
| `pwsh eng/validate-benchmarks.ps1 -configuration Release -ci` | Validate benchmark projects with BenchmarkDotNet Dry jobs; custom multi-job Razor harnesses use their explicit validation mode. Used by the correctness artifacts CI job. |
| `artifacts/bin/BuildBoss/<config>/net472/BuildBoss.exe -r <repo>/ -c <config> -p Roslyn.slnx` | Validate solutions, project files and build artifacts. Used by the correctness artifacts CI job and the bootstrap build. See `src/Tools/BuildBoss/README.md` for available checks and options. |
| `dotnet msbuild <proj> /t:UpdateXlf` | Refresh `.xlf` after `.resx` changes. |
| `folly.ps1 <attune\|weave\|bind\|scry> [Debug\|Release] [--core\|--desktop]` (Windows) / `folly.sh <attune\|weave\|bind\|scry> [Debug\|Release]` (Linux/macOS) | This fork's wrapper around `eng/build.ps1`/`eng/build.sh` for `FollyOfTheUnbound.slnx`: `attune` restores, `weave` restores+builds, `bind` restores+builds+packs (via the real Arcade toolset, not a bare `dotnet build`/`pack`, which bypasses this repo's SDK bootstrap). `bind` also copies the produced nupkgs to `../.nupkg/FotU/<Configuration>`. `scry` restores+builds+tests, but the two wrappers differ here: `folly.sh scry` only runs CoreCLR tests (no `net472` runtime on Linux/macOS to run Desktop tests against, so it has no `--core`/`--desktop` equivalent), while `folly.ps1 scry` runs both a CoreCLR pass and a Desktop pass by default, each writing straight to its own `-CoreClr`/`-Desktop`-suffixed `artifacts/TestResults`/`artifacts/log` directory via `FOTU_TEST_RESULTS_SUFFIX` (read by `eng/build.ps1`'s `TestUsingRunTests`, which forwards it as RunTests' `--out`/`--logs`) so the two passes' same-named result files never collide -- `--core`/`--desktop` restrict it to just one pass, and `--timeout <minutes>` overrides RunTests' whole-run `--timeout` watchdog (default 90; both wrappers now forward `-testTimeout`/`--testTimeout` to `eng/build.ps1`/`eng/build.sh`, which fall back to 90 when it's unset and Helix isn't in use). After both requested passes finish, `folly.ps1` tallies each pass's PASSED/FAILED/TIMEOUT counts from its own pass-specific log -- `runtestsCoreCLR.log`/`runtestsDesktop.log` (RunTests' `Program.WriteLogFile` names the file after `Options.TestRuntime` precisely so the two passes' otherwise-identically-named log files never collide when collected together; RunTests logs everything it prints, including its own summary table, regardless of console mode -- see `src/Tools/RunTests/ConsoleUtil.cs`) -- and prints one combined total, since RunTests' live progress table draws in the terminal's alternate screen buffer and a second pass's table re-entering that buffer right after the first pass's summary printed made it easy to lose track of in scrollback. |
| `folly.ps1 cleanse` / `folly.sh cleanse` | Deletes `artifacts/` via a single bulk `Remove-Item -Recurse -Force`/`rm -rf` (backgrounded, with a throttled byte/count progress display), after first running `dotnet build-server shutdown` so a lingering VBCSCompiler/MSBuild/Razor build server can't hold a BuildHost DLL open and block the delete on Windows. Exit code is a real success/failure contract, not just a summary: **exits 0** only if `artifacts/` no longer exists afterward (including "there was nothing to clean" and "it existed only as a file/symlink and got removed"); **exits 1** whenever anything survives -- a locked file, a permission-denied subtree, or any other leftover -- after one bulk-delete retry. In the exit-1 case the summary line distinguishes an exact survivor count from an uncertain one (`"at least N ... (some may be unreadable and not counted)"`) when the scan itself couldn't fully traverse the remaining tree. Both wrappers exit identically here; this is intentional lockstep behavior, not incidental overlap (see the `folly.sh`/`folly.ps1` parity rule in `CONVENTIONS.md`). |

Solution filters: `Roslyn.slnx` (full), `Compilers.slnf`, `Ide.slnf`, `Razor.slnf`, `FollyOfTheUnbound.slnx` (this fork's Compilers+IDE+Razor filter, excluding most of `RoslynAnalyzers`).

## Public API Tracking

- Every public-API addition/change must update the owning project's `PublicAPI.Unshipped.txt`.
- Enforced by the PublicApiAnalyzer (e.g., `RS0016` for undeclared public API). Promote entries to `PublicAPI.Shipped.txt` at release/snap time.

## Resource Strings

- Strings live in `.resx`, accessed via generated designer classes (`CSharpResources`, `FeaturesResources`, `AnalyzersResources`, …).
- After editing a `.resx`, run `dotnet msbuild <project.csproj> /t:UpdateXlf` to refresh `.xlf` files.
