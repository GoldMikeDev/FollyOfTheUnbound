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

## `ProjectSystemProject.RemoveFromWorkspaceMaybeAsync` releases its own resources independently of solution membership

**Affected area:** `src/Workspaces/Core/Portable/Workspace/ProjectSystem/ProjectSystemProject.cs`
**Description:** `RemoveFromWorkspaceMaybeAsync` can be entered concurrently for the same project --
e.g. the LSP daemon's `Workspace` being disposed at the same time as, and before, the
`LanguageServerProjectLoader` that owns the project (see that type's `Dispose` for the same race
already anticipated there). When that happens,
`_projectSystemProjectFactory.Workspace.CurrentSolution.ContainsProject(Id)` is `false` and the method
throws `InvalidOperationException("The project has already been removed.")`. A `_removedFromWorkspace`
flag, set under `_gate` the first time this method runs, guards against a second racing call
re-entering cleanup: that second call throws the same exception immediately rather than proceeding.
**Invariant:** `_fileChangesToProcess.Dispose()` and `_documentFileChangeContext.Dispose()` run
unconditionally, guarded only by `_removedFromWorkspace`, before the `ContainsProject` check that can
throw -- they belong to this `ProjectSystemProject` instance and aren't tied to solution membership, so
the first call releases them exactly once regardless of which caller wins the race to remove the
project. If a future edit moves either disposal after that check, or removes the
`_removedFromWorkspace` guard, hitting this race leaks `_documentFileChangeContext` as an active
file-watch context for the rest of the process's lifetime -- `FileWatcherReleaseTracker`
(`src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer.UnitTests/Utilities/FileWatcherReleaseTracker.cs`)
asserts against exactly that after every test, so one project hitting this race would fail every later
test sharing the xUnit test-host process.

## `LspRelay.CopyStreamDetectingSentinelAsync` must keep reading after a forward fails, not just after the sentinel's own chunk fails

