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
**Fixed:** `DecompilationMetadataAsSourceFileProvider` (`src/Features/Core/Portable/MetadataAsSource/`) used to
mutate the one shared `MetadataAsSourceWorkspace`'s *solution-wide*, language-keyed
`Solution.FallbackAnalyzerOptions` on every new metadata document generation, so a later connection's
navigation-to-metadata could silently change settings in effect for an earlier connection's already-open
metadata document. Fixed via a genuine per-project fallback-options override:
`Solution.WithProjectFallbackAnalyzerOptions(ProjectId, StructuredAnalyzerConfigOptions)` /
`Workspace.OnProjectFallbackAnalyzerOptionsChanged` set the fallback options for just the one temporary
project the provider creates for that navigation (via the pre-existing but previously solution-only-reachable
per-project state on `ProjectState`/`AnalyzerConfigOptionsCache`), leaving every other project's — and every
other connection's — fallback options untouched. `Project.GetFallbackAnalyzerOptions()` now reads this
per-project state directly (`State.FallbackAnalyzerOptions`) instead of the solution-wide dictionary, so the
per-project override is honored transparently by every consumer (formatting, diagnostics, brace completion,
etc.) with zero behavior change for any project that never sets one. Verified by
`SolutionTests.WithProjectFallbackAnalyzerOptions_DoesNotAffectOtherProjects` (Workspaces-layer round-trip)
and `MetadataAsSourceTests.TestFallbackAnalyzerOptionsIsolatedPerConnection` (two-connection regression through
the shared provider/workspace).
**Workaround:** None needed anywhere in this daemon-per-connection-isolation effort anymore. Telemetry
misattribution remains open by design (see above); both were tracked as
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

## UpdateProcThreadAttribute retains lpValue -- it does not copy it

**Affected area:** `src/LanguageServer/roslyn-language-server/Interop/Win32BreakawayProcessLauncher.cs`
**Description:** `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` (used to restrict `CreateProcess`'s handle
inheritance to exactly the redirected stdio pipes, instead of every inheritable handle open
anywhere in the process) is registered via `UpdateProcThreadAttribute(..., lpValue: handles, ...)`.
Win32 only stores the pointer passed as `lpValue` -- it does not copy the buffer's contents -- and
`CreateProcess` doesn't read through it until later, when the attribute list is actually consumed.
The original implementation `stackalloc`'d the `HANDLE[3]` array inside a helper method
(`TryCreateHandleListAttributeList`) that returned before `CreateProcess` ran, so by the time
`CreateProcess` read the handle list back, that stack memory was already dead/reused --
silently defeating the restriction. This wasn't caught by unit tests (nothing in this class can be
exercised without a real Windows job object); it surfaced as `Win32BreakawayLauncherTests
.DaemonLaunchBreaksAwayFromKillOnCloseJobObject` reporting `SENTINEL:Accessible` instead of the
expected `SENTINEL:Inaccessible` on an actual Windows CI run (GoldMikeDev/roslyn#11).
**Workaround:** Any buffer passed as `UpdateProcThreadAttribute`'s `lpValue` must stay allocated
(heap, not stack, and not scoped to a helper method that returns early) until after `CreateProcess`
has run and `DeleteProcThreadAttributeList` has been called -- the same lifetime as the attribute
list's own backing buffer. `TryCreateHandleListAttributeList` now `Marshal.AllocHGlobal`s the handle
array alongside the attribute-list buffer and frees both together in `TryStart`'s `finally` block.

## `NamedPipeServerStream.RunAsClient` requires a prior read -- elevation check must run after the handshake

**Affected area:** `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/LanguageServer/NamedPipeDaemonConnectionSource.cs`
**Description:** `NamedPipeUtil.CheckClientElevationMatches` impersonates the connecting client via
`NamedPipeServerStream.RunAsClient` to compare its elevation against the daemon's own. On Windows,
`RunAsClient` throws `IOException: "Unable to impersonate using a named pipe until data has been
read from that pipe."` if nothing has been read from that pipe instance yet. `ProcessAcceptedConnectionAsync`
originally ran this elevation check *before* reading the per-connection `ConnectionHandshake`, so
every single daemon connection failed elevation and was rejected -- not a race or a cold-start issue,
a deterministic 100% failure once a client got as far as being accepted. This wasn't caught by unit
tests (`CheckClientElevationMatches` returns `true` without calling `RunAsClient` at all on
non-Windows, so Linux CI and this repo's Linux dev sandbox can't exercise the failure) and surfaced
via `daemon-diagnostic-*.log` files (see `ROSLYN_DAEMON_DIAGNOSTIC_LOG=1` in `Program.cs`) captured
from a real Windows run, where every one of 15 daemon processes hit the same exception at the same
call site. It's the root cause behind the `DaemonServerLifecycleTests` connection failures this fork
investigated across several sessions.
**Workaround:** Any code path that validates a newly-accepted `NamedPipeServerStream` must perform at
least one real read from that stream before calling `NamedPipeUtil.CheckClientElevationMatches` on
it -- see `NamedPipeClientConnection.ReadBuildRequestAsync` in the compiler server (`src/Compilers/Server/VBCSCompiler/NamedPipeClientConnection.cs`)
for the same shared `NamedPipeUtil` used in the correct order, with a comment explaining why. Future
refactors of `ProcessAcceptedConnectionAsync` (or any other consumer of `CheckClientElevationMatches`)
must preserve "read first, then check elevation" -- reordering it back regresses this silently on
Linux, since nothing there will ever throw to catch it.
