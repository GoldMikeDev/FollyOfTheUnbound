# Daemon-mode per-connection isolation

## Status

All phases are complete except phase 6 (telemetry), which is dropped by design: 1 (verify `AsyncLocal`
propagation), 2 (wire it into `GlobalLogMessageLogger`), 3 (audit `IGlobalOptionService` call sites reachable
from LSP request handling), 4 (the per-connection option override facade + `DidChangeConfigurationNotificationHandler`
rewire), 5 (per-connection `ExtensionLogDirectory` routing via a new client→daemon handshake), and 7
(`SourceGeneratorExecutionPreference` routing, reusing that same handshake). Tracked in
[GoldMikeDev/roslyn#9](https://github.com/GoldMikeDev/roslyn/issues/9). Flagged across three separate Codex
review rounds on PR #3 (the daemon-mode `roslyn-language-server` thin client).

**A foundational ordering bug, found by Codex during phase 5 review, affected every phase built on the
ambient-context primitive (2, 4, 5) for real (non-test) request dispatch** -- see "The ambient-token ordering
bug" below. Fixed; all phases were re-verified against the fix.

## Background

This fork's `roslyn-language-server` thin client can run the bundled `Microsoft.CodeAnalysis.LanguageServer`
as a shared daemon: the first client to connect launches it (via a short-lived bootstrap that orphans the
daemon out of the editor's process tree), and later clients reuse the same daemon process by connecting to
its named pipe, keyed by `DaemonPipeName.GetPipeName` (user identity, elevation, tool path, and the launching
client's startup arguments).

The daemon itself, however, was built around a **single-client mental model** carried forward largely
unchanged from the non-daemon (`--stdio`/`--pipe`, one server per process) case:

- `Program.RunAsync` constructs exactly one MEF `ExportProvider` (`LanguageServerExportProviderBuilder.CreateExportProviderAsync`)
  and exactly one `ExtensionAssemblyManager`, once, from whichever client's `ServerConfiguration` happened
  to launch the daemon.
- Every subsequent client that connects to that daemon gets its own `LanguageServerHost`
  (`LanguageServerConnectionManager.TryStartServerAsync`), but that host is built from the **same** shared
  `ExportProvider` — there is no MEF-level notion of "this export belongs to connection N."
- Several `Program.RunAsync`-level side effects driven by `ServerConfiguration` — `IGlobalOptionService`
  writes, telemetry session initialization, extension log directory creation — also happen exactly once, from
  the first client's configuration, and are never revisited for later connections.

Multiple daemon clients are a real scenario for this fork (that's the entire point of the daemon mode), so
these singleton assumptions produce three distinct, independently-discovered symptoms.

## Why this isn't a MEF version problem

The obvious-looking fix — give each connection its own scoped `ExportProvider` — was investigated and ruled
out. This repo already restores `Microsoft.VisualStudio.Composition` **18.9.15**, which is *ahead of the
entire public release history on nuget.org* (whose latest listed version is 17.13.41; 18.9.x is only
available from Microsoft's internal feed). Reflecting directly over the restored 18.9.15 assembly found no
`CreateShared`/scoped-child-provider API anywhere on `ExportProvider` or `IExportProviderFactory`, and no
type in the assembly with "Scope," "Boundary," or "Shared" in its name beyond the existing `[Shared]`
attribute and static `SharingBoundary` metadata on `ComposablePartDefinition` (which describes the
composition graph but exposes no runtime API to instantiate a scoped sub-container from a built
`ExportProvider`). There is nothing to upgrade to. Any fix here has to be built by hand on top of what VS MEF
gives us, not unlocked by a package bump.

## The three symptoms

### 1. Option isolation (`IGlobalOptionService`)

`DidChangeConfigurationNotificationHandlerFactory` hands every `LanguageServerHost` the same shared
`IGlobalOptionService` singleton (`src/Workspaces/Core/Portable/Options/GlobalOptionService.cs`). Each
client's `DidChangeConfigurationNotificationHandler.RefreshOptionsAsync` → `SetGlobalOptions` call mutates
that one shared instance, so the last client to initialize or change settings silently changes behavior for
every other concurrent connection. `IGlobalOptionService` is also read from throughout the workspace/IDE
layer (formatting, completion, every option-gated feature) — not just that one write path — so this is a
read problem as much as a write problem.

### 2. Per-session server configuration

Several `ServerConfiguration` fields are consumed exactly once, at daemon startup, from the first client's
command line, and never revisited:

- `ExtensionLogDirectory` — read once in `Program.RunAsync` to `Directory.CreateDirectory` it, and read again
  from the single composed `ServerConfigurationFactory` export by `RunTestsHandler` for every connection's
  test-log path.
- `TelemetryLevel` / `SessionId` — passed once to `RoslynLogger.Initialize`.
- `SourceGeneratorExecutionPreference` — applied once via `globalOptionService.SetGlobalOption`, i.e. this is
  actually the *same* problem as symptom 1, not a separate one.

This was originally scoped (by a Codex finding on `DaemonPipeName`) as "just exclude per-session args like
`--extensionLogDirectory` from the daemon pipe-key hash so sessions with different log directories can share
a daemon." That framing was investigated and rejected: excluding an argument from the key without also
routing its value to each connection individually doesn't fix anything, it just relocates the bug from
"wrong daemon" (loud, visible) to "second client's log directory silently ignored" (quiet, worse). The pipe
key is not the right layer to fix this at.

**Also investigated and found to be a dead end, not just deferred**: `RoslynLogger`
(`src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Logging/RoslynLogger.cs`) is a hard process-wide
singleton — `Initialize` does `Contract.ThrowIfTrue(_instance is not null)` and registers itself into
`Microsoft.CodeAnalysis.Internal.Log.Logger`'s static global logger. There is currently no way for two
`RoslynLogger` instances (one per connection, with its own `TelemetryLevel`/`SessionId`) to coexist without
changes to the telemetry plumbing itself, not just this file. So of the three "per-session config" fields,
only `ExtensionLogDirectory` is close to independently fixable; `TelemetryLevel`/`SessionId` and
`SourceGeneratorExecutionPreference` both bottom out in the same singleton problem as symptom 1.

**A fourth, narrower case turned out to be genuinely fixable, unlike the three above — fixed directly**:
`ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE` (resolved into the daemon's keepalive by
`LanguageServerCommandLine`) is read from the *environment*, not from `serverArguments`, so two clients
relying on different values for it (without an explicit `--daemonKeepAlive` argument, which would already be
part of `serverArguments`) previously derived the same pipe name and silently shared a daemon keyed to
whichever value happened to launch it. Unlike `ExtensionLogDirectory`, keepalive can't be given per-connection
semantics at all — it governs how long the one shared daemon lingers after its *last* client disconnects, not
any single client's session — so there's no routing fix available, only the same "different daemons" trade-off
already accepted for incompatible `serverArguments`. `DaemonKeepAliveEnvironmentVariable` moved from
`LanguageServerCommandLine` into the shared `DaemonPipeName.cs` (aliased back for source compatibility) so its
raw value can be folded into the pipe-key hash. See `DaemonPipeNameTests.PipeName_DiffersByKeepAliveEnvironmentVariable`.

### 3. Global log broadcast (`GlobalLogMessageLogger`) — fixed in phase 2

`GlobalLogMessageLogger` (`src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Logging/GlobalLogMessageLogger.cs`)
implements the MEF-exported process-global `ILogger`. It **used to** iterate every started server
(`LanguageServerConnectionManager.GetStartedServers()`) and forward each log entry to every one of them over
`window/logMessage`, regardless of which connection the work was attributable to -- so, since some of what
logs through this path is workspace-specific (e.g. `VSCodeExtensionAssemblyAnalyzerLoader.LoadFromPath` logs a
full analyzer path), one client's activity became visible in every other connected client's log output.

This is now fixed (phase 2, see "Suggested phasing" below): `LanguageServerConnectionManager` sets
`DaemonConnectionContext.SetCurrent(server)` immediately before starting each connection's server, so the
JSON-RPC dispatch loop that connection spins up captures it as ambient context.
`GlobalLogMessageLogger.GetTargetServers` reads that ambient context and, when it identifies a still-live
connection, routes the log entry to just that one server instead of broadcasting. The broadcast-to-all
behavior is retained only as the fallback for genuinely ambientless activity (process-wide startup logging
before any client has connected, or a stale ambient value whose connection already ended) -- see
`GlobalLogMessageLoggerTests` for the cases this covers.

## Why one fix, not three

All three symptoms come from the same structural gap: the daemon has exactly one MEF composition and one
`ServerConfiguration`, consumed once, with nothing that says "this piece of shared state belongs to
connection N." A narrow patch to any one symptom (e.g. giving `GlobalLogMessageLogger` its own ad hoc
per-connection routing while leaving options untouched) would leave the same gap open for the other two and
add a one-off mechanism that the next fix can't reuse.

## Proposed direction

Build a **per-connection ambient context primitive** that every piece of currently-singleton shared
infrastructure can consult, rather than trying to give each connection a real scoped `ExportProvider` (ruled
out above) or rewriting each singleton's storage model independently (duplicated effort, three different
ad hoc mechanisms).

Sketch: an `AsyncLocal<ConnectionContext?>` (name TBD), set for the duration of async work performed on
behalf of a given `LanguageServerHost`/connection — most naturally established where
`LanguageServerConnectionManager.TryStartServerAsync` starts a connection's server loop, and flowing
naturally through `async`/`await` continuations the same way `ExecutionContext` already does. Consumers:

- `GlobalLogMessageLogger.Log`/`IsEnabled` reads the ambient context and routes to just that one server,
  falling back to the current broadcast-to-all behavior only when nothing is ambient (e.g. genuine
  startup-time logs before any client has connected).
- A per-connection `IGlobalOptionService` **facade** (not a MEF-level scope) wraps the real shared singleton:
  reads check a per-connection override dictionary keyed by the ambient context first, then fall through to
  the shared instance; writes only ever touch the local dictionary. `DidChangeConfigurationNotificationHandlerFactory`
  would need to hand out the facade instead of the raw export.
- `ExtensionLogDirectory` (and eventually `TelemetryLevel`/`SessionId`, contingent on a separate
  `RoslynLogger` redesign that is out of scope here) move from being read once in `Program.RunAsync` to being
  read per-connection wherever they're actually consumed (e.g. `RunTestsHandler`), sourced from that
  connection's own parsed configuration instead of the daemon-global one.

## Suggested phasing

This is explicitly **not** a single PR. Recommended order, smallest/least-entangled first:

1. **Build the ambient-context primitive in isolation** — done. `DaemonConnectionContext`
   (`src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/LanguageServer/DaemonConnectionContext.cs`), an
   `AsyncLocal<LanguageServerHost?>` wrapper, propagation verified by `AsyncLocalPropagationTests`.
   **Split during phase 4** (see phase 4 below) into `AmbientConnectionToken`
   (`src/LanguageServer/Protocol/LspServices/AmbientConnectionToken.cs`), the actual `AsyncLocal<object?>`
   holder, with `DaemonConnectionContext` reduced to a thin `LanguageServerHost`-typed wrapper around it. This
   was required by project layering, not a design change: the daemon project
   (`Microsoft.CodeAnalysis.LanguageServer`, home of `LanguageServerHost`/`DaemonConnectionContext`) has a
   one-way `ProjectReference` to the Protocol project (`Microsoft.CodeAnalysis.LanguageServer.Protocol`, home
   of the LSP handlers and `*OptionsStorage` files phase 4 needed to touch); Protocol cannot reference back to
   `LanguageServerHost`. `AmbientConnectionToken` is typed `object?` precisely so it can live at the lower
   layer both projects can see, with each layer attaching its own meaning to the token's identity
   (`LanguageServerHost` in the daemon project; an opaque per-connection key in Protocol).
2. **Wire it into `GlobalLogMessageLogger`** — done. `LanguageServerConnectionManager.TryStartServerAsync`
   calls `DaemonConnectionContext.SetCurrent(server)` immediately before `server.Start()` (so the JSON-RPC
   dispatch loop `Start()` spins up captures it), and `GlobalLogMessageLogger.GetTargetServers` routes to just
   that connection when it's set and still live. **Refined after an initial gap**: a *stale* ambient
   connection (its client disconnected after the value was captured, e.g. `RequestExecutionQueue` cancels a
   fire-and-forget non-mutating request on shutdown without awaiting it, so the request's own work can still
   be running and logging after its connection is gone) does **not** fall back to broadcasting to every other,
   *unrelated* live connection -- that would just relocate the leak from "this client's activity visible
   everywhere" to "this now-departed client's activity visible in other still-connected clients' logs," not
   fix it. It falls through to the fallback logger (an empty target list) instead, same as the "no servers
   started yet" case. Only a genuinely null ambient context (no connection at all, e.g. process-wide startup
   logging) still broadcasts. Verified by `GlobalLogMessageLoggerTests`, using the real multi-client daemon
   test harness (not mocks): no ambient context broadcasts to all; a live ambient connection routes to just
   that one; a stale ambient connection with no other servers targets nothing; a stale ambient connection
   *with another live connection still connected* does not leak to it; and two connections' ambient values
   don't cross-talk. All 6 pass, plus the full pre-existing 28-test daemon suite still passes unchanged.
3. **Audit every `IGlobalOptionService` call site** reachable from daemon-mode code before touching anything
   — this is the expensive, non-optional step. A facade is easy to write; verifying nothing still calls the
   raw shared singleton directly and silently bypasses it is the actual work, and doing it half-way would
   "look fixed without being fixed" (the same failure mode flagged in the original Codex finding). **Done.**
   Findings, scoped per the "Decisions" section below (LSP request-handling path, not the whole IDE layer):
   - **One confirmed write call site**, and it's the one this whole symptom is named after:
     `DidChangeConfigurationNotificationHandler.RefreshOptionsAsync`
     (`src/LanguageServer/Protocol/Handler/Configuration/DidChangeConfigurationNotificationHandler.cs`),
     reachable both from the `workspace/didChangeConfiguration` notification itself and from
     `OnInitializedAsync`'s initial fetch.
   - **Two writes that are daemon-startup-time, not per-connection LSP dispatch**, and therefore correctly
     out of scope per the phase-3 scoping decision: `ServerConfigurationFactory.InitializeConfiguration` and
     `Program.cs`'s one-time `SourceGeneratorExecution` write (this is the same `SourceGeneratorExecutionPreference`
     case tracked as phase 7 — falls out of the facade for free once something routes it per-connection, but
     nothing does yet, so it stays shared for now).
   - **~35 handler classes** with direct read-only `IGlobalOptionService`/`IGlobalOptionService`-derived
     injection, most via `[ExportCSharpVisualBasicStatelessLspService(...), Shared]` handlers that are
     themselves process-wide singletons (no per-connection object identity to exploit even if we wanted to —
     confirming the ambient-context approach, not per-connection handler instances, is the only option here).
   - **The read surface funnels through a small, enumerable set of `*OptionsStorage` extension-method files**
     under `src/LanguageServer/Protocol/Features/Options/` — 12 of them call `IGlobalOptionService.GetOption`
     directly and were the migration surface for phase 4 (see below); a few others (e.g.
     `ClassificationOptionsStorage.cs`) are written against the more general `IOptionsReader` abstraction
     instead and were correctly left alone (see phase 4's "false positive" note). One additional call site,
     `ClientFallbackAnalyzerConfigOptionsProvider.cs`, reads via the `OptionKey2` overload rather than a typed
     option and needed its own extension overload. Total call-site count for phase 4 landed in the low dozens,
     not hundreds — the audit's main output was confirming that, not just asserting it.
4. **`IGlobalOptionService` facade + `DidChangeConfigurationNotificationHandlerFactory` rewire**, once step 3
   has produced a complete call-site inventory. **Done.** Implemented as explicit call-site routing rather
   than MEF-level interception (VS MEF's `[Export(typeof(IGlobalOptionService)), Shared]` process-wide
   singleton export has no supported way to shadow/override per connection without production catalog
   surgery — `ComposableCatalog.WithoutPartsOfTypes`/`.WithParts` exists and is used by
   `TestComposition.RemoveParts`/`.WithParts` in test infrastructure, but was judged too risky to apply to the
   real composition graph blind):
   - `ConnectionScopedOptionOverrides` (`src/LanguageServer/Protocol/Features/Options/ConnectionScopedOptionOverrides.cs`)
     — a `ConditionalWeakTable<object, ConcurrentDictionary<OptionKey2, object?>>` keyed by
     `AmbientConnectionToken.Current`'s identity. `TryGetOverride` checks the ambient connection's dictionary
     first; `SetOverrides` writes into it, falling back to writing the shared `IGlobalOptionService` directly
     only when there's genuinely no ambient connection (preserving prior behavior for connection-less callers
     instead of silently dropping the write).
   - `ConnectionScopedOptionExtensions` (`ConnectionScopedOptionExtensions.cs`) — `GetConnectionScopedOption`
     extension methods mirroring `IGlobalOptionService.GetOption`'s three overloads (`Option2<T>`,
     `PerLanguageOption2<T>` + language, `OptionKey2`), each checking the override before falling through to
     the real `GetOption`.
   - `DidChangeConfigurationNotificationHandler.RefreshOptionsAsync`'s final `SetGlobalOptions` call replaced
     with `ConnectionScopedOptionOverrides.SetOverrides` — the one confirmed write site from the audit.
   - All 12 `*OptionsStorage` files identified by the audit, plus `ClientFallbackAnalyzerConfigOptionsProvider.cs`
     and `OnAutoInsertHandler.cs`/`DiagnosticsPullCache.cs` (direct handler call sites also found during the
     audit), migrated from `GetOption` to `GetConnectionScopedOption`.
   - Verified by `ConnectionScopedOptionOverridesTests`
     (`src/LanguageServer/ProtocolUnitTests/Options/ConnectionScopedOptionOverridesTests.cs`): a write under one
     ambient connection token is invisible to a different token's reads and never mutates the underlying shared
     `IGlobalOptionService`, for both `Option2<T>`/`PerLanguageOption2<T>`-typed and `OptionKey2`-keyed reads;
     the no-ambient-connection case still falls all the way through to the shared service, matching prior
     behavior. `DidChangeConfigurationNotificationHandlerTest`'s pre-existing single-connection workflow test
     (which exercises the no-ambient-connection fallback path, since it drives one `LspTestServer` directly
     rather than through the multi-client daemon harness) still passes unchanged.
