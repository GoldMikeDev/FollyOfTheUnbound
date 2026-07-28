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
