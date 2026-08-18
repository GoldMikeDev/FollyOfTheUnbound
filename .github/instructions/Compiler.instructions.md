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
- `src/Tools/` - Compiler tooling (BuildBoss, format tools, analyzers) and benchmark harnesses; `IdeCoreBenchmarks/BenchmarkDotNet.Directory.Packages.props` supplies the package policy copied beside generated BDN runners. `src/Tools/RunTests/` is the custom test runner behind `eng/build.ps1`/`eng/build.sh`'s `-test*`/`--test*` flags (and this fork's `folly.ps1`/`folly.sh scry`): partitions test assemblies into work items and runs them in parallel via `vstest.console.dll` -- capped at `Environment.ProcessorCount - 1` concurrent work items (not full oversubscription), deliberately leaving one processor free for the rest of the system, including the console itself, so the live progress table's redraws stay responsive instead of getting starved by CPU-saturated test processes; `Sequential` mode still forces exactly 1, for the open integration tests that perform actual UI operations and can't run two at once without conflicting -- and detects test-host crashes/hangs from vstest's `/Blame` dump output. Each work item gets its own isolated `/ResultsDirectory` (`ProcessTestExecutor.GetDumpResultsDirectory`, keyed by partition index) so `CheckForCrashes` can only ever find dumps that actually belong to it -- the shared artifacts directory and a directory-wide/timestamp-only scan can't rule out attributing a concurrently-running work item's dump to an unrelated one. `TestRunner`'s run loop drives `LiveTestProgressDisplay`, a table drawn into the terminal's alternate screen buffer (like `vim`/`htop`) shown only on a real, non-redirected interactive terminal (`Console.IsOutputRedirected`). It tracks every work item, sorted alphabetically, regardless of status (queued/passed rows included, not just running/failed/timed-out), with the title/column-header/separator lines frozen and only the row list underneath drawing a scrolling *window* of however many rows the terminal actually has (`ComputeScrollStart`) -- there is no way to show more rows at once than the window physically has, on any terminal, and the alternate screen (rather than writing an oversized frame into the normal buffer, which breaks on Unix/ConPTY-style terminals once it scrolls) is what makes each redraw reliably repaint in place everywhere. By default that window auto-follows whichever row is running/queued, but it's also manually scrollable: the up/down arrow keys always work (`ApplyNavigationKey` -- deliberately just up/down, no PageUp/PageDown/Home/End), and so does the mouse wheel wherever `_supportsMouseWheel` is true -- unconditionally on non-Windows (assumed xterm-compatible; the terminal is asked for SGR mouse reporting via `EnableMouseTracking`, and raw escape bytes are parsed by hand in `PollKeyboardAndMouseInputRaw`, since `Console.ReadKey`'s own decoding only ever matches its fixed table of *key* sequences and silently discards anything else, like a mouse report). Every actual byte comes off a single dedicated background thread (`_rawInputReaderThread`) that owns the only call to `Console.In.Read()` for the display's entire lifetime and feeds a lock-free queue everything else drains from (`ReadRawByte`) -- an earlier version instead spawned a fresh `Task.Run(Console.In.Read)` per byte bounded by a short timeout, and any read that didn't finish in time was abandoned but kept blocking forever inside `Console.In`'s internal lock, leaking a thread-pool thread per timeout and, over a long run with any scrolling, starving the same pool the redraw loop depends on. The reader thread is started lazily on first use and deliberately never stopped (`Console.In` is `TextReader.Synchronized`-wrapped, so closing it from `Complete`/`Suspend` would itself block on the same monitor the reader's pending `Read()` call holds, turning "end the display" into "hang until another key arrives"); it's left to sit blocked in its last read forever instead, which is safe only because it's a single background thread that can never keep the process itself from exiting. On Windows, only once `TryDetectWindowsConsoleInputSupport` has confirmed both a `WT_SESSION` environment variable (Windows Terminal specifically -- `ENABLE_VIRTUAL_TERMINAL_INPUT` + `ENABLE_MOUSE_INPUT` is only known to translate mouse/wheel into VT sequences on stdin there, not under legacy conhost, which keeps delivering native `MOUSE_EVENT_RECORD`s the stdin-byte parser would never see) and that `SetConsoleMode(ENABLE_VIRTUAL_TERMINAL_INPUT | ENABLE_MOUSE_INPUT)` actually takes effect on that console (via CsWin32-generated bindings declared in `src/Tools/RunTests/NativeMethods.txt`) -- both paths funnel into the same raw-escape parser once enabled. Esc returns control to auto-follow. Mouse/console-mode capture is never turned on when stdin is redirected. That's only ever true outside CI when `eng/build.ps1` is passed `-testInteractiveConsole` (which `folly.ps1 scry` does) -- PowerShell's own object pipeline (e.g. `| Tee-Object`) doesn't set `[Console]::IsOutputRedirected`, so whether to let `RunTests.exe` inherit the real console can't be auto-detected and is opt-in instead, defaulting to today's piped/relayed behavior. `TestResultDisplay` holds the column widths/formatting (status text, fixed-width elapsed, name truncation preserving the trailing `_<partition>` suffix) shared between the live table and `TestRunner`'s own final summary table, so the two stay consistent; covered by `src/Tools/RunTests.UnitTests/`.

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