5. **`ExtensionLogDirectory` per-connection routing**, independent of steps 3–4 and can happen in parallel.
   **Done.** Required more than routing existing state, unlike phases 2–4: the daemon previously had *no*
   protocol at all for a connecting client to tell it anything about itself before the pipe stream became the
   raw LSP JSON-RPC channel (`LanguageServerConnection` was just a stream pair;
   `NamedPipeDaemonConnectionSource.AcceptConnectionsAsync` accepted a client and immediately yielded it with
   no handshake). Added:
   - `ConnectionHandshake` (`src/LanguageServer/DaemonConnection/ConnectionHandshake.cs`, source-shared into
     both the thin client and daemon like `DaemonPipeName`) — a small hand-rolled, length-prefixed binary
     frame (no JSON dependency, keeping the thin client dependency-light) a connecting client writes
     immediately after its pipe connects, carrying `ExtensionLogDirectory` and (for phase 7, unused for now)
     `SourceGeneratorExecutionPreference`. General-purpose by design rather than single-field, since phase 7
     needs the same mechanism for a second field.
   - `DaemonClient.ConnectPipe` (thin client) writes the handshake, built from the client's own
     `serverArguments` via `ConnectionHandshake.FromServerArguments`, right after connecting — blocking
     (`.GetAwaiter().GetResult()`), not awaited, because this method runs under `DaemonClientMutex`, which is
     documented to require acquire/release on the same thread (no `await` in between).
   - `NamedPipeDaemonConnectionSource.AcceptConnectionsAsync` reads the handshake (10s timeout; a client that
     never completes it is rejected like any other bad connection, not allowed to hang the accept loop) before
     yielding the connection.
   - `ConnectionHandshakeRegistry` (daemon project) associates each connection's handshake with its ambient
     token (see the ordering-bug fix below), read back by `RunTestsHandler` in place of the daemon-wide
     `ServerConfiguration.ExtensionLogDirectory`.
   - **Test-infrastructure fallout**: the daemon's real (non-thin-client) test harnesses connect with a raw
     `NamedPipeClientStream` and don't know about this new protocol. Both `AbstractLanguageServerHostTests`
     (`DaemonClientTestLspServer.CreateAsync`) and `LanguageServerDaemonTests`
     (`Daemon_SlowServerStartup_DoesNotBlockAcceptingNextConnection`'s raw client) needed a
     `ConnectionHandshake.Empty.WriteAsync(...)` added right after connecting -- without it, every test
     connection paid the daemon's full 10-second handshake-read timeout before being rejected, which is what
     turned a normal ~20-second daemon test run into a 77-minute one the first time this was tested end to
     end. Caught by actually running the full suite, not just the individually-targeted new tests, which is
     why "run the whole affected suite, not just what you wrote" stayed the standard here even under time
     pressure.
6. **`TelemetryLevel`/`SessionId`/`RoslynLogger`**: **decided out of scope, by design** (see "Decisions" below)
   — not deferred pending a future decision, just not going to happen.
7. **`SourceGeneratorExecutionPreference`**. **Done.** `LanguageServerConnectionManager.TryStartServerAsync`
   now parses `connection.Handshake.SourceGeneratorExecutionPreference` (case-insensitive
   `Enum.TryParse<SourceGeneratorExecutionPreference>`, matching how `LanguageServerCommandLine`'s
   `Option<SourceGeneratorExecutionPreference>` itself parses `--sourceGeneratorExecutionPreference` --
   deliberately *not* `SourceGeneratorExecutionPreferenceUtilities.Parse`, which is a different lowercase
   editorconfig-string format for a different purpose) and, if it parses, writes it as a connection-scoped
   override via `ConnectionScopedOptionOverrides.SetOverrides` while the connection's marker token is still
   ambient. A client that didn't send one, or sent one that doesn't parse, falls through to the daemon-wide
   default `Program.cs` already sets from whichever client launched the daemon -- unchanged from before this
   phase.
   - Verified by `SourceGeneratorExecutionPreferenceRoutingTests`, which surfaced a real gap in how far the
     *test-only* `DaemonConnectionContext.SetCurrent(LanguageServerHost)` shortcut (added for phase 2's
     `GlobalLogMessageLoggerTests`, which only needs `DaemonConnectionContext.Current` to resolve correctly)
     can be reused: it makes the *server instance itself* the ambient token, but the real per-connection write
     happens under a separate, internal marker token minted before that server is even constructed (see the
     ordering-bug section above). A test using `SetCurrent(server)` to read back an option override doesn't
     see it -- it's looking up the wrong key entirely, not "no override was written" -- and silently falls
     through to the shared default, which was indistinguishable from a passing assertion until a value
     collided with that default by coincidence. Added `DaemonConnectionContext.GetAmbientTokenForTesting`,
     a reverse `LanguageServerHost` → token lookup populated alongside `Associate`, so tests can recover and
     re-establish the *actual* token real connection startup used, rather than substituting a different one
     that happens to also resolve `Current` correctly for the one thing (`GlobalLogMessageLogger`) that
     doesn't care about token identity beyond that.

## Post-phase-7 Codex findings

Six more real findings across three Codex review rounds on the phase 7 commits, all fixed:

- **Handshake-processing failures weren't cleaned up.** `LanguageServerConnectionManager.TryStartServerAsync`'s
  handshake handling (`Directory.CreateDirectory` for a client's `ExtensionLogDirectory`; parsing and applying
  its `SourceGeneratorExecutionPreference`) ran *after* `LanguageServerHost` was constructed but *outside* any
  try/catch, unlike construction itself (which already aborts/disposes on failure) and unlike `Start()` (same).
  A failure there -- e.g. `CreateDirectory` throwing because a client-supplied log path's permissions changed,
  or a path segment collided with an existing file -- propagated up to `StartAndSuperviseAsync`'s
  `catch (Exception ex) when (isolateFaults)`, which only logs. The constructed server was never aborted and
  `connection.Resource` was never disposed, so `NamedPipeDaemonConnectionSource`'s already-incremented
  active-connection count (from `OpenConnection()`) never returned to zero -- `ConnectionIdleTimeout` only
  re-arms when it does -- permanently preventing the daemon from ever reaching its idle keepalive timeout,
  regardless of how it's configured. Fixed by wrapping the handshake-processing block in the same
  abort-server/dispose-connection/rethrow pattern already used for construction and `Start()`. Verified by
  `LanguageServerDaemonTests.HandshakeProcessingFailure_CleansUpConnection_AndIdleTimeoutStillElapses`, which
  forces `CreateDirectory` to fail (a path segment that's an existing file) and confirms the daemon still
  reaches its (short, test-only) keepalive and exits -- it wouldn't have, before the fix, regardless of how
  long the test waited.
- **`--extensionLogDirectory` (and, by the same reasoning, `--sourceGeneratorExecutionPreference`) still split
  clients into separate daemons.** `DaemonPipeName.GetPipeName` hashes the complete `serverArguments` array,
  including these two, so two clients differing only in one of them still got different pipe names and never
  shared a daemon -- meaning they never actually exercised the phase 5/7 per-connection routing this whole
  effort built, since each was the only client of its own daemon. Both options are now excluded from the
  pipe-key hash (`DaemonPipeName.GetServerArgumentsForPipeKey`, handling both the two-token and inline
  `--option=value` forms, mirroring `ConnectionHandshake.FromServerArguments`'s own parsing) while still being
  sent through the handshake as before. Verified by new `DaemonPipeNameTests` cases confirming pipe names are
  unaffected by either option's value (both argument forms) while still differing on other, still-daemon-wide
  arguments positioned around them.
- **The pipe-key exclusion above reopened a leak in both fields' *read* side.** Once
  `--extensionLogDirectory`/`--sourceGeneratorExecutionPreference` stopped splitting clients into separate
  daemons, a second finding surfaced: `ConnectionHandshakeRegistry.Current` returned the same sentinel
  (`ConnectionHandshake.Empty`, all fields null) both when a connection had no handshake at all
  (single-server mode) *and* when a real daemon connection's handshake simply omitted a field -- making the
  two indistinguishable to every consumer. Both consumers fell back to the daemon-wide value in the second
  case, which now reflects an unrelated client's explicit choice (whichever client happened to launch the
  daemon), not a sensible default:
  - `RunTestsHandler` wrote a later client's `runTests` log into the *launching* client's
    `ExtensionLogDirectory` whenever the later client omitted its own.
  - The `SourceGeneratorExecutionPreference` override write only ran when the handshake's field parsed
    successfully, so an omitted/unparseable value installed *no* override, silently falling through to
    whatever `Program.cs` wrote into the shared `IGlobalOptionService` from the launching client's explicit
    request -- not the command-line default (`Automatic`) a client omitting the option would reasonably expect.
  Fixed by making `ConnectionHandshakeRegistry.Current` return `ConnectionHandshake?` (nullable): `null` means
  "no handshake, fall back to daemon-wide configuration" (still correct for single-server mode); a non-null
  handshake with a null field means "this connection explicitly has no value," which callers must not paper
  over with the daemon-wide value. `RunTestsHandler` now uses `null` only when there's no handshake at all.
  The `SourceGeneratorExecutionPreference` write now *always* installs an override for a daemon connection
  (falling back to `SourceGeneratorExecutionPreference.Automatic`, matching `LanguageServerCommandLine`'s own
  default, when the handshake's value is missing or unparseable) instead of skipping the write. Verified by
  new `ConnectionHandshakeRegistryTests` (the nullable-vs-sentinel distinction, isolated from the daemon) and
  updated `SourceGeneratorExecutionPreferenceRoutingTests` (a daemon-launching client with an explicit,
  non-default preference, to distinguish "fell back to the shared value" from "fell back to the command-line
  default" -- both looked like plausible-but-different answers unless pinned apart like this).
- **`EditorConnection.CreateAsync`'s pipe connect had no timeout.** In daemon mode, `Program.cs` already opens
  a connection to the shared daemon before calling this, so an editor that launched the thin client but never
  created its own listening pipe (crashed, or `--pipe` pointed at the wrong name) left this waiting forever --
  with the daemon connection opened moments earlier still counted as active the whole time, permanently
  blocking that daemon's idle keepalive from ever starting. Fixed with a 30-second bound on the pipe connect
  (`NamedPipeClientStream.ConnectAsync(int)`, which throws `TimeoutException` on expiry); `Program.cs`'s
  existing `catch` for `TimeoutException` and the `using` around the daemon connection already handle
  reporting and cleanup correctly once this throws instead of hanging, no changes needed there.
- **Connection-scoped option overrides never propagated into already-loaded workspaces.**
  `ConnectionScopedOptionOverrides.SetOverrides` (phase 4) intentionally never touches the shared
  `IGlobalOptionService`, so it never raises the `OptionChanged` event `SolutionAnalyzerConfigOptionsUpdater`
  listens for to keep `Solution.FallbackAnalyzerOptions` in sync. A daemon connection's editorconfig-backed
  option change (naming style, diagnostic scope, code-style options, ...) from `workspace/didChangeConfiguration`
  was therefore readable via `GetConnectionScopedOption` but silently never reached already-loaded workspaces'
  analyzer configuration -- diagnostics stayed computed against the stale value until the workspace was
  recreated. Fixed by extracting `SolutionAnalyzerConfigOptionsUpdater`'s solution-transform logic into a
  reusable `ApplyChangedOptionsIfRelevant(Workspace, IReadOnlyList<KeyValuePair<OptionKey2, object?>>)`, and
  calling it directly from `DidChangeConfigurationNotificationHandler.RefreshOptionsAsync` for just this
  connection's own workspaces (enumerated via its `LspWorkspaceRegistrationService`) right after installing
  the override. Verified by a new `DidChangeConfigurationNotificationHandlerTest` case confirming
  `Solution.FallbackAnalyzerOptions` reflects the client's new value immediately after the notification, not
  just `GetConnectionScopedOption`'s own read path. **Correction from a follow-up finding below:**
  `LspWorkspaceRegistrationService` is *not* purely a per-server view, as first assumed here -- see the next
  entry.
- **The fix above wasn't scoped correctly: `LspWorkspaceRegistrationService.GetAllRegistrations()` includes
  the one workspace kind that genuinely is shared process-wide.** `LanguageServerLspWorkspaceRegistrationEventListener`
  deliberately shares `WorkspaceKind.MetadataAsSource` across every daemon connection in the process (its own
  doc comment says so explicitly), while only Host/MiscellaneousFiles are registered per-server. The
  `ApplyChangedOptionsIfRelevant` loop above, added to fix the previous finding, iterated *all* of
  `GetAllRegistrations()` with no such distinction -- so it re-introduced exactly the cross-connection leak
  the whole per-connection-isolation effort exists to prevent, just for metadata-as-source documents instead
  of the option value itself. Fixed by skipping `WorkspaceKind.MetadataAsSource` in that loop. **Verification
  note:** a test written for this (opening a metadata-as-source document, then asserting its
  `Solution.FallbackAnalyzerOptions` don't change) passed identically with and without the fix -- i.e. it
  didn't actually exercise the bug, for a reason not tracked down within this session's time budget (most
  likely the shared workspace's `FallbackAnalyzerOptions` dictionary not yet containing a `"csharp"` entry by
  the point the test reads it, so both the buggy and fixed code paths are no-ops for that specific check). The
  test was removed rather than kept as false assurance; the fix itself stands on the listener's own explicit,
  unambiguous doc comment about which workspace kind is shared, not on this test.
- **`DaemonClientMutex`'s wait timeout didn't cover the daemon-launching client's own hold duration.**
  `DaemonClient.ConnectAsync` holds `clientMutex` for its *entire* `using` block, including the connecting
  client's full wait for a newly-launched daemon to become ready (`s_newDaemonConnectTimeout`, i.e.
  `DaemonBootstrap.ReadyTimeout`, 60s) -- not just the brief "check server, launch if absent" decision. A
  second client racing to connect during that same cold start waited only `s_daemonMutexTimeout` (20s,
  independent of and shorter than the 60s the first client might legitimately hold the mutex) before giving
  up and concluding the shared daemon was unreachable, launching its own redundant fallback server instead --
  defeating daemon sharing precisely when MEF composition is slowest and sharing matters most. Fixed by
  deriving `s_daemonMutexTimeout` from `s_newDaemonConnectTimeout` plus a fixed scheduling margin, instead of
  an independent literal.
- **The shared `WorkspaceKind.MetadataAsSource` workspace has a second, separate cross-connection leak beyond
  `FallbackAnalyzerOptions`, in `DecompilationMetadataAsSourceFileProvider` itself (confirmed real, not yet
  fixed).** `DecompilationMetadataAsSourceFileProvider` is a `[Shared]` (process-wide singleton) MEF part with
  its own `_keyToInformation` / `_generatedFilenameToInformation` caches, keyed by `UniqueDocumentKey`
  (assembly identity/metadata ID + language + symbol ID + `signaturesOnly` -- see
  `GetUniqueDocumentKeyAsync`). Neither the key nor the cached `MetadataAsSourceGeneratedFileInfo` includes the
  requesting connection or `sourceWorkspace` identity: `_keyToInformation.GetOrAdd(infoKey, _ => new
  MetadataAsSourceGeneratedFileInfo(tempPath, sourceWorkspace, sourceProject, ...))` captures whichever
  connection's `sourceWorkspace`/`sourceProject` happened to request that symbol *first*, and every later
  request for the same symbol from a *different* connection reuses that cached `fileInfo` -- including its
  captured first-connection workspace/project. So a second daemon client navigating to the same framework
  symbol resolves its metadata-as-source document against the first client's project, and can fail outright
  once that first connection's workspace is disposed, or silently return results from the wrong solution.
  This is independent of (and not fixed by) the `WorkspaceKind.MetadataAsSource` skip added above for
  `FallbackAnalyzerOptions` propagation -- that fix only addressed the options-sync path; this is the
  decompilation cache itself. **Not fixed in this PR**: a correct fix means making
  `DecompilationMetadataAsSourceFileProvider`'s cache connection-aware (e.g. keying on `sourceWorkspace`
  identity in addition to symbol/assembly), which is a real behavioral change to a process-wide singleton
  service shared by design (see `LanguageServerLspWorkspaceRegistrationEventListener`'s doc comment) and needs
  its own design/test pass rather than a rushed edit in a review-response cycle -- particularly around what
  happens to `_generatedFilenameToInformation`'s temp-file-path keying (physical files on disk are not
  naturally connection-scoped the way in-memory dictionaries are) if two connections generate the same symbol
  concurrently. Tracked as follow-up work; flagged explicitly in the PR review thread rather than closed out.

## The ambient-token ordering bug

Found by Codex while reviewing phase 5, but it affected phases 2 and 4 too for real (non-test) request
dispatch, undetected until then because every existing test set the ambient token directly in the test's own
call context rather than exercising the real connection-startup path.

**The bug**: `LanguageServerConnectionManager.TryStartServerAsync` called `DaemonConnectionContext.SetCurrent(server)`
*after* constructing `LanguageServerHost`, but `LanguageServerHost`'s constructor synchronously starts
`RequestExecutionQueue`'s background dispatch loop (via `RoslynLanguageServer`'s constructor calling
`AbstractLanguageServer.Initialize()` → `GetRequestExecutionQueue()` → `new RequestExecutionQueue(...)`, whose
own constructor kicks off `ProcessQueueAsync()`). That loop's `ExecutionContext` -- and therefore its ambient
`AsyncLocal` state -- is captured the moment it starts, permanently, for that loop and everything it later
schedules (including a `Task.Run` per dispatched request). Setting the token afterward, even "before `Start()`"
as the code claimed, was already too late: the loop had captured a null ambient value before `SetCurrent` ever
ran, so *every* request actually dispatched through the real queue saw no ambient connection, regardless of
what was set elsewhere at request time. `GlobalLogMessageLoggerTests` and `ConnectionScopedOptionOverridesTests`
didn't catch this because they call `DaemonConnectionContext.SetCurrent`/read routing decisions directly in the
test body, never through a request the real `RequestExecutionQueue` dispatches.

**The fix**: the ambient token now has to exist *before* `LanguageServerHost` is constructed, not merely before
`Start()`. `TryStartServerAsync` mints a lightweight marker object and calls
`AmbientConnectionToken.SetCurrent(new object())` before `new LanguageServerHost(...)`, so the queue's
background loop captures the right value from the start. `DaemonConnectionContext.Associate(server)` (called
right after construction succeeds) then maps that token to the constructed `LanguageServerHost`, so `Current`
still resolves to a `LanguageServerHost?` for existing consumers. `ConnectionHandshakeRegistry` (phase 5) keys
directly by the ambient token's identity rather than going through that resolution, for the same reason
`ConnectionScopedOptionOverrides` (phase 4) already does. `DaemonConnectionContext.SetCurrent(LanguageServerHost)`
is kept as a test-only convenience for tests that want to simulate "the current connection is this server"
directly without the early-token dance.

`AsyncLocalPropagationTests` gained two tests (`ValueSetAfterAsyncWorkAlreadyStarted_IsNotObservedByThatWork`,
`ValueSetBeforeAsyncWorkStarted_IsObservedByThatWork`) that isolate and document this exact ordering property
with a minimal `Task.Run`-based reproduction, independent of the full daemon/MEF stack.

**Also found during the same re-audit** (not the ordering bug, a separate miss from phase 3's original
call-site audit): `CodeLensHandler.GetCodeLensAsync`, `AbstractFormatDocumentHandlerBase.GetTextEditsAsync`,
`ProtocolConversions.ConvertDiagnostic` (fading options), `CompletionHandler.GetCompletionListAsync`
(`MaxCompletionListSize`), and `TaskListOptionsStorage.GetTaskListOptions` all read `IGlobalOptionService`
directly instead of through the phase 4 facade. Migrated to `GetConnectionScopedOption`; `ClassificationOptionsStorage.cs`
was re-confirmed as a correct exclusion (it's written against `IOptionsReader`, not `IGlobalOptionService`).

## Decisions

These were open questions in an earlier draft; resolved before starting implementation so phase 1 isn't
blocked on them.

- **Telemetry session isolation: won't fix, by design.** Telemetry answers "how is this tool used/crashing in
  aggregate," not "what did this specific workspace do." Two clients sharing a daemon landing in the same
  telemetry session is an accuracy loss (some events get attributed to the session that happened to launch
  the daemon), not a correctness or privacy problem — nothing about it exposes one client's *content* to
  another the way the option-bleed and log-broadcast symptoms do. Phase 6 is dropped entirely, not deferred.
  This also removes the one open question that had no clean answer, so the remaining scope (phases 1–5, 7) is
  fully options + logs, both of which cause behavior a user would actually notice as wrong.
- **Scope of the phase 3 option-read audit: bounded to the LSP request-handling path.** "Every option read
  anywhere in the IDE layer" is unbounded and is what made phase 3 look risky. Scope it instead to option
  reads reachable while a `LanguageServerHost` is handling a specific client's request or notification
  (formatting, completion, and similar per-request work). Background/global work not attributable to any one
  client (e.g. a workspace-event-triggered source-generator run with no request in flight) stays on the
  shared `IGlobalOptionService` instance untouched — there's no other client for it to leak into, so it's not
  in scope for isolation. This turns phase 3 into a grep from the LSP handler dispatch entry points, not a
  whole-codebase audit.
- **`AsyncLocal` propagation: verified, not assumed.** `AsyncLocalPropagationTests`
  (`src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer.UnitTests/Daemon/AsyncLocalPropagationTests.cs`)
  confirms the value survives a direct `await`, `ConfigureAwait(false)`, `Task.Run`, and — the one that
  actually matters for this design — a real `StreamJsonRpc` request/response round trip through
  `JsonRpc.InvokeAsync`/`StartListening`, i.e. the same dispatch mechanism `LanguageServerHost` uses for real
  LSP requests. Also confirmed the isolation property the whole design depends on: concurrent `Task.Run` calls
  each see only their own value (no cross-talk), and a value set inside a child `Task.Run` never leaks back to
  the caller. All 6 cases pass. `AsyncLocal` is a viable mechanism for the ambient-context primitive; phase 2
  can proceed without this being an open risk.
