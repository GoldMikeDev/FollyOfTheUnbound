// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

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

        // The connection that never sent a preference falls through to the shared, daemon-wide default --
        // which neither override above ever actually mutated.
        var sharedDefault = globalOptions.GetOption(WorkspaceConfigurationOptionsStorage.SourceGeneratorExecution);
        SetAmbientToRealConnectionToken(unspecifiedClient.DaemonServer);
        Assert.Equal(sharedDefault, globalOptions.GetConnectionScopedOption(WorkspaceConfigurationOptionsStorage.SourceGeneratorExecution));
    }

    [Fact]
    public async Task InvalidPreference_FallsThroughToSharedDefault()
    {
        await using var daemon = await CreateDaemonServerAsync();

        await using var client = await daemon.CreateClientAsync(
            handshake: new ConnectionHandshake(ExtensionLogDirectory: null, SourceGeneratorExecutionPreference: "not-a-real-value"));

        var globalOptions = daemon.ExportProvider.GetExportedValue<IGlobalOptionService>();

        SetAmbientToRealConnectionToken(client.DaemonServer);
        Assert.Equal(
            globalOptions.GetOption(WorkspaceConfigurationOptionsStorage.SourceGeneratorExecution),
            globalOptions.GetConnectionScopedOption(WorkspaceConfigurationOptionsStorage.SourceGeneratorExecution));
    }

    private static void SetAmbientToRealConnectionToken(LanguageServerHost server)
    {
        var token = DaemonConnectionContext.GetAmbientTokenForTesting(server);
        Assert.NotNull(token);
        AmbientConnectionToken.SetCurrent(token);
    }
}
