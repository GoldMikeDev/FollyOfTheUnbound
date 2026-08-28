---
coverage: IDE-layer (src/{Analyzers,CodeStyle,Features,Workspaces,EditorFeatures,VisualStudio,LanguageServer}) test base classes & authoring conventions
---

# IDE — Testing

Layer-specific test guidance for the IDE/Workspaces stack under
`src/{Features,Analyzers,EditorFeatures,...}`.

## Test workspace (MEF-dependent tests)

```csharp
[UseExportProvider]
public class MyTests
{
    [Fact]
    public async Task TestSomething()
    {
        var workspace = EditorTestWorkspace.CreateCSharp("class C { }");
        var document = workspace.Documents.Single();
    }
}
```

## Language Server tests (`src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer.UnitTests`)

`AbstractLanguageServerHostTests` (`Utilities/AbstractLanguageServerHostTests.cs`) is a two-mode base class:

- **Single-server mode** (`CreateLanguageServerAsync`): one in-memory `LanguageServerHost` wired to
  in-process `System.IO.Pipelines` pipes (`SingleServerTestLspServer`). Use this for ordinary LSP
  request/notification tests that don't care about daemon-mode multi-client behavior.
- **Daemon mode** (`CreateDaemonServerAsync`): a real `NamedPipeDaemonConnectionSource` +
  `LanguageServerConnectionManager` pair (`TestDaemon`), so multiple independent `LanguageServerHost`
  instances can be connected concurrently via `daemon.CreateClientAsync()` (returns a
  `DaemonClientTestLspServer`, one per connected client). Use this for anything exercising daemon lifecycle,
  multi-client isolation, or connection-scoped infrastructure (e.g. `GlobalLogMessageLoggerTests`,
  `LanguageServerDaemonTests`) — it gives you real registered `LanguageServerHost` instances and a real
  `LanguageServerConnectionManager` (exposed via `TestDaemon.ConnectionManager` for tests that need to
  construct daemon-wide infrastructure directly, like a `GlobalLogMessageLogger`) instead of requiring an ad
  hoc host construction. `TestDaemon.GetStartedServers()` / `daemon.DaemonExitTask` /
  `GetConnectionManagerTestAccessor()` cover the common lifecycle assertions (server count, daemon exit,
  simulating a startup failure).

Both modes share the same `LogMessageReceived` event and `window/logMessage` capture on the test LSP client,
and the same file-watcher-release / MEF composition setup from the base class.

## Language Server process-host tests (`src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer.ProcessHost.UnitTests`)

A separate, heavier base from the in-memory one above: `AbstractLanguageServerClientTests`
(`Utilities/AbstractLanguageServerClientTests.cs`) actually launches the real `roslyn-language-server` thin
client and `Microsoft.CodeAnalysis.LanguageServer` as out-of-process executables (deployed into a `RoslynLSP`
test-output subdirectory — see the project's `_CopyLanguageServerFiles`/`_CopyThinClientFiles` MSBuild
targets), rather than wiring an in-memory `LanguageServerHost` to in-process pipes. Use this layer for
anything that needs the real process boundary: daemon bootstrap/orphaning, named-pipe vs. stdio transport
selection, or process-kill/crash lifecycle behavior that an in-memory host can't exercise.

`CreateLanguageServerAsync` selects one of four launch modes via `LspServerLaunchOptions`
(`DaemonMode` × `UseNamedPipe`), each routed to its own `TestLspClient` factory:

- `TestLspClient.CreateDaemonPipeAsync` — daemon mode over a named pipe.
- `TestLspClient.CreateDaemonStdioAsync` — daemon mode over stdio.
- `TestLspClient.CreateSingleServerPipeAsync` — single-server (non-daemon) mode over a named pipe.
- `TestLspClient.CreateSingleServerStdioAsync` — single-server mode over stdio.

See `Lifecycle/DaemonServerLifecycleTests.cs` and `Lifecycle/SingleServerLifecycleTests.cs` for the existing
lifecycle/cleanup conventions (e.g. asserting on process exit codes, killing one client/the daemon and
checking only the expected connections tear down) before adding another ad hoc process-launching test.

## ProjectData test projects use an xUnit v2 `TestContext` shim

**Affected area:** `src/LanguageServer/ProjectData/Microsoft.NET.ProjectData.Tests/XunitV2TestContext.cs`,
`src/LanguageServer/ProjectData/Microsoft.NET.ProjectData.Generators.Tests/XunitV2TestContext.cs`

Both `ProjectData` test projects still run on xUnit v2, but their test bodies use the xUnit v3-shaped
`TestContext.Current.CancellationToken` API. Each project carries its own identical, file-local
`internal static class TestContext` shim (`XunitV2TestContext.cs`) providing just
`Current.CancellationToken`, hardcoded to `CancellationToken.None` — it does **not** wire up real
runner-driven test cancellation. This is deliberate duplication (two separate internal types, not a
shared library), so:
- Don't mistake `TestContext.Current.CancellationToken` in these projects for actual runner
  cancellation — it never fires.
- A fix or removal of this shim must be applied to **both** copies, not just one.
- Remove both shims (and switch to real `TestContext` from the xUnit v3 SDK) only once/if these two
  projects are migrated to xUnit v3.

## Conventions

- Use `[UseExportProvider]` for any test that depends on MEF services (a missing
  attribute typically surfaces as an unrelated-looking failure).
- Analyzer tests inherit from
  `AbstractCSharpDiagnosticProviderBasedUserDiagnosticTest_NoEditor` (and the VB
  equivalents).
- For analyzer/code-fix tests, use `TestInRegularAndScriptAsync` /
  `TestMissingInRegularAndScriptAsync`.
- Prefer raw string literals (`"""..."""`) over verbatim strings (`@"..."`) for
  test source code.
- Keep tests focused — avoid unnecessary intermediary assertions; use `.Single()`
  rather than asserting a count then indexing.
