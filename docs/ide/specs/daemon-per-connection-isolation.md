# Daemon-mode per-connection isolation

## Status

Design write-up, not yet implemented. Tracked in
[GoldMikeDev/roslyn#9](https://github.com/GoldMikeDev/roslyn/issues/9). Flagged across three
separate Codex review rounds on PR #3 (the daemon-mode `roslyn-language-server` thin client).

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

### 3. Global log broadcast (`GlobalLogMessageLogger`)

`GlobalLogMessageLogger` (`src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Logging/GlobalLogMessageLogger.cs`)
implements the MEF-exported process-global `ILogger` by iterating every started server
(`LanguageServerConnectionManager.GetStartedServers()`) and forwarding each log entry to every one of them
over `window/logMessage`. Because some of what logs through this path is workspace-specific (e.g.
`VSCodeExtensionAssemblyAnalyzerLoader.LoadFromPath` logs a full analyzer path), one client's activity
becomes visible in every other connected client's log output. The root cause is that the operations being
logged (extension loading, MEF composition) are themselves process-global under the current
single-composition design, so there's no per-connection context available at the log call site to route
through even in principle.

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

1. **Build the ambient-context primitive in isolation**, with unit tests for the propagation semantics
   (does it survive `Task.Run`? Nested connections? Disposal?) but no consumers wired up yet. No user-visible
   behavior change.
2. **Wire it into `GlobalLogMessageLogger`** as the first consumer — the smallest full vertical slice, and
   the one with the least entanglement with other shared singletons (no facade needed, just a routing
   change).
3. **Audit every `IGlobalOptionService` call site** reachable from daemon-mode code before touching anything
   — this is the expensive, non-optional step. A facade is easy to write; verifying nothing still calls the
   raw shared singleton directly and silently bypasses it is the actual work, and doing it half-way would
   "look fixed without being fixed" (the same failure mode flagged in the original Codex finding).
4. **`IGlobalOptionService` facade + `DidChangeConfigurationNotificationHandlerFactory` rewire**, once step 3
   has produced a complete call-site inventory.
5. **`ExtensionLogDirectory` per-connection routing**, independent of steps 3–4 and can happen in parallel.
6. **`TelemetryLevel`/`SessionId`/`RoslynLogger`**: explicitly out of scope for this design until someone
   decides whether per-connection telemetry sessions are even a desired product behavior (vs. e.g. keeping
   telemetry daemon-global by design, which may be an acceptable simplification telemetry infrastructure
   elsewhere in Visual Studio already assumes). Needs its own decision before design work, not lumped in
   here.
7. **`SourceGeneratorExecutionPreference`**: falls out of step 4 for free once the option facade exists,
   since it's already routed through `IGlobalOptionService`.

## Open questions

- Is per-connection telemetry session isolation actually desired, or is "one telemetry session per daemon
  process" an acceptable, deliberate simplification? This blocks phase 6 and should be settled before any
  code is written for it.
- Should `IGlobalOptionService` reads/writes from **outside** LSP request handling (e.g. background analysis
  work queued on a workspace) attribute to a connection at all, or are they inherently daemon-global?  This
  affects how much of phase 3's audit surface actually needs facade routing vs. can stay on the shared
  instance.
- Does `AsyncLocal` propagation actually survive every relevant transition in this codebase (thread pool
  hops via `ConfigureAwait(false)`, `Task.Run`, JSON-RPC's own dispatch)? Needs to be verified with a test
  before phase 2, not assumed.
