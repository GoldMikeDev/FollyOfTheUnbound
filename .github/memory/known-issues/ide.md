---
coverage: IDE-layer (src/{Analyzers,CodeStyle,Features,Workspaces,EditorFeatures,VisualStudio,LanguageServer}) known issues, quirks & workarounds
---

# IDE — Known Issues

Layer-specific quirks for the IDE/Workspaces stack. Load when working under
`src/{Analyzers,CodeStyle,Features,Workspaces,EditorFeatures,VisualStudio,LanguageServer}`.
Cross-cutting issues live in `.github/memory/KNOWN_ISSUES.md`.

## MEF composition failures surface as test failures

**Affected area:** MEF-dependent IDE/Workspaces tests
**Description:** A missing/incorrect MEF export attribute often manifests as an
unrelated-looking test failure rather than a clear composition error.
**Workaround:** When IDE tests fail unexpectedly, check the export attributes
first (`[ExportLanguageService]`/`[ExportWorkspaceService]`, `[Shared]`,
`[ImportingConstructor]` + `[Obsolete(MefConstruction.ImportingConstructorMessage)]`).

## Daemon-mode `roslyn-language-server` per-connection isolation: telemetry only, by design

**Affected area:** `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/` daemon mode (`--daemon`),
`src/LanguageServer/roslyn-language-server/`
**Description:** The daemon builds exactly one MEF `ExportProvider` and, since every connection shares that
one composition (no `Microsoft.VisualStudio.Composition` version currently ships a scoped/child-`ExportProvider`
API — confirmed via reflection over the actual restored 18.9.15 assembly, ahead of the entire public
nuget.org release history), everything genuinely per-connection is layered on top via an ambient-token
primitive (`AmbientConnectionToken`/`DaemonConnectionContext`), not a real MEF scope. This is now largely
complete: global log routing (`GlobalLogMessageLogger`), `IGlobalOptionService` reads/writes reachable from
LSP request handling (`ConnectionScopedOptionOverrides`/`GetConnectionScopedOption`, including propagation
into `Solution.FallbackAnalyzerOptions` via `SolutionAnalyzerConfigOptionsUpdater.ApplyChangedOptionsIfRelevant`),
and per-connection `ExtensionLogDirectory`/`SourceGeneratorExecutionPreference` (via a `ConnectionHandshake`
a connecting client sends before its stream becomes the LSP channel, no longer baked into the daemon pipe
key) are all isolated per connection. **Still open, by design, not deferred:** `TelemetryLevel`/`SessionId`
still come from whichever client happened to launch the daemon — `RoslynLogger` is a hard process-wide
singleton with no way for two instances (one per connection) to coexist without a telemetry-plumbing
redesign that's out of scope, and telemetry answers "how is this tool used in aggregate," not "what did this
workspace do," so misattributing it across a shared daemon's connections isn't a correctness/privacy problem
the way the option/log gaps were.
**Still open, genuinely unresolved (not by design):** two Razor cohosting MEF singletons have the same
"last connection to initialize wins" shape as the original `IGlobalOptionService` gap, but haven't been
fixed: `RazorClientServerManagerProvider` (`src/Razor/.../Services/RazorClientServerManagerProvider.cs`)
caches one `IClientLanguageServerManager` overwritten by whichever connection's Razor cohost startup ran
last, so `HtmlDocumentPublisher`/`HtmlRequestInvoker` can send one connection's generated HTML or forward
its HTML requests to a *different* connection's editor — a cross-client content-disclosure bug, not just
wrong behavior. `CohostConfigurationChangedService`'s shared `IClientSettingsManager` has the same shape for
Razor settings (e.g. `razor.advanced.show_all_c_sharp_code_actions`), read by `CSharpCodeActionProvider`
regardless of which connection is being served. Root cause is identical to the option/log gaps above
(`Microsoft.VisualStudio.Composition` has no per-connection sharing boundary), just not yet patched with the
same ambient-token-based workaround. A third instance of the same shape: `FeatureProviderRefresher`
(`src/LanguageServer/Protocol/Handler/FeatureProviderRefresher.cs`) is a process-wide `[Shared]` event
source that every connection's `AbstractRefreshQueue`-derived queues (`SemanticTokensRefreshQueue`,
`DiagnosticsRefreshQueue`, `CodeLensRefreshQueue`, `InlayHintRefreshQueue`, `ProjectContextRefreshQueue`)
subscribe to — a `workspace/featureProviders/_vs_refresh` notification meant for one connection fires the
refresh queues of every other connection sharing the daemon, causing unrelated editors to recompute
semantic tokens/diagnostics/code lenses/inlay hints/project context. Not a content-disclosure bug like the
Razor pair (no data crosses connections, just spurious recomputation), but the same "no per-connection
routing for a shared MEF singleton" gap. A fourth, more severe instance: `ServiceBrokerProvider`
(`src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/BrokeredServices/ServiceBrokerProvider.cs`) is an
`[ExportWorkspaceService(...), Shared]` part, which resolves to the same singleton for every
`Workspace`/connection sharing the daemon's `ExportProvider` (confirmed via `MefWorkspaceServices`'s
constructor). `ServiceBrokerFactory.CreateAsync` calls `SetContainer` on it once per connection; the second
call trips `Contract.ThrowIfTrue(_serviceBrokerContainerTask.Task.IsCompleted)` — so a second daemon
connection's service-broker setup throws outright, not just misroutes brokered-service traffic to the wrong
editor. Three more Razor cohosting MEF singletons, same "classic `System.ComponentModel.Composition`
`[Export]` defaults to process-wide shared" shape as the original pair: `VSCodeWorkspaceProvider`
(`WorkspaceProviderInitializer`'s `SetWorkspace`) is overwritten by whichever connection's Razor cohost
initialized most recently, so `RemoteFindAllReferencesService`/`RemoteGoToDefinitionService` can navigate
using a different connection's workspace; `RazorCohostClientCapabilitiesService.SetCapabilities` has the
same "last write wins" shape for client capabilities, affecting completion/code-action/diagnostics/
semantic-token/remote-service responses; `CohostCompletionListCache`'s ten-entry circular buffer is shared
process-wide, so one client's completions can evict another's still-pending one before
`completionItem/resolve` runs, silently losing delegated/snippet resolution context. Two more: shared
`SemanticTokensRefreshNotifier` sends duplicate semantic-token refreshes only to whichever connection
initialized most recently (earlier connections get stale tokens), and shared `HtmlDocumentSynchronizer`'s
`_synchronizationRequests` is keyed only by URI, so `razor/documentClosed` from one connection can cancel
another connection's in-flight HTML sync for the same URI. A non-Razor instance:
`DecompilationMetadataAsSourceFileProvider` (`src/Features/Core/Portable/MetadataAsSource/`) mutates the one
shared `MetadataAsSourceWorkspace`'s fallback analyzer options on every navigation-to-metadata, so a later
connection's navigation can change settings in effect for an earlier connection's already-open metadata
document -- and it's below `LanguageServer.Protocol` in the dependency graph, so it's structurally out of
reach of `GetConnectionScopedOption` even in principle. One more Razor instance: `CohostDocumentSymbolEndpoint`'s
`_useHierarchicalSymbols` field is overwritten by whichever connection's dynamic-registration handshake ran
most recently, so `textDocument/documentSymbol` can return the wrong result shape (flat vs. hierarchical) for
a connection whose client capabilities didn't match whichever one wrote that field last.
**Workaround:** None needed for the option/log/handshake-routed config anymore. Telemetry misattribution,
the Razor leaks (eight, now), the `FeatureProviderRefresher` cross-connection refresh fan-out, the
`ServiceBrokerProvider` crash, and the `DecompilationMetadataAsSourceFileProvider` fallback-options leak have
no workaround and aren't going to get one without further work; all tracked as
[GoldMikeDev/roslyn#9](https://github.com/GoldMikeDev/roslyn/issues/9). Full design write-up, phase-by-phase
history, and the "Decisions" section explaining why telemetry is out of scope:
`docs/ide/specs/daemon-per-connection-isolation.md`.

## New loop-like statement kinds need registering in `IsContinuableConstruct`/`IsBreakableConstruct`

**Affected area:** `src/Workspaces/SharedUtilitiesAndExtensions/Compiler/CSharp/Extensions/SyntaxNodeExtensions.cs`
**Description:** `IsContinuableConstruct`/`IsBreakableConstruct` are shared helpers with their own
hardcoded `SyntaxKind` switch, consumed independently by `BreakKeywordRecommender`/
`ContinueKeywordRecommender` (keyword completion) and `LoopHighlighter` (keyword highlighting).
When this fork added `SyntaxKind.DoUntilStatement`, none of the other IDE-support work (classification,
formatting, outlining) touched this file, so `break`/`continue` silently weren't suggested and
weren't highlighted inside `do { } until (...)` loops until this helper was updated — with no error,
crash, or test failure to flag it.
**Workaround:** Any new loop-like statement kind must be added to `IsContinuableConstruct`'s switch
directly; it is not covered by any of the other per-feature registration points. See the
"Adding IDE Support for a New Statement/Expression SyntaxKind" checklist in
`.github/instructions/IDE.instructions.md`.

## Collapsed-region "..." is NOT Roslyn-rendered; Inline Hints is the real intra-text-adornment pattern

**Affected area:** anything needing to paint extra/resolved info inline over source text without
touching the buffer (e.g. `.github/memory/experimental-language-features.md`'s `*.` root-namespace
adornment)
**Description:** It's tempting to assume collapsed outlining regions (the "..." shown for a folded
`#region`/method body) are an example of a Roslyn-owned intra-text adornment to copy. They aren't —
Roslyn only supplies `BlockSpan`s (via `BlockStructureProvider`s) saying *where* things are
collapsible; the VS platform itself renders the collapsed ellipsis. The actual Roslyn-owned mechanism
for "show computed text inline without changing the buffer" is **Inline Hints**
(`src/Features/Core/Portable/InlineHints` → `src/EditorFeatures/Core/InlineHints`), which already
does this for inferred-type hints and parameter-name hints via WPF `IntraTextAdornmentTag`s.
**Workaround:** For a new intra-text adornment, add a language-service category to
`AbstractInlineHintsService`'s aggregation (see `IInlineRootNamespaceHintsService` for the pattern —
Core/Portable interface + `AbstractInlineHintsService.GetInlineHintsAsync` wiring + a C#-only, or
per-language, implementation) rather than reaching for outlining/collapsible-span APIs.
