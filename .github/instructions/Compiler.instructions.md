---
applyTo: "src/{Compilers,Dependencies,ExpressionEvaluator,Tools}/**/*.{cs,vb}"
---

# Roslyn Compiler Instructions for AI Coding Agents

## Architecture Overview

Roslyn follows a **layered compiler architecture**:
- **Lexer → Parser → Syntax Trees → Semantic Analysis → Lowering/Rewriting → Symbol Tables → Emit**
- Core abstraction: `Compilation` is immutable and reusable. Create new compilations via `AddSyntaxTrees()`, `RemoveSyntaxTrees()`, `ReplaceSyntaxTree()` for incremental changes
- **Internal vs Public APIs**: Use `InternalSyntax` namespace for performance-critical parsing; `Microsoft.CodeAnalysis` for public consumption

### Key Directories
- `src/Compilers/Core/Portable/` - Language-agnostic compiler infrastructure
- `src/Compilers/CSharp/Portable/` - C# compiler implementation  
- `src/Compilers/VisualBasic/Portable/` - VB compiler implementation
- `src/Compilers/Server/` - `VBCSCompiler` build server
- `src/Dependencies/` - High-performance collections (`PooledObjects`, `Threading`)
- `src/ExpressionEvaluator/` - Debugger expression evaluation (uses special `LexerMode.DebuggerSyntax`)
- `src/Tools/` - Compiler and infrastructure tooling (BuildBoss, `dotnet-roslyn-tools`, format tools, analyzers) and benchmark harnesses; `IdeCoreBenchmarks/BenchmarkDotNet.Directory.Packages.props` supplies the package policy copied beside generated BDN runners. `src/Tools/RunTests/` is the custom test runner behind `eng/build.ps1`/`eng/build.sh`'s `-test*`/`--test*` flags (and this fork's `folly.ps1`/`folly.sh scry`): partitions test assemblies into work items and runs them in parallel via `vstest.console.dll` -- capped at `Environment.ProcessorCount - 1` concurrent work items (not full oversubscription), deliberately leaving one processor free for the rest of the system, including the console itself, so the live progress table's redraws stay responsive instead of getting starved by CPU-saturated test processes -- on Windows that count also excludes whatever `ProcessorTopology.GetParkedLogicalProcessorCount` (`GetSystemCpuSetInformation`'s live per-core `Parked` bit -- that API always returns every CPU set on the whole system no matter which process handle is passed; each entry's `AllocatedToTargetProcess` flag is only true for a CPU set the process was *explicitly* restricted to (`SetProcessDefaultCpuSets`, a CPU-set-aware job object limit, etc), so an ordinary unrestricted process has it false on every entry -- the parked count is filtered to only `AllocatedToTargetProcess` entries when at least one entry has that flag set at all (an explicit CPU-set restriction is in play); with none flagged, every parked entry counts, matching how CoreCLR's own PAL interprets the same API. That flag says nothing about a plain CPU-affinity-mask restriction (`Process.ProcessorAffinity`, a non-CPU-set-aware job object CPU limit) though, which `Environment.ProcessorCount` also respects, so `GetProcessAffinityMask`'s mask is cross-referenced too (matched against each entry's `LogicalProcessorIndex` bit within the entry's own group; `GetProcessAffinityMask` only succeeds for a process confined to a single group, and `GetProcessGroupAffinity` identifies which one so the mask isn't misapplied against the wrong group, left unapplied if that group can't be determined) -- same CsWin32/`NativeMethods.txt` pattern) currently reports parked (a hybrid CPU can keep one efficiency-class tier, e.g. Arrow Lake-H's "Low Power Island" E-cores, parked essentially permanently), re-sampled every loop tick and only ever raised (never lowered, so an idle-machine snapshot taken before this run's own load ramps up self-corrects instead of permanently under-provisioning, and an already-running work item is never starved by a transient re-parking blip) -- the CI/non-live fallback path (see below) is woken on the same timer for this same reason, its status line suppressed on a tick where nothing else changed so this doesn't reintroduce log spam; `Sequential` mode still forces exactly 1, for the open integration tests that perform actual UI operations and can't run two at once without conflicting -- and detects test-host crashes/hangs from vstest's `/Blame` dump output. Each work item gets its own isolated `/ResultsDirectory` (`ProcessTestExecutor.GetDumpResultsDirectory`, keyed by partition index) so `CheckForCrashes` can only ever find dumps that actually belong to it -- the shared artifacts directory and a directory-wide/timestamp-only scan can't rule out attributing a concurrently-running work item's dump to an unrelated one. `TestRunner`'s run loop drives `LiveTestProgressDisplay`, a table drawn into the terminal's alternate screen buffer (like `vim`/`htop`) shown only on a real, non-redirected interactive terminal (`Console.IsOutputRedirected`). It tracks every work item, sorted alphabetically, regardless of status (queued/passed rows included, not just running/failed/timed-out), with the title/column-header/separator lines frozen and only the row list underneath drawing a scrolling *window* of however many rows the terminal actually has (`ComputeScrollStart`) -- there is no way to show more rows at once than the window physically has, on any terminal, and the alternate screen (rather than writing an oversized frame into the normal buffer, which breaks on Unix/ConPTY-style terminals once it scrolls) is what makes each redraw reliably repaint in place everywhere. By default that window auto-follows whichever row is running/queued, but it's also manually scrollable: the up/down arrow keys always work (`ApplyNavigationKey` -- deliberately just up/down, no PageUp/PageDown/Home/End), and so does the mouse wheel wherever `_supportsMouseWheel` is true -- unconditionally on non-Windows (assumed xterm-compatible; the terminal is asked for SGR mouse reporting via `EnableMouseTracking`, and raw escape bytes are parsed by hand in `PollKeyboardAndMouseInputRaw`, since `Console.ReadKey`'s own decoding only ever matches its fixed table of *key* sequences and silently discards anything else, like a mouse report). Every actual byte comes off a single dedicated background thread (`_rawInputReaderThread`) that owns the only call to `Console.In.Read()` for the display's entire lifetime and feeds a lock-free queue everything else drains from (`ReadRawByte`) -- an earlier version instead spawned a fresh `Task.Run(Console.In.Read)` per byte bounded by a short timeout, and any read that didn't finish in time was abandoned but kept blocking forever inside `Console.In`'s internal lock, leaking a thread-pool thread per timeout and, over a long run with any scrolling, starving the same pool the redraw loop depends on. The reader thread is started lazily on first use and deliberately never stopped (`Console.In` is `TextReader.Synchronized`-wrapped, so closing it from `Complete`/`Suspend` would itself block on the same monitor the reader's pending `Read()` call holds, turning "end the display" into "hang until another key arrives"); it's left to sit blocked in its last read forever instead, which is safe only because it's a single background thread that can never keep the process itself from exiting. On Windows, only once `TryDetectWindowsConsoleInputSupport` has confirmed both a `WT_SESSION` environment variable (Windows Terminal specifically -- `ENABLE_VIRTUAL_TERMINAL_INPUT` + `ENABLE_MOUSE_INPUT` is only known to translate mouse/wheel into VT sequences on stdin there, not under legacy conhost, which keeps delivering native `MOUSE_EVENT_RECORD`s the stdin-byte parser would never see) and that `SetConsoleMode(ENABLE_VIRTUAL_TERMINAL_INPUT | ENABLE_MOUSE_INPUT)` actually takes effect on that console (via CsWin32-generated bindings declared in `src/Tools/RunTests/NativeMethods.txt`) -- both paths funnel into the same raw-escape parser once enabled. Esc returns control to auto-follow. Mouse/console-mode capture is never turned on when stdin is redirected. That's only ever true outside CI when `eng/build.ps1` is passed `-testInteractiveConsole`, which `folly.ps1 scry` now passes unconditionally for every `--core`/`--framework` pass it runs, single or both together -- `Console.IsOutputRedirected` is a one-way flag checked once up front, so piping a pass's output through the pipeline at all (even just to relay it back out) permanently costs that pass its live table with no way to recover it afterward. Combining both passes' *final* tables into one view without that cost needed a separate, RunTests-side mechanism instead of any output-piping trick: `Options.SuppressConsoleSummary` (`-testSuppressConsoleSummary` on `eng/build.ps1`, `--suppressConsoleSummary` on `RunTests` itself) makes `TestRunner.Print`'s own PASSED/FAILED/TIMEOUT table log-only, skipping just its `ConsoleUtil` calls for that one table while leaving the live table, per-failure diagnostics, and the log file itself untouched -- `folly.ps1` passes this only when both passes run together, then reads both passes' tables back from their log files afterward and prints them together in one combined block, immediately followed by a compact combined numeric recap -- see `folly.ps1`'s own comments. `TestResultDisplay` holds the column widths/formatting (status text, fixed-width elapsed, name truncation preserving the trailing `_<partition>` suffix) shared between the live table and `TestRunner`'s own final summary table, so the two stay consistent. Every row (live table, `TestRunner.Print`'s own table, and `folly.ps1`/`folly.sh`'s combined-both-legs block that reads both tables back from each leg's log file) is colored green/yellow/red for passed/timeout/failed via raw SGR escapes, normal color while queued/running. The live table also shows a "Previous" column -- how long each work item took the last time it *passed* on this machine, from `LocalTestTimingHistory`: a per-machine, gitignored `<repo root>/.test-timings.json` (deliberately outside `artifacts/`, which `cleanse` deletes; `<repo root>` is found by walking up from this process's own binary location for the enclosing `artifacts` directory -- the same technique `Options.TryGetArtifactsPath` uses by default -- rather than assumed to be `--artifactspath`'s parent, since that switch accepts an arbitrary directory outside the checkout entirely), keyed by assembly/TFM/configuration/architecture (`LocalTestTimingHistory.GetKey`) so a Debug and a Release run of the same assembly never overwrite each other's baseline. Recorded by `TestRunner` itself in its own completion handling (`GetNameParts` is `internal`, not `private`, specifically so `TestRunner` can derive the same key `LiveTestProgressDisplay` used for its row) -- deliberately not gated on the live table existing, so a redirected-output/CI run's real, unfiltered timings still update the baseline the next interactive run reads. Skipped entirely for a `--testFilter` run (its elapsed time covers only a subset of the assembly, not a real full-run baseline). Each write re-reads the file and merges into its current on-disk content immediately before saving, rather than blindly overwriting with this instance's own in-memory snapshot, so two `scry` processes running concurrently don't silently erase each other's keys. Covered by `src/Tools/RunTests.UnitTests/`.

