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
**Fixed since the above was written:** `RazorClientServerManagerProvider`
(`src/Razor/.../Services/RazorClientServerManagerProvider.cs`), `CohostConfigurationChangedService`'s
`IClientSettingsManager` (`src/Razor/.../Services/ClientSettingsManager.cs`), and the separate remote/OOP
`RemoteClientSettingsManager` (`src/Razor/.../Microsoft.CodeAnalysis.Remote.Razor/Settings/`) all used to
cache one value overwritten by whichever connection's Razor cohost startup/configuration-change ran last —
now keyed by `AmbientConnectionToken.Current` (`ConditionalWeakTable<object, T>` per connection), the same
ambient-token pattern `ConnectionScopedOptionOverrides` already used on the Roslyn LSP side, verified by
`RazorPerConnectionIsolationTests`. `ServiceBrokerProvider`
(`src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/BrokeredServices/ServiceBrokerProvider.cs`), which
used to be an `[ExportWorkspaceService(...), Shared]` part crashing the second daemon connection's
`ServiceBrokerFactory.CreateAsync` outright (`Contract.ThrowIfTrue` tripping on the shared singleton's
already-completed container task), is now an `[ExportWorkspaceServiceFactory(...), Shared]`
(`ServiceBrokerProviderFactory`) keyed the same way — one provider shared across all of a *single*
connection's workspaces (Host and MiscellaneousFiles both need the same completed container, since
`serviceBroker/connect` only calls `SetContainer` on one of them), but a fresh instance per connection.
Verified by `Daemon_EachServerGetsItsOwnServiceBrokerProvider`. `FeatureProviderRefresher`
(`src/LanguageServer/Protocol/Handler/FeatureProviderRefresher.cs`) no longer exposes a plain
`event Action<DocumentUri?>?`; `Subscribe`/`Unsubscribe` record the subscribing `AbstractRefreshQueue`'s
`AmbientConnectionToken.Current` alongside its handler, and `RequestProviderRefresh` only invokes
subscribers whose recorded token matches the token ambient *at request time* (falling back to broadcasting
to all subscribers only when there's no ambient connection at request time) — verified by
`FeatureProviderRefresherTests` (two-connection targeting, no-ambient-connection broadcast fallback,
unsubscribe). All six Razor cohosting MEF singletons previously listed here are fixed too, same
ambient-token-keyed pattern: `VSCodeWorkspaceProvider`, `AbstractClientCapabilitiesService` (base of
`RazorCohostClientCapabilitiesService`), `CompletionListCache` (base of `CohostCompletionListCache`, now an
`Impl` instance per connection instead of one shared circular buffer), `SemanticTokensRefreshNotifier`
(state *and* its `ClientSettingsChanged` subscription/handler are now per-connection), `HtmlDocumentSynchronizer`
(`_synchronizationRequests` is now a dictionary-per-connection), and `CohostDocumentSymbolEndpoint`
(`_useHierarchicalSymbols` is now boxed per-connection).
**Still open, genuinely unresolved (not by design):** A non-Razor instance:
`DecompilationMetadataAsSourceFileProvider` (`src/Features/Core/Portable/MetadataAsSource/`) mutates the one
shared `MetadataAsSourceWorkspace`'s fallback analyzer options, so a later connection's navigation-to-metadata
can still change settings in effect for an earlier connection's already-open metadata document — narrowed
(the mutation now only happens on first generation of a given metadata document, not on every re-navigation)
but not eliminated, because `MapDocument`/`ShouldCollapseOnOpen` (the `IMetadataAsSourceFileProvider`
call sites) have no connection/session identity available today, and it's below `LanguageServer.Protocol` in
the dependency graph, so it's structurally out of reach of `GetConnectionScopedOption` even in principle —
a full fix needs new plumbing from the LSP `RequestContext` down through `MetadataAsSourceFileService`/
`AbstractLanguageService`/`AbstractStructureTaggerProvider`.
**Workaround:** None needed for the option/log/handshake-routed config, `ServiceBrokerProvider`,
`FeatureProviderRefresher`, or any of the six Razor singletons anymore. Telemetry misattribution and the
`DecompilationMetadataAsSourceFileProvider` fallback-options leak have no workaround and aren't going to get
one without further work; both tracked as
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
