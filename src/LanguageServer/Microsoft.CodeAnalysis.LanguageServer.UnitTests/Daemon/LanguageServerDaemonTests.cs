// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Microsoft.CodeAnalysis.BrokeredServices;
using Microsoft.CodeAnalysis.LanguageServer.BrokeredServices;
using Microsoft.CodeAnalysis.LanguageServer.BrokeredServices.Services.BrokeredServiceBridgeManifest;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;
using Microsoft.CodeAnalysis.Text;
using Roslyn.LanguageServer.Protocol;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class LanguageServerDaemonTests(ITestOutputHelper testOutputHelper) : AbstractLanguageServerHostTests(testOutputHelper)
{
    /// <summary>
    /// Regression coverage for a Codex finding on PR #3: a connection whose handshake processing fails after
    /// <see cref="LanguageServer.LanguageServerHost"/> has already been constructed (e.g.
    /// <see cref="System.IO.Directory.CreateDirectory(string)"/> throwing for a client-supplied
    /// <c>ExtensionLogDirectory</c> whose permissions changed) previously wasn't cleaned up the same way a
    /// construction failure is: the server was never aborted and <c>connection.Resource</c> was never disposed,
    /// so <see cref="NamedPipeDaemonConnectionSource"/>'s already-incremented active-connection count never
    /// returned to zero and the daemon's idle keepalive could never re-arm, keeping it alive indefinitely.
    /// </summary>
    [Fact]
    public async Task HandshakeProcessingFailure_CleansUpConnection_AndIdleTimeoutStillElapses()
    {
        await using var daemon = await CreateDaemonServerAsync(keepAlive: TimeSpan.FromMilliseconds(200));

        // Directory.CreateDirectory throws when an existing regular file occupies a path segment.
        var blockingFile = TempRoot.CreateFile();
        var uncreatableDirectory = Path.Combine(blockingFile.Path, "subdir");

        await Assert.ThrowsAnyAsync<Exception>(() => daemon.CreateClientAsync(
            handshake: new ConnectionHandshake(ExtensionLogDirectory: uncreatableDirectory, SourceGeneratorExecutionPreference: null)));

        // Before the fix, this would never happen: the leaked active-connection count meant the idle timer
        // never got a chance to elapse, regardless of how long the test waited.
        await WaitForConditionAsync(() => !daemon.IsRunning);
    }

    [Fact]
    public async Task Daemon_SecondInstanceOnSamePipe_TryCreateReturnsFalse()
    {
        await using var daemon = await CreateDaemonServerAsync();

        // The daemon holds the server mutex for its pipe...
        Assert.True(daemon.IsRunning);

        // ...so a second daemon on the same pipe must observe it and fail to become the daemon.
        Assert.False(NamedPipeDaemonConnectionSource.TryCreate(
            daemon.PipeName, Timeout.InfiniteTimeSpan, LoggerFactory.CreateLogger("Daemon2"), out var secondSource));
        Assert.Null(secondSource);
    }

    [Fact]
    public async Task Daemon_AcceptsClientAndInitializes()
    {
        await using var daemon = await CreateDaemonServerAsync();
        await using var client = await daemon.CreateClientAsync();
        Assert.NotNull(client.ServerCapabilities);
    }

    [Fact]
    public async Task Daemon_ClientDisconnect_WithInfiniteKeepAlive_StaysAlive()
    {
        await using var daemon = await CreateDaemonServerAsync();

        await using (var client = await daemon.CreateClientAsync())
        {
            Assert.NotNull(client.ServerCapabilities);
        }

        // After the only client disconnects, its per-client server tears down...
        await WaitForConditionAsync(() => daemon.GetStartedServers().IsEmpty);

        // ...but with an infinite keepalive the daemon itself keeps running.
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.False(daemon.DaemonExitTask.IsCompleted);
    }

    // Two clients connect to the same daemon concurrently. Each gets its own independent server, so both initialize
    // successfully and neither connection affects the other or the daemon.
    [Fact]
    public async Task Daemon_SecondConcurrentConnection_IsIsolatedFromDaemonAndFirstClient()
    {
        await using var daemon = await CreateDaemonServerAsync();

        await using var first = await daemon.CreateClientAsync();
        Assert.NotNull(first.ServerCapabilities);

        await using var second = await daemon.CreateClientAsync();
        Assert.NotNull(second.ServerCapabilities);

        // Both clients have their own independent server, and the daemon stays up.
        Assert.False(daemon.DaemonExitTask.IsCompleted);
        Assert.Equal(2, daemon.GetStartedServers().Length);
    }

    // Each connected client gets its own server with its own Host workspace. A project loaded into one server's
    // workspace must not be visible to the other server, and each server's registration service must track only
    // its own workspaces.
    [Fact]
    public async Task Daemon_EachServerLoadsProjectsInItsOwnWorkspace()
    {
        await using var daemon = await CreateDaemonServerAsync();
        await using var first = await daemon.CreateClientAsync();
        await using var second = await daemon.CreateClientAsync();

        var factory1 = first.GetRequiredLspService<LanguageServerWorkspaceFactory>();
        var factory2 = second.GetRequiredLspService<LanguageServerWorkspaceFactory>();

        // Each server has its own, distinct Host workspace instance.
        Assert.NotSame(factory1.HostWorkspace, factory2.HostWorkspace);

        // Load a distinct project into each server's Host workspace.
        LoadProject(factory1, "ProjectOne");
        LoadProject(factory2, "ProjectTwo");

        // Each server only sees its own project; loading into one server does not leak into the other.
        Assert.Contains(factory1.HostWorkspace.CurrentSolution.Projects, p => p.Name == "ProjectOne");
        Assert.DoesNotContain(factory1.HostWorkspace.CurrentSolution.Projects, p => p.Name == "ProjectTwo");
        Assert.Contains(factory2.HostWorkspace.CurrentSolution.Projects, p => p.Name == "ProjectTwo");
        Assert.DoesNotContain(factory2.HostWorkspace.CurrentSolution.Projects, p => p.Name == "ProjectOne");

        // Each server's registration service tracks its own Host workspace and not the other server's.
        var registrations1 = first.GetRequiredLspService<LspWorkspaceRegistrationService>().GetAllRegistrations();
        var registrations2 = second.GetRequiredLspService<LspWorkspaceRegistrationService>().GetAllRegistrations();
        Assert.Contains(factory1.HostWorkspace, registrations1);
        Assert.DoesNotContain(factory2.HostWorkspace, registrations1);
        Assert.Contains(factory2.HostWorkspace, registrations2);
        Assert.DoesNotContain(factory1.HostWorkspace, registrations2);
    }

    // Regression coverage for GoldMikeDev/roslyn#9: IServiceBrokerProvider used to be exported as a directly
    // [Shared] IWorkspaceService, so every daemon connection's Workspace (all built from the same process-wide
    // ExportProvider) resolved the exact same singleton instance. ServiceBrokerFactory.CreateAsync calls
    // SetContainer on it once per connection, and SetContainer throws if already completed -- so the second
    // connection's own service-broker setup crashed outright, not just misrouted brokered-service traffic.
    // ServiceBrokerProviderFactory now hands out a fresh instance per Workspace instead.
    [Fact]
    public async Task Daemon_EachServerGetsItsOwnServiceBrokerProvider()
    {
        await using var daemon = await CreateDaemonServerAsync();
        await using var first = await daemon.CreateClientAsync();
        await using var second = await daemon.CreateClientAsync();

        var factory1 = first.GetRequiredLspService<LanguageServerWorkspaceFactory>();
        var factory2 = second.GetRequiredLspService<LanguageServerWorkspaceFactory>();

        // IServiceBrokerProvider is keyed by AmbientConnectionToken.Current, established for the duration of
        // real request dispatch -- not present on this thread by default, so establish each connection's real
        // token (the same one its own request dispatch loop actually saw) before resolving from its
        // workspaces, the same way ConnectionScopedOptionOverridesTests verifies the equivalent Roslyn-LSP-side
        // facade.
        var token1 = DaemonConnectionContext.GetAmbientTokenForTesting(first.DaemonServer);
        var token2 = DaemonConnectionContext.GetAmbientTokenForTesting(second.DaemonServer);

        AmbientConnectionToken.SetCurrent(token1!);
        var provider1 = factory1.HostWorkspace.Services.GetRequiredService<IServiceBrokerProvider>();

        // ...but a single connection's *other* workspaces (MiscellaneousFiles, alongside Host) must resolve
        // to that same connection's provider, not a separate uninitialized one: serviceBroker/connect only
        // ever calls SetContainer once, on requestContext.Workspace (the Host workspace) -- resolving
        // IServiceBrokerProvider from the MiscellaneousFiles workspace (e.g. loose-file navigation reaching
        // PdbSourceDocumentMetadataAsSourceFileProvider's ISourceLinkService lookup) must see the same
        // completed container rather than hang forever on a never-completed one.
        var miscProvider1 = factory1.MiscellaneousFilesWorkspaceProjectFactory.Workspace.Services.GetRequiredService<IServiceBrokerProvider>();
        Assert.Same(provider1, miscProvider1);

        AmbientConnectionToken.SetCurrent(token2!);
        var provider2 = factory2.HostWorkspace.Services.GetRequiredService<IServiceBrokerProvider>();

        // Each connection's workspace must get its own provider instance.
        Assert.NotSame(provider1, provider2);

        // ...so setting up the service broker for both connections' workspaces, in the order a real daemon
        // would (each connection's ServiceBrokerFactory.CreateAsync runs independently), does not throw for
        // the second connection the way it would if both resolved the same shared instance.
        var serviceBrokerFactory1 = first.GetRequiredLspService<ServiceBrokerFactory>();
        var serviceBrokerFactory2 = second.GetRequiredLspService<ServiceBrokerFactory>();

        AmbientConnectionToken.SetCurrent(token1!);
        var container1 = await serviceBrokerFactory1.CreateAsync(factory1.HostWorkspace);

        AmbientConnectionToken.SetCurrent(token2!);
        var container2 = await serviceBrokerFactory2.CreateAsync(factory2.HostWorkspace);

        Assert.NotSame(container1, container2);

        // Connecting via the Host workspace also satisfies the MiscellaneousFiles workspace's own
        // IServiceBroker (proving they really do share one provider, not just one that happens to look alike).
        AmbientConnectionToken.SetCurrent(token1!);
        var miscServiceBroker = await miscProvider1.ServiceBroker.GetProxyAsync<IBrokeredServiceBridgeManifest>(
            BrokeredServiceBridgeManifest.ServiceDescriptor, cancellationToken: CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(miscServiceBroker);
    }

    // If one client's server faults (e.g. the client process crashes and abruptly drops its connection), the daemon
    // tears down only that server. The daemon stays alive, the other client's server is untouched and still
    // functional, and the daemon keeps accepting new clients.
    [Fact]
    public async Task Daemon_OneClientCrashing_DoesNotAffectDaemonOrOtherClients()
    {
        await using var daemon = await CreateDaemonServerAsync();
        await using var survivor = await daemon.CreateClientAsync();
        await using var crashing = await daemon.CreateClientAsync();

        Assert.Equal(2, daemon.GetStartedServers().Length);

        // Simulate the second client process crashing: abruptly drop its transport with no clean LSP shutdown.
        await crashing.CrashAsync();

        // The daemon tears down only the crashed client's server and keeps running.
        await WaitForConditionAsync(() => daemon.GetStartedServers().Length == 1);
        Assert.False(daemon.DaemonExitTask.IsCompleted);

        // Load a document into the surviving server, then issue a real LSP request through the surviving client's
        // JSON-RPC connection. This verifies that the crash did not just leave the server object alive, but that its
        // request loop and response path are still functional.
        var survivorFactory = survivor.GetRequiredLspService<LanguageServerWorkspaceFactory>();
        var survivorDocumentUri = LoadProjectWithDocument(survivorFactory, "Survivor");
        var hover = await survivor.ExecuteRequestAsync<HoverParams, Hover>(
            Methods.TextDocumentHoverName,
            new HoverParams
            {
                TextDocument = new TextDocumentIdentifier { DocumentUri = survivorDocumentUri },
                Position = new Position(0, 6),
            },
            CancellationToken.None);

        Assert.NotNull(hover);
        Assert.Contains("Survivor", hover.Contents.Fourth.Value);

        // And the daemon still accepts brand-new clients.
        await using var late = await daemon.CreateClientAsync();
        Assert.NotNull(late.ServerCapabilities);
    }

    [Fact]
    public async Task Daemon_ShortKeepalive_DoesNotFireBeforeFirstConnectionOrWhileClientIsActive()
    {
        await using var daemon = await CreateDaemonServerAsync(keepAlive: TimeSpan.FromSeconds(1));

        // The configured keepalive is shorter than this delay, but the daemon uses its separate initial-connection
        // timeout after signaling readiness.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(daemon.DaemonExitTask.IsCompleted);

        await using (var client = await daemon.CreateClientAsync())
        {
            Assert.NotNull(client.ServerCapabilities);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(daemon.DaemonExitTask.IsCompleted);
        }

        await daemon.DaemonExitTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(daemon.IsRunning);
    }

    [Fact]
    public async Task Daemon_AcceptedConnection_WinsRaceWithInitialTimeout()
    {
        await using var daemon = await CreateDaemonServerAsync(
            keepAlive: TimeSpan.Zero,
            initialConnectionTimeout: Timeout.InfiniteTimeSpan);
        using var connectionAccepted = new ManualResetEventSlim();
        using var releaseConnection = new ManualResetEventSlim();
        var sourceAccessor = daemon.GetConnectionSourceTestAccessor();
        sourceAccessor.OnConnectionAccepted = () =>
        {
            connectionAccepted.Set();
            releaseConnection.Wait();
        };

        try
        {
            var clientTask = daemon.CreateClientAsync();
            connectionAccepted.Wait();

            // By the time OnConnectionAccepted's callback runs, the connection has already been recorded as
            // active (ConnectionIdleTimeout.OpenConnection runs immediately after WaitForConnectionAsync
            // succeeds, before elevation/handshake validation -- see NamedPipeDaemonConnectionSource's
            // "stay one accept ahead" background accept loop). So triggering the timeout here races against an
            // *already-open* connection, not merely an *accepted-but-not-yet-open* one: it must not cause the
            // daemon to shut down while this connection is still mid-handshake, even though the raw timeout
            // token this triggers does get cancelled.
            sourceAccessor.TriggerTimeout();

            releaseConnection.Set();
            await using (var client = await clientTask)
            {
                Assert.NotNull(client.ServerCapabilities);
                Assert.False(daemon.DaemonExitTask.IsCompleted);
            }

            await daemon.DaemonExitTask;
            Assert.False(daemon.IsRunning);
        }
        finally
        {
            sourceAccessor.OnConnectionAccepted = null;
            releaseConnection.Set();
        }
    }

    [Fact]
    public async Task Daemon_ZeroKeepalive_ExitsImmediatelyAfterFirstClientDisconnects()
    {
        await using var daemon = await CreateDaemonServerAsync(keepAlive: TimeSpan.Zero);

        await using (var client = await daemon.CreateClientAsync())
        {
            Assert.NotNull(client.ServerCapabilities);
        }

        await daemon.DaemonExitTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(daemon.IsRunning);
    }

    [Fact]
    public async Task Daemon_StartupFailure_DaemonSurvivesAndContinuesAccepting()
    {
        await using var daemon = await CreateDaemonServerAsync();

        var startAttempts = 0;
        daemon.GetConnectionManagerTestAccessor().OnBeforeStartServer = () =>
        {
            if (Interlocked.Increment(ref startAttempts) == 1)
                throw new InvalidOperationException("Simulated startup failure for test.");
        };

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var failedClient = await daemon.CreateClientAsync();
        });

        await WaitForConditionAsync(() => startAttempts >= 1);
        Assert.False(daemon.DaemonExitTask.IsCompleted);

        daemon.GetConnectionManagerTestAccessor().OnBeforeStartServer = null;
        await using var client = await daemon.CreateClientAsync();
        Assert.NotNull(client.ServerCapabilities);
    }

    [Fact]
    public async Task Daemon_SlowServerStartup_DoesNotBlockAcceptingNextConnection()
    {
        await using var daemon = await CreateDaemonServerAsync();
        using var firstStartEntered = new ManualResetEventSlim();
        using var releaseFirstStart = new ManualResetEventSlim();

        var startAttempts = 0;
        daemon.GetConnectionManagerTestAccessor().OnBeforeStartServer = () =>
        {
            if (Interlocked.Increment(ref startAttempts) == 1)
            {
                firstStartEntered.Set();
                releaseFirstStart.Wait();
            }
        };

        using var firstClient = NamedPipeUtil.CreateClient(
            serverName: ".",
            daemon.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await firstClient.ConnectAsync(timeout: 30_000);

            // NamedPipeDaemonConnectionSource.AcceptConnectionsAsync reads a handshake from every connection
            // before accepting it (OnBeforeStartServer runs after that); a real client always sends one
            // immediately after connecting (see DaemonClient.ConnectPipe), so this raw test client must too.
            await ConnectionHandshake.Empty.WriteAsync(firstClient, CancellationToken.None);

            Assert.True(firstStartEntered.Wait(TimeSpan.FromSeconds(30)));

            await using var secondClient = await daemon.CreateClientAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(secondClient.ServerCapabilities);
        }
        finally
        {
            daemon.GetConnectionManagerTestAccessor().OnBeforeStartServer = null;
            releaseFirstStart.Set();
        }
    }

    // Regression coverage for a Codex finding on PR #3: AcceptConnectionsAsync used to be fully sequential --
    // it created one listening pipe instance, accepted a client on it, then (still holding that same instance,
    // before creating the *next* listening instance) ran the elevation check and handshake read for that client.
    // A client that connects but withholds its handshake bytes (stalled, slow, or malicious) used to block every
    // other client from connecting until that read finished or timed out. The fix keeps a listening instance
    // available essentially at all times by starting the next accept in the background as soon as the current
    // one completes, before doing the current connection's elevation check/handshake read.
    [Fact]
    public async Task Daemon_SlowClientHandshake_DoesNotBlockAcceptingNextConnection()
    {
        await using var daemon = await CreateDaemonServerAsync();

        // Connect a client but never send its handshake -- simulates a stalled/slow/malicious client that has
        // connected but is withholding (or trickling) its handshake bytes.
        using var stalledClient = NamedPipeUtil.CreateClient(
            serverName: ".",
            daemon.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await stalledClient.ConnectAsync(timeout: 30_000);

        // A second, well-behaved client must still be able to connect and complete its handshake promptly,
        // without waiting for the stalled client's handshake read to time out (s_handshakeTimeout is 10 seconds).
        await using var second = await daemon.CreateClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(second.ServerCapabilities);
    }

    private static void LoadProject(LanguageServerWorkspaceFactory workspaceFactory, string projectName)
        => workspaceFactory.HostWorkspace.SetCurrentSolution(
            solution => solution.AddProject(projectName, projectName, LanguageNames.CSharp).Solution,
            WorkspaceChangeKind.ProjectAdded);

    private static DocumentUri LoadProjectWithDocument(LanguageServerWorkspaceFactory workspaceFactory, string typeName)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cs");
        workspaceFactory.HostWorkspace.SetCurrentSolution(
            solution => solution
                .AddProject(typeName, typeName, LanguageNames.CSharp)
                .AddDocument(Path.GetFileName(filePath), SourceText.From($"class {typeName} {{ }}"), filePath: filePath)
                .Project.Solution,
            WorkspaceChangeKind.ProjectAdded);

        return ProtocolConversions.CreateAbsoluteDocumentUri(filePath);
    }
}
