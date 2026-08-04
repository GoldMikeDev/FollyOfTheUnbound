// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;
using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;
using Microsoft.CodeAnalysis.Options;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

/// <summary>
/// Phase 7 of the daemon per-connection isolation work (see
/// docs/ide/specs/daemon-per-connection-isolation.md): verifies a connection's own
/// <c>--sourceGeneratorExecutionPreference</c>, carried in its <see cref="ConnectionHandshake"/>, is applied as
/// a connection-scoped override (via <see cref="LanguageServerConnectionManager"/>) rather than mutating the
/// one shared <see cref="IGlobalOptionService"/> that every connection's request handling ultimately reads
/// from.
/// <para>
/// Reads back using <see cref="DaemonConnectionContext.GetAmbientTokenForTesting"/> rather than
/// <see cref="DaemonConnectionContext.SetCurrent(LanguageServerHost)"/>: the real per-connection write happens
/// under the lightweight marker token <see cref="LanguageServerConnectionManager"/> mints and makes ambient
/// *before* constructing the connection's <see cref="LanguageServerHost"/> (see that type's remarks), not under
/// the server instance itself, so <c>SetCurrent</c>'s server-as-its-own-token shortcut would silently look up
/// the wrong key and always miss (falling through to the shared default) rather than genuinely testing
/// isolation.
/// </para>
/// </summary>
public sealed class SourceGeneratorExecutionPreferenceRoutingTests(ITestOutputHelper testOutputHelper) : AbstractLanguageServerHostTests(testOutputHelper)
{
    [Fact]
    public async Task ConnectionsWithDifferentPreferences_SeeOnlyTheirOwnOverride()
    {
        await using var daemon = await CreateDaemonServerAsync();

        await using var automaticClient = await daemon.CreateClientAsync(
            handshake: new ConnectionHandshake(ExtensionLogDirectory: null, SourceGeneratorExecutionPreference: "Automatic"));
        await using var balancedClient = await daemon.CreateClientAsync(
            handshake: new ConnectionHandshake(ExtensionLogDirectory: null, SourceGeneratorExecutionPreference: "balanced"));
        await using var unspecifiedClient = await daemon.CreateClientAsync();

        var globalOptions = daemon.ExportProvider.GetExportedValue<IGlobalOptionService>();

        SetAmbientToRealConnectionToken(automaticClient.DaemonServer);
        Assert.Equal(SourceGeneratorExecutionPreference.Automatic, globalOptions.GetConnectionScopedOption(WorkspaceConfigurationOptionsStorage.SourceGeneratorExecution));

        SetAmbientToRealConnectionToken(balancedClient.DaemonServer);
        Assert.Equal(SourceGeneratorExecutionPreference.Balanced, globalOptions.GetConnectionScopedOption(WorkspaceConfigurationOptionsStorage.SourceGeneratorExecution));

        // A daemon connection that never sent a preference gets the command-line default (Automatic) as its
        // own explicit override -- *not* whatever the shared IGlobalOptionService happens to hold (which
        // reflects the daemon-launching client's own explicit choice, potentially a completely different
        // client than this one now that --sourceGeneratorExecutionPreference no longer splits clients into
        // separate daemons). Falling through to the shared value here would leak that launching client's
        // choice into this connection.
        SetAmbientToRealConnectionToken(unspecifiedClient.DaemonServer);
        Assert.Equal(SourceGeneratorExecutionPreference.Automatic, globalOptions.GetConnectionScopedOption(WorkspaceConfigurationOptionsStorage.SourceGeneratorExecution));

        // And the shared service itself still was never actually mutated by any connection's override.
        Assert.Equal(SourceGeneratorExecutionPreference.Balanced, globalOptions.GetOption(WorkspaceConfigurationOptionsStorage.SourceGeneratorExecution));
    }

    [Fact]
    public async Task InvalidPreference_RejectsTheConnection()
    {
        await using var daemon = await CreateDaemonServerAsync();

        // A daemon-launching client whose own explicit value differs from the command-line default, so this
        // client staying alive and unaffected afterward can't be confused with "the daemon crashed and took
        // every connection down with it" -- only the bad connection itself should be rejected.
        await using var launchingClient = await daemon.CreateClientAsync(
            handshake: new ConnectionHandshake(ExtensionLogDirectory: null, SourceGeneratorExecutionPreference: "Balanced"));

        var serversBefore = daemon.GetStartedServers();

        // LanguageServerConnectionManager now throws for a handshake whose SourceGeneratorExecutionPreference is
        // present but unparseable (see the "reject invalid routed source-generator preferences" fix), matching
        // what a cold daemon launch's own command-line parsing would do for the same invalid value. That throw
        // happens before the connection is ever registered/started, so the client side observes the connection
        // being torn down rather than getting an initialized server.
        await Assert.ThrowsAnyAsync<Exception>(() => daemon.CreateClientAsync(
            handshake: new ConnectionHandshake(ExtensionLogDirectory: null, SourceGeneratorExecutionPreference: "not-a-real-value")));

        // The rejection must be scoped to just that one bad connection attempt -- the daemon itself, and the
        // client that was already connected to it, must be unaffected.
        Assert.True(serversBefore.SequenceEqual(daemon.GetStartedServers()));
        Assert.True(daemon.IsRunning);

        var globalOptions = daemon.ExportProvider.GetExportedValue<IGlobalOptionService>();
        SetAmbientToRealConnectionToken(launchingClient.DaemonServer);
        Assert.Equal(SourceGeneratorExecutionPreference.Balanced, globalOptions.GetConnectionScopedOption(WorkspaceConfigurationOptionsStorage.SourceGeneratorExecution));
    }

    private static void SetAmbientToRealConnectionToken(LanguageServerHost server)
    {
        var token = DaemonConnectionContext.GetAmbientTokenForTesting(server);
        Assert.NotNull(token);
        AmbientConnectionToken.SetCurrent(token);
    }
}