### Essential Files for Context
- `src/Compilers/CSharp/Portable/Errors/ErrorCode.cs` - All C# compiler error codes
- `src/Compilers/CSharp/Portable/Errors/MessageID.cs` - Language feature version gating
- `src/Compilers/CSharp/Portable/LanguageVersion.cs` - Public language-version enum and parsing/mapping logic
- `src/Compilers/CSharp/Portable/Syntax/Syntax.xml` - Syntax tree node definitions (generated code source)
- `src/Compilers/CSharp/Portable/BoundTree/BoundNodes.xml` - Bound tree node definitions (generated code source)
- `docs/wiki/Roslyn-Overview.md` - Architecture deep-dive

## Code Generation

Several core data structures are generated from XML definitions — **never edit the generated `.cs` or `.vb` files directly**:
- **Syntax trees**: `src/Compilers/CSharp/Portable/Syntax/Syntax.xml`
- **Bound trees**: `src/Compilers/CSharp/Portable/BoundTree/BoundNodes.xml`
- **IOperation API**: `src/Compilers/Core/Portable/Operations/OperationInterfaces.xml` (shared between C# and VB — generates `OperationKind.Generated.cs` and `Operations.Generated.cs`, both public API tracked via `PublicAPI.Unshipped.txt`)
- After modifying these XML files, regenerate and build:
  ```bash
  dotnet run --file eng/generate-compiler-code.cs
  dotnet build src/Compilers/{CSharp,VisualBasic}/Portable # choose the project matching the C# or VB syntax you changed
  ```

## Conventions

- **MEF is not used in the compiler layer.** `ExportLanguageService` / `ImportingConstructor` and the IDE service model are IDE-layer concepts — ignore them here.
- **Null checks**: validate internal-API preconditions with `Debug.Assert(...)` (a violated internal precondition may NRE in release); validate public APIs with explicit null checking when appropriate, throwing a dedicated exception with a localized string.
- **Immutability** is via `Compilation` (`AddSyntaxTrees`/`RemoveSyntaxTrees`/`ReplaceSyntaxTree`), not the workspace `Document`/`Solution` model.

## Essential Patterns

### Memory Management
- **Avoid LINQ in hot paths** - use manual enumeration or `struct` enumerators
- **Avoid `foreach` over collections without struct enumerators** 
- **Use object pools extensively** - see patterns in `src/Dependencies/PooledObjects/`
- **Prefer `Debug.Assert()` over exceptions** for internal validation

## Build & Test Workflows

### Essential Build Commands

```powershell
# Full build (use VS Code tasks when available)
./build.sh

# Build specific components  
dotnet build Compilers.slnf                    # Compiler-only build
dotnet build src/Compilers/CSharp/csc/AnyCpu/  # C# compiler

# Generate compiler code after changes
dotnet run --file eng/generate-compiler-code.cs
```

## Debugger Integration

**Expression Evaluator** uses special parsing modes:
- `LexerMode.DebuggerSyntax` for expression evaluation
- `IsInFieldKeywordContext` flag for context-aware parsing
- `ConsumeFullText` parameter for complete expression parsing

## MSBuild Integration

Compiler tasks are in `src/Compilers/Core/MSBuildTask/`:
- `Csc.cs` - C# compiler task
- `Vbc.cs` - VB compiler task  
- `ManagedCompiler.cs` - Base compiler task functionality

## Performance Considerations

1. **Lexer/Parser optimizations**: Use `InternalSyntax` types for performance-critical code
2. **Immutable data structures**: Roslyn heavily uses immutable collections and copy-on-write semantics
3. **Caching**: `Compilation` objects cache semantic information - reuse when possible
4. **Threading**: Most compiler operations are thread-safe through immutability

## Symbol Resolution

Navigate the symbol hierarchy:
```cs
var compilation = CreateCompilation(source);
var globalNamespace = compilation.GlobalNamespace;
var typeSymbol = globalNamespace.GetTypeMembers("MyClass").Single();
var methodSymbol = typeSymbol.GetMembers("MyMethod").Single();
```

Symbol equality is complex due to generics and substitution - always test with multiple generic scenarios.
