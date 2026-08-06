// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.Logging;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

/// <summary>
/// Phase 2 of the daemon per-connection isolation work (see
/// docs/ide/specs/daemon-per-connection-isolation.md): verifies <see cref="GlobalLogMessageLogger"/> routes to
/// just the ambient connection when one is set and still live, instead of broadcasting a call attributable to
/// one client's activity (e.g. its own extension loading) to every connected client's logs.
/// </summary>
public sealed class GlobalLogMessageLoggerTests(ITestOutputHelper testOutputHelper) : AbstractLanguageServerHostTests(testOutputHelper)
{
    [Fact]
    public async Task NoAmbientConnection_TargetsAllStartedServers()
    {
        await using var daemon = await CreateDaemonServerAsync();
        await using var first = await daemon.CreateClientAsync();
        await using var second = await daemon.CreateClientAsync();

        var logger = CreateLogger(daemon);

        // No ambient DaemonConnectionContext is set on this test's own call context, so this mirrors genuine
        // process-wide activity (e.g. logging before any client had connected) -- the pre-existing broadcast
        // behavior should still apply.
        var targets = logger.GetTestAccessor().GetTargetServers();

        Assert.Equal(2, targets.Length);
        Assert.Contains(daemon.GetStartedServers()[0], targets);
        Assert.Contains(daemon.GetStartedServers()[1], targets);
    }

    [Fact]
    public async Task AmbientConnectionSetAndLive_TargetsOnlyThatConnection()
    {
        await using var daemon = await CreateDaemonServerAsync();
        await using var first = await daemon.CreateClientAsync();
        await using var second = await daemon.CreateClientAsync();

        var logger = CreateLogger(daemon);
        var servers = daemon.GetStartedServers();
        var firstServer = servers[0];

        DaemonConnectionContext.SetCurrent(firstServer);
        var targets = logger.GetTestAccessor().GetTargetServers();

        var target = Assert.Single(targets);
        Assert.Same(firstServer, target);
        Assert.DoesNotContain(servers[1], targets);
    }

    [Fact]
    public async Task AmbientConnectionSetButNoLongerLive_AndNoOtherServers_TargetsNothing()
    {
        await using var daemon = await CreateDaemonServerAsync();
        var first = await daemon.CreateClientAsync();

        var logger = CreateLogger(daemon);
        var staleServer = daemon.GetStartedServers()[0];

        // Disconnect the client the ambient context points at, so its server is torn down and unregistered --
        // simulating a connection that ended between when the ambient context was captured and when the log
        // call actually runs. Not wrapped in `await using`: it's deliberately disposed here, mid-test, rather
        // than at scope exit.
        await first.DisposeAsync();
        await WaitForConditionAsync(() => daemon.GetStartedServers().IsEmpty);

        DaemonConnectionContext.SetCurrent(staleServer);
        var targets = logger.GetTestAccessor().GetTargetServers();

        // No longer a valid target -- falls through to the fallback logger (empty target list) rather than
        // the stale server, which the caller shouldn't be sending anything to anymore.
        Assert.Empty(targets);
    }

    [Fact]
    public async Task AmbientConnectionSetButNoLongerLive_WithOtherServersStillConnected_DoesNotLeakToThem()
    {
        // This is the case NoLongerLive_AndNoOtherServers can't distinguish from a genuinely ambientless call:
        // a log entry attributable to one, now-disconnected client must not be broadcast to a *different*,
        // still-live client's logs either -- it's not process-wide activity just because its own connection
        // ended, so the "no ambient connection" broadcast fallback must not apply here.
        await using var daemon = await CreateDaemonServerAsync();
        var first = await daemon.CreateClientAsync();
        await using var survivor = await daemon.CreateClientAsync();

        var logger = CreateLogger(daemon);
        var serversBeforeDisconnect = daemon.GetStartedServers();

        await first.DisposeAsync();
        await WaitForConditionAsync(() => daemon.GetStartedServers().Length == 1);
        var survivorServer = daemon.GetStartedServers().Single();
        var staleServer = serversBeforeDisconnect.Single(s => s != survivorServer);

        DaemonConnectionContext.SetCurrent(staleServer);
        var targets = logger.GetTestAccessor().GetTargetServers();

        Assert.Empty(targets);
        Assert.DoesNotContain(survivorServer, targets);
    }

    [Fact]
    public async Task AmbientTokenSetButNeverAssociated_WithOtherServersStillConnected_DoesNotLeakToThem()
    {
        // LanguageServerConnectionManager mints and makes the token ambient *before* constructing the
        // LanguageServerHost, associating it (via DaemonConnectionContext.Associate) only once construction
        // succeeds -- so there's a real window (construction, or handshake/registration processing before
        // that) where AmbientConnectionToken.Current is non-null but DaemonConnectionContext.Current still
        // resolves to null, because no server has been associated with that token yet. A log call or thrown
        // exception during that window must not be broadcast to *other*, unrelated live connections just
        // because DaemonConnectionContext.Current alone can't distinguish "never associated" from "no ambient
        // connection at all" -- the same leak AmbientConnectionSetButNoLongerLive_WithOtherServersStillConnected_DoesNotLeakToThem
        // guards against for a token whose connection *ended*, but for a token whose connection hasn't
        // *started* yet.
        await using var daemon = await CreateDaemonServerAsync();
        await using var survivor = await daemon.CreateClientAsync();

        var logger = CreateLogger(daemon);

        AmbientConnectionToken.SetCurrent(new object());
        var targets = logger.GetTestAccessor().GetTargetServers();

        Assert.Empty(targets);
        Assert.DoesNotContain(daemon.GetStartedServers().Single(), targets);
    }

    [Fact]
    public async Task ConcurrentConnections_DoNotObserveEachOthersAmbientContext()
    {
        // The routing decision relies on AsyncLocal isolation between concurrent flows (verified generically in
        // AsyncLocalPropagationTests); this exercises that specifically through GlobalLogMessageLogger's own
        // target-resolution logic, using the real per-connection ambient value LanguageServerConnectionManager
        // sets for each daemon connection it starts (not a value this test sets itself).
        await using var daemon = await CreateDaemonServerAsync();
        await using var first = await daemon.CreateClientAsync();
        await using var second = await daemon.CreateClientAsync();

        var logger = CreateLogger(daemon);
        var servers = daemon.GetStartedServers();

        DaemonConnectionContext.SetCurrent(servers[0]);
        var targetsForFirst = logger.GetTestAccessor().GetTargetServers();

        DaemonConnectionContext.SetCurrent(servers[1]);
        var targetsForSecond = logger.GetTestAccessor().GetTargetServers();

        Assert.Equal(servers[0], Assert.Single(targetsForFirst));
        Assert.Equal(servers[1], Assert.Single(targetsForSecond));
    }

    private GlobalLogMessageLogger CreateLogger(TestDaemon daemon)
        => new(
            categoryName: nameof(GlobalLogMessageLoggerTests),
            fallbackLoggerFactory: LoggerFactory,
            connectionManager: daemon.ConnectionManager,
            fallbackLogConfiguration: new LogConfiguration(LogLevel.Trace));
}
