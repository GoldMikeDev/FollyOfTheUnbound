---
coverage: Top-level src/ overview; per-layer directory detail lives in the instruction files
---

# File Map

Source lives under `src/`. Build orchestration is in `eng/` and root scripts; docs in `docs/`. A pervasive convention: a product project `X` has its tests in a sibling `XTest` / `*.UnitTests` project (e.g., `Workspaces/Core` ↔ `Workspaces/CoreTest`).

This file is a **top-level map only**. For per-area directory detail, read the matching path-scoped instruction file:
- Compiler areas → `.github/instructions/Compiler.instructions.md`
- IDE areas → `.github/instructions/IDE.instructions.md`
- Razor → `.github/instructions/Razor.instructions.md`

## `src/` Areas

| Area | Layer | Purpose |
|------|-------|---------|
| `Compilers/` | compiler | C#/VB compilers (`Core`, including MSBuild tasks; `CSharp`; `VisualBasic`; `Server`). |
| `Dependencies/` | compiler | High-performance pooled collections & threading. |
| `ExpressionEvaluator/` | compiler | Debugger expression evaluator. |
| `Tools/` | compiler | Compiler and infrastructure tooling (BuildBoss, format tools, `dotnet-roslyn-tools`) and benchmark harnesses. Includes `RunTests/`, the custom parallel test runner behind `eng/build.ps1`/`eng/build.sh` and this fork's `folly.ps1`/`folly.sh scry`, with a live in-place progress table (frozen header, keyboard/mouse-wheel-scrollable body) on interactive terminals -- Windows mouse-wheel support uses CsWin32-generated console-mode bindings declared in `RunTests/NativeMethods.txt` (tested by the sibling `RunTests.UnitTests/`). `TestRunner`'s concurrency (`Environment.ProcessorCount - 1`) also excludes logical processors Windows currently reports as parked (`ProcessorTopology.GetParkedLogicalProcessorCount`, via `GetSystemCpuSetInformation` and the same CsWin32/`NativeMethods.txt` pattern) -- on a hybrid CPU this can mean a whole efficiency-class tier stays parked essentially permanently (e.g. Arrow Lake-H's "Low Power Island" E-cores), while the rest of the tiers are scheduled normally; counting a parked core toward concurrency can schedule a work item onto it and stall it. |
| `Workspaces/` | ide | Solution/Project/Document model, MSBuild loading, Remote (OOP). |
| `Features/`, `EditorFeatures/` | ide | IDE feature logic and editor integration. |
| `Analyzers/`, `CodeStyle/` | ide | IDE0xxx code-style analyzers & fixes. |
| `LanguageServer/` | ide | LSP server; this fork's `roslyn-language-server` thin client/bootstrap/daemon split (incl. Windows Job Object breakaway) and its `*.ProcessHost.UnitTests`. Also hosts `LanguageServer/ProjectData/Microsoft.NET.ProjectData/`, a public MSBuild project-evaluation caching contract (build receipts, plus a `Donor/` cross-worktree cache-sharing index) — see `.github/instructions/IDE.instructions.md`. |
| `VisualStudio/` | ide | VS language services & UI. |
| `Razor/src/` | razor | Razor compiler + tooling (own sub-tree layout). |
| `Scripting/`, `Interactive/` | — | C#/VB scripting engine and REPL. |
| `RoslynAnalyzers/` | — | Shipping `Microsoft.CodeAnalysis.*` analyzer packages. |
| `RoslynSdk/` | — | Analyzer testing libraries, Visual Studio SDK templates, Syntax Visualizer, and Roslyn SDK tests; excluded from source-only builds by its root `Directory.Build.props`. |
| `Deployment/`, `NuGet/`, `Setup/`, `Test/` | — | Deployment/VSIX, packaging, shared test infrastructure. |

## Non-source Roots

| Path | Status | Purpose |
|------|--------|---------|
| `eng/` | Config / Generated | Arcade build engineering. Pipeline definitions and templates live in `eng/pipelines/`; `eng/common/` is DARC-synced and must not be hand-edited. `eng/generate-compiler-code.cs` regenerates compiler code. |
| `docs/` | Active | Contributor & design docs. New docs use kebab-case filenames in the right subdirectory. |
| Root | Config | Entry points & solution filters: `build.sh`/`Build.cmd`, `test.sh`/`Test.cmd`, `Roslyn.slnx`, `Compilers.slnf`, `Ide.slnf`, `Razor.slnf`, `FollyOfTheUnbound.slnx` (Compilers+IDE+Razor, deliberately excluding most of `src/RoslynAnalyzers` — see its `/Analyzers/` folder comment), `global.json`, `Directory.*.props/targets`, `Directory.Packages.props`. |
| `folly.ps1` / `folly.sh` | Active | This fork's own build/pack/test wrapper for `FollyOfTheUnbound.slnx` (`attune`/`weave`/`bind`/`scry`, mapping to Arcade's `--restore`/`--build`/`--pack`/test) — Windows and Linux/macOS respectively. Not fully in sync: `scry` runs both Core and Framework tests on Windows (`folly.ps1`, restrictable to just one via `--core`/`--framework`) but only Core on Linux/macOS (`folly.sh`, since there's no `net472` runtime there, so no `--core`/`--framework` there either). See `API_MAP.md`'s Build & Test Entry Points table for the action mapping. |
| `scripts/` | Active | Manually-invoked scripts not used by the build process (per its own `README.md`). Includes `test-folly-cleanse.sh` (standalone manual test harness for `folly.sh cleanse`), `test-folly-cleanse.ps1` (the same, for `folly.ps1 cleanse`'s bulk-delete path), `test-folly-scry-args.ps1` (standalone regression harness for `folly.ps1 scry`'s argument parsing and unified test summary), and `test-folly-scry-args.sh` (the bash counterpart, for `folly.sh scry`'s argument parsing) — see `TESTING_STRATEGY.md`. |
| `.github/workflows/` | Active | GitHub Actions. `codeql.yml` runs CodeQL with an advanced/manual build (`FollyOfTheUnbound.slnx`) instead of default autobuild, which can't handle this repo's SDK bootstrapping. |
