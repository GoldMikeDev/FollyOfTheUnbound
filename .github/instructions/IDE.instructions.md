---
applyTo: "src/{Analyzers,CodeStyle,Features,Workspaces,EditorFeatures,VisualStudio,LanguageServer}/**/*.{cs,vb}"
---

# Roslyn IDE Development Guide

## Architecture Overview

Roslyn uses a **layered service architecture** built on MEF (Managed Extensibility Framework):

- **Workspaces** (`src/Workspaces/`): Core abstractions — `Workspace`, `Solution`, `Project`, `Document`
- **Features** (`src/Features/`): Language-agnostic IDE features (refactoring, navigation, completion)
- **Analyzers** (`src/Analyzers/`): IDE diagnostic analyzers and code fixes (IDE0xxx diagnostics)
- **CodeStyle** (`src/CodeStyle/`): Code-style analyzer packaging shared with the command-line
- **LanguageServer** (`src/LanguageServer/`): Shared LSP protocol implementation and Roslyn LSP executable (`roslyn-language-server`). This fork's `roslyn-language-server` is split into three processes: a dependency-light **thin client** (`src/LanguageServer/roslyn-language-server/`, entry point `ServerExecutable.ResolveSelf`/`ResolveLanguageServer`) that editors launch directly and that relays stdio LSP traffic over a named pipe (`LspRelay`); a short-lived **bootstrap** (`DaemonBootstrap`, re-launched copy of the thin client) that starts the long-lived daemon and exits so the daemon is orphaned out of the editor's process tree; and the **daemon** itself (`Microsoft.CodeAnalysis.LanguageServer`, the full MEF/Roslyn workspace host), shared across client connections and keyed by pipe name (`DaemonPipeName.GetPipeName`, derived from user/elevation/tool path/server arguments so incompatible clients never share a daemon). See `src/LanguageServer/roslyn-language-server/DaemonClient.cs` for the connect/launch flow. On Windows, both the thin-client→bootstrap and bootstrap→daemon launches (`ServerExecutable.Start`) also attempt to break the child out of any Windows Job Object the launching process belongs to (`Interop/Win32BreakawayProcessLauncher.cs`) — editors like VS Code run their own process tree inside a kill-on-close job, so without this the "orphaned" daemon would still die when the editor closes. It falls back to a normal `ProcessStartInfo` launch on any failure, including a job that doesn't permit breakaway. Covered by `Win32BreakawayLauncherTests` in `Microsoft.CodeAnalysis.LanguageServer.ProcessHost.UnitTests/Lifecycle/` (Windows-only, `[ConditionalFact(typeof(WindowsOnly))]`), which drives a hidden `--breakaway-self-test` CLI hook (`Interop/BreakawaySelfTest.cs`) so a real kill-on-close job's termination lands on a helper process rather than the test host. **Confirmed passing on real Windows** (`DaemonLaunchBreaksAwayFromKillOnCloseJobObject`, GoldMikeDev/roslyn#11) — the escaped child survives the job being torn down. The launcher also scopes handle inheritance to just the three stdio pipes via `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` (rather than `CreateProcess`'s all-or-nothing `bInheritHandles: true`, which was found to leak unrelated inheritable handles — e.g. named pipes/mutexes — into the breakaway-launched daemon and broke `DaemonServerLifecycleTests` handshakes); the same test's sentinel-handle assertion (`SENTINEL:Inaccessible`) proves the scoping in the source, but the fix itself is still pending a confirmed real-Windows run — treat the handle-list scoping specifically as unverified until that lands, even though the underlying breakaway mechanism is verified.
- **LanguageServer/ProjectData** (`src/LanguageServer/ProjectData/Microsoft.NET.ProjectData/`): a public
  `Microsoft.NET.ProjectData` contract (arrived via upstream sync) for caching MSBuild project-evaluation
  results across builds/worktrees so the language server can skip a full design-time build when a cached
  result is still valid. Key pieces: `ProjectDataBuildReceipt` (per-project, per-attempt completion evidence
  keyed by a SHA-256 hash of the normalized project path, plus an aggregate-completion marker),
  `ProjectDataBuildDiagnosticProtocol` and `ProjectDataBuildAttemptManifest` (structured records of a build
  attempt), and the `Donor/` subtree (`ProjectDataDonorIndex` and friends) — a repo-scoped index
  (`lscache-donor-index.json`) that lets a sibling git worktree "donate" its own cache entries to bootstrap
  a fresh worktree's cache instead of recomputing from scratch. This is a large, newly-public API surface
  (see `PublicAPI.Unshipped.txt` in that project); treat any change here as a public-API change subject to
  this repo's normal `PublicAPI.*.txt` tracking (`.github/memory/API_MAP.md`).
- **EditorFeatures** (`src/EditorFeatures/`): VS Editor integration and text manipulation
- **VisualStudio** (`src/VisualStudio/`): Visual Studio-specific implementations

### Service Resolution
```csharp
// Workspace services
var service = workspace.Services.GetRequiredService<IMyWorkspaceService>();

// Language-specific services
var csharpService = workspace.Services.GetLanguageServices(LanguageNames.CSharp)
    .GetRequiredService<IMyCSharpService>();
```

### MEF Export Patterns
```csharp
// Workspace service (language-agnostic)
[ExportWorkspaceService(typeof(IMyService)), Shared]
internal class MyService : IMyService { }

// Language service (per-language — never share across C#/VB)
[ExportLanguageService(typeof(IMyService), LanguageNames.CSharp), Shared]
internal class CSharpMyService : IMyService { }

// Constructor — always include both attributes
[ImportingConstructor]
[Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
public MyService(IDependency dependency) { }
```

## Resource & Localization

- UI strings live in `.resx` files (e.g., `AnalyzersResources.resx`, `FeaturesResources.resx`, `WorkspacesResources.resx`)
- Reference via generated designer class: `FeaturesResources.Some_string`
- For localizable strings: `new LocalizableResourceString(nameof(FeaturesResources.Some_string), FeaturesResources.ResourceManager, typeof(FeaturesResources))`
- After modifying `.resx` files, run `dotnet msbuild <path to csproj> /t:UpdateXlf` to update `.xlf` localization files

## LSP URI Parsing (`src/LanguageServer/Protocol/Protocol/ParsedUri.cs`)

`ParsedUri` (arrived via upstream sync) is the LSP layer's own URI parser/formatter, replacing
`System.Uri` for LSP document/file identity: it implements vscode-uri's parsing and encoding
semantics (including `ParsedUri.File` for filesystem paths and `ParsedUri.Parse` for LSP-supplied
URI strings) rather than .NET's own `Uri` rules, since editors send URIs following the
JavaScript/vscode-uri conventions the LSP spec was written against. `DocumentUri` (the LSP wire
type) is constructed from a `ParsedUri` — prefer that constructor over the `System.Uri`-based one
(marked obsolete, tracked at https://github.com/dotnet/roslyn/issues/84785) when producing a
`DocumentUri` from a string that isn't already a validated `System.Uri`. Percent-decoding
(`DecodeURIComponentGraceful`) degrades gracefully on malformed escapes by peeling one `%XX` triplet
at a time and retrying the remainder iteratively (not recursively — a long run of invalid escapes
must not grow the call stack).

## Language Server Project Loading (`src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/HostWorkspace/`)

Design-time build and workspace-project lifecycle for the language server daemon is split across
partial-class files, one concept per file:

- **`LanguageServerProjectLoader.cs`** — the abstract base loader: queues and batches project
  (re)loads (`_projectsToReload`), runs design-time builds via `BuildHostProcessManager`, tracks
  loaded projects in `_loadedProjects` (`Dictionary<string, LoadedProject>`), and owns automatic
  NuGet restore.
- **`LanguageServerProjectLoader.ProjectToLoad.cs`** — the `ProjectToLoad` record (path + optional
  `WorkDoneProgressTracker`) queued onto `_projectsToReload`.
- **`LanguageServerProjectLoader.WorkDoneProgressTracker.cs`** — `WorkDoneProgressTracker`, which
  coalesces LSP `WorkDoneProgress` percentage updates from parallel callers and reports 100% on
  disposal.
- **`LoadedProject.cs`** — one instance per loaded project *file* (not per target framework); owns
  the project's file watches and holds a `List<Target>` — one `Target` per target framework when the
  project is multi-targeted.
- **`LoadedProject.Target.cs`** — the nested `Target` type: one loaded `Microsoft.CodeAnalysis.Project`
  registered in the workspace, its own asset-file watcher and options processor. `Target.Dispose()`
  releases those first, then calls `RemoveFromWorkspace()` last — so a caller disposing multiple
  targets must not let one `RemoveFromWorkspace()` failure (e.g. the owning `Workspace` already torn
  down) skip disposing the rest; `LoadedProject.DisposeAsync()` catches per-target for this reason.

## Analyzers & Code Fixes (IDE0xxx)

- IDE code-style analyzers inherit from `AbstractBuiltInCodeStyleDiagnosticAnalyzer` — not raw `DiagnosticAnalyzer`
- Always provide a `FixAllProvider` for code fixes (typically `WellKnownFixAllProviders.BatchFixer`)
- Diagnostic ID constants live in `src/Analyzers/Core/Analyzers/IDEDiagnosticIds.cs`

## Out-of-Process (OOP) Services

- ServiceHub components live under `src/Workspaces/Remote/` and have special deployment considerations for .NET Core vs .NET Framework — keep both targets in mind when changing remote services

## Key Development Patterns

### TestAccessor Pattern
Expose internal state to tests without making it public:
```csharp
internal class ProductionClass
{
    private int _privateField;

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor
    {
        private readonly ProductionClass _instance;
        internal TestAccessor(ProductionClass instance) => _instance = instance;
        internal ref int PrivateField => ref _instance._privateField;
    }
}
```
**TestAccessor calls are forbidden in production code** — enforced by analyzer RS0043.

### SyntaxGenerator (Language-Agnostic Code Generation)
Use `SyntaxGenerator` to generate code without language-specific knowledge:
```csharp
var generator = SyntaxGenerator.GetGenerator(document);
var methodDecl = generator.MethodDeclaration("MyMethod", ...);
```

## Coding Conventions

- **Private fields**: `_camelCase`
- **Naming**: MEF exports match interface names without "I" prefix
- **Null checks**: Use `Contract.ThrowIfNull()` instead of manual null checks
- **Immutability**: All `Document`, `Solution`, `Project` instances are immutable — use `With*` methods
- **Cancellation**: Always thread `CancellationToken` through async operations
- **Performance**: Avoid LINQ in hot paths, prefer `for` loops or `.AsSpan()`, use `ObjectPool<T>`
- **LanguageServer request context**: Handlers should use the asynchronous `RequestContext.Get*Async` methods for workspace, solution, and document access. Obsolete synchronous members remain only for compatibility with existing external-access consumers and forward to the asynchronous accessors.

## Adding IDE Support for a New Statement/Expression SyntaxKind

When the compiler side of a new language construct (parser/binder/lowering) already exists but the
IDE doesn't recognize it yet, the gap is spread across several independent, per-`SyntaxKind`
registration points rather than one place. Checklist, in the order it's easiest to verify each:

1. **Classification** (`src/Workspaces/CSharp/Portable/Classification/ClassificationHelpers.cs`) —
   real/contextual keywords are colored automatically via `SyntaxFacts.IsKeywordKind`; a keyword
   that's parsed as a bare `IdentifierToken` (matched by text, not `SyntaxKind`) needs a case added
   to `IsActualContextualKeyword`. Add the statement's `SyntaxKind` to `IsControlStatementKind` for
   `ControlKeyword` coloring parity with existing control-flow statements.
2. **Keyword completion** (`src/Features/CSharp/Portable/Completion/KeywordRecommenders/`) — one
   recommender class per keyword, registered in `KeywordCompletionProvider.cs`'s alphabetical array.
   `AbstractSyntacticSingleKeywordRecommender` requires a real `SyntaxKind` (used for
   `SyntaxFacts.GetText`); a text-matched keyword with no `SyntaxKind` needs a recommender written
   directly against `IKeywordRecommender<CSharpSyntaxContext>` instead (see `MutateKeywordRecommender.cs`).
3. **Formatting** (`src/Workspaces/SharedUtilitiesAndExtensions/Compiler/CSharp/Formatting/Rules/`) —
   indentation is generic (Block-parent based, needs nothing), but same-line keyword placement
   (`} while`, `} catch`, `} else`) and brace/paren spacing are hardcoded per token-kind/parent-kind
   in `TokenBasedFormattingRule.cs`, `NewLineUserSettingFormattingRule.cs`, `SpacingFormattingRule.cs`.
4. **Outlining** (`src/Features/CSharp/Portable/Structure/Providers/BlockSyntaxStructureProvider.cs`)
   — one shared provider keyed off a block's parent `SyntaxKind`; add cases to `GetType` and, for
   constructs with multiple related blocks (if/else-if/else, try/catch/finally), a dedicated branch
   in `CollectBlockSpans` plus a `GetEnd` override so the outer region extends through the whole chain.
5. **Brace matching** and **generic close-brace Quick Info**
   (`CSharpSyntacticQuickInfoProvider.BuildQuickInfoCloseBrace`) — both fully generic/token-based;
   new constructs get these for free as long as they reuse ordinary `{`/`}`/`(`/`)` tokens.
6. **Keyword highlighting** (`src/Features/CSharp/Portable/Highlighting/KeywordHighlighters/`) — one
   MEF-exported highlighter per concrete node type (`AbstractKeywordHighlighter<TNode>`); a new
   statement kind gets zero highlighting until a case/class is added, even if it's conceptually
   similar to an existing construct (e.g. `do`/`until` needed its own case in `LoopHighlighter`
   alongside `do`/`while`, since it's a distinct node type).
7. **Breakpoint spans** (`src/Features/CSharp/Portable/EditAndContinue/BreakpointSpans.cs`) — an
   exhaustive switch (`TryCreateSpanForNode` for non-statement nodes, `TryCreateSpanForStatement`
   for `StatementSyntax`); an unhandled kind falls through to a whole-statement span (degraded, not
   broken) — add explicit cases mirroring the closest existing construct.

**Don't forget shared low-level helpers with their own exhaustive switches** — e.g.
`SyntaxNodeExtensions.IsContinuableConstruct`/`IsBreakableConstruct` (used by `break`/`continue`
keyword completion *and* `LoopHighlighter`) has its own `SyntaxKind` list separate from all of the
above; a new loop-like statement needs a case there too or `break`/`continue` silently won't be
recommended/highlighted inside it. See `known-issues/ide.md`.

## Common Gotchas

- **ImportingConstructor must be marked `[Obsolete]`** with `MefConstruction.ImportingConstructorMessage`
- **Language services must be exported with a specific language name** — don't use generic exports for both C#/VB
- **Workspace changes must use immutable updates** — `Workspace.SetCurrentSolution()`
- **`[VisualStudioContribution]` metadata properties can't use collection expressions** — properties like
  `CommandConfiguration.Placements` and `CommandGroupConfiguration.Children`
  (`src/VisualStudio/CSharp/Impl/**`) are evaluated by the `Microsoft.VisualStudio.Extensibility` SDK's
  compile-time interpreter, which doesn't support `CollectionExpressionSyntax` (`[...]`) and fails the
  build with `CEE0001`. Use `new[] { ... }` array initializers instead.