**Affected area:** `src/LanguageServer/roslyn-language-server/LspRelay.cs`
**Description:** The thin client's editor-side transport can already be disposed by the time the
daemon writes `CleanExitSentinel` -- `TestLspClient.ShutdownAndExitCoreAsync` (and any well-behaved
LSP client following the same pattern) sends `exit` as a fire-and-forget notification and then
immediately tears down its side of the connection without waiting for any response, so the relay's
attempt to forward the daemon's *next* chunk back to the editor routinely lands after that pipe is
already gone. The sentinel is not necessarily in that first failing chunk -- unrelated response bytes
the daemon wrote just before shutting down commonly fail to forward first, one or more reads before
the sentinel-carrying chunk even arrives. Treating any forwarding failure as conclusive (the original
implementation returned immediately on the first destination-write exception) discards the read loop
before it ever reaches the sentinel, so `LspRelay.RelayAsync` classifies a perfectly clean shutdown as
`EditorConnectionLost` -- this was `DaemonServerLifecycleTests.KeepAlive_DaemonReusedWithinWindow_ThenExitsWhenIdle`'s
observed flake (`Assert.Equal(0, ...)` failing with `Actual: 2`), reproducible locally at roughly a
1-in-6 rate before this fix.
**Invariant:** `CopyStreamDetectingSentinelAsync` keeps reading `source` after a destination-write
failure instead of returning immediately -- it only stops forwarding (via the `destinationAlive` flag).
That drain is bounded by `s_deadDestinationDrainTimeout` (5s, on a `CancellationTokenSource` linked to
the caller's token): a source that never produces the sentinel and never itself closes is a real
possibility (e.g. stdio transport, where the editor's read and write streams are independent and only
one side has broken), so the drain cannot run unbounded either -- if the timeout elapses first, the
method falls back to the original `DestinationException` outcome. Whether the daemon's exit was clean
is determined by what it wrote to the source stream within that window; a dead destination alone does
not change that fact and must not short-circuit the search for the sentinel before the window expires.
A future edit that reintroduces an unconditional early return on the first destination-write exception,
or removes the timeout bound, regresses this silently, since nothing in CI reliably reproduces a race
this timing-sensitive on a single run.

## `LspRelay.RelayAsync` must race `serverToEditor`'s wait against the *failure*, not a fixed timer covering the whole wait

**Affected area:** `src/LanguageServer/roslyn-language-server/LspRelay.cs`
**Description:** When `serverToEditor` is the direction `RelayAsync` is still waiting on (the other one
already completed), its destination write can fail at *any* point during that wait -- there's no way to
bound when in advance, since `serverToEditor` keeps trying to forward whatever the daemon sends for as
long as it's running. Once it does fail, `CopyStreamDetectingSentinelAsync` starts its own fresh
`s_deadDestinationDrainTimeout`-bounded drain from that moment (see the entry above). Three earlier
attempts at bounding `RelayAsync`'s own outer wait all under- or over-covered this: racing `serverToEditor`
against a fixed `s_secondCloseGracePeriod` timer cut off a drain that started partway through it; racing
it against `s_secondCloseGracePeriod + s_deadDestinationDrainTimeout` still under-covered a failure that
started late in that combined window (a write failing near the end still needs nearly a full extra
`s_deadDestinationDrainTimeout` beyond it), both producing a spurious `EditorConnectionLost` for what was
actually a clean shutdown; and removing the outer race entirely (awaiting `serverToEditor` unconditionally)
overcorrected into a genuine hang whenever the destination never fails at all -- the daemon never responds
and never closes, so no write is ever attempted and no drain ever starts -- exactly the case
`EditorClosesAloneWithoutServerFollowing_IsNotCleanShutdown` exercises.
**Invariant:** `RelayAsync` races `serverToEditor` against two things, not one: `s_secondCloseGracePeriod`
itself, and a `destinationFailedSignal` (`TaskCompletionSource`) that `CopyStreamDetectingSentinelAsync`
completes the instant a destination write first fails -- i.e. the moment its own post-failure drain
begins. If the grace period elapses with no failure ever observed, the wait times out normally (the same
outcome `editorToServer`'s unbounded `ProcessUtilities.CopyStreamAsync` already gets from that same grace
period, since it has no internal timeout of its own to defer to). If a failure is observed at any point --
even right at the boundary of the grace period -- `serverToEditor` is then awaited directly with no further
outer bound, trusting `CopyStreamDetectingSentinelAsync`'s own internal timeout unconditionally from then
on, however late within the original wait it started. `MaximumShutdownWait`
(`s_secondCloseGracePeriod + s_deadDestinationDrainTimeout`) is accurate again as the true worst case under
this design. A future edit that races `serverToEditor` against only a fixed-duration timer covering the
*whole* wait (whether or not it sums the two constants) reintroduces the under-coverage bug; awaiting it
with no bound at all reintroduces the hang. The fix has to target the failure's own start, not the wait as
a whole.

## `RpcServer.ProcessRequestAsync` must swallow a broken-pipe response write, not let it crash the whole BuildHost process

**Affected area:** `src/Workspaces/MSBuild/BuildHost/Rpc/RpcServer.cs`
**Description:** Diagnosed from a real Core test-run crash dump (`WorkItem_24`, `MoveStaticMembersRefactoringTests`):
`Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll` crashed with an unhandled `System.IO.IOException: "Pipe
is broken."`. `ProcessRequestAsync`'s response write (`_streamWriter.WriteLineAsync`/`FlushAsync`) can race the
client (the language server host process) tearing down and closing its end of the pipe -- most commonly because
the client itself is shutting down while this BuildHost still has a response in flight. Before this fix, that
write was unguarded: the `IOException` propagated out of `ProcessRequestAsync`'s `Task.Run` background task, into
`RunAsync`'s `Task.WhenAll(remainingTasks)`, faulting `RunAsync`'s own task and crashing the entire BuildHost
process with an unhandled exception instead of exiting quietly.
**Invariant:** The response write in `ProcessRequestAsync` is wrapped in `try`/`catch (IOException)`. If the pipe
is already gone, there is nobody left to receive the response, so the failure isn't actionable -- the catch calls
`Shutdown()` so `RunAsync`'s read loop also winds down on its own (via its existing `_shutdownTokenSource`
handling) rather than continuing to process requests against a dead pipe. A future refactor of this write path
(e.g. removing the try/catch as "dead code" or replacing it with a broader catch that also swallows unrelated
failures) must preserve this specific behavior: broken-pipe write failures during shutdown are expected and must
not propagate to crash the process, while other exceptions from the write path should still surface normally.
Regression test: `RpcTests.ResponseWriteAfterClientDisconnectsDoesNotCrashServer` (inherently racy like the
neighboring `RequestThatClosesServerDoesNotThrow`, guarding dotnet/roslyn#77040 -- not guaranteed to reproduce the
underlying race on every platform, see that test's own remarks). That test's `RpcPair` test harness itself is
worth noting: `ServerCompletion` originally used `RunAsync().ContinueWith(_ => _serverStream.Dispose(), ...)`,
whose default continuation discards the antecedent's exception -- so `RunAsync` faulting was previously
invisible to every test in this file (`await rpcPair.ServerCompletion` would still complete successfully). Fixed
via an `async` local function that awaits `RunAsync` directly (propagating any fault) and disposes the stream in
a `finally`. That fix immediately exposed a second, pre-existing bug: `RpcPair.DisposeAsync` disposed
`_serverStream` directly instead of calling `Server.Shutdown()` first (unlike every real production caller, e.g.
`AbstractBuildHost`), which yanks the pipe out from under `RunAsync`'s blocked read and throws a raw
`IOException`/`SocketException` instead of the graceful, expected `OperationCanceledException` `Shutdown()`'s
cancellation produces -- this was failing on nearly every test's normal teardown, silently, the entire time.
`DisposeAsync` now calls `Server.Shutdown()` before disposing the client stream, matching production shutdown
order.
