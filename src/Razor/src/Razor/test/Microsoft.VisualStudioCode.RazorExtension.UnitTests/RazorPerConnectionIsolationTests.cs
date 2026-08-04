// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Razor.Settings;
using Microsoft.VisualStudio.Razor.LanguageClient.Cohost;
using Microsoft.VisualStudioCode.RazorExtension.Configuration;
using Xunit;

namespace Microsoft.VisualStudioCode.RazorExtension.Test;

/// <summary>
/// Regression coverage for GoldMikeDev/roslyn#9: <see cref="ClientSettingsManager"/> and
/// <see cref="RazorClientServerManagerProvider"/> are both <c>[Shared]</c> MEF parts, so a real daemon
/// resolves the exact same instance for every connection. Both used to store one connection's data in a
/// single shared field, so a second connection's <c>workspace/didChangeConfiguration</c> (or Razor cohost
/// startup) silently clobbered the first connection's state. Both are now keyed by
/// <see cref="AmbientConnectionToken.Current"/> instead; these tests simulate two connections directly (the
/// same technique <c>ConnectionScopedOptionOverridesTests</c> uses on the Roslyn LSP side) rather than
/// standing up two full daemon connections, since the leak is entirely within these two MEF-singleton types
/// and doesn't need real request dispatch to reproduce or verify.
/// </summary>
public sealed class RazorPerConnectionIsolationTests
{
    [Fact]
    public void ClientSettingsManager_TwoConnections_DoNotShareUpdates()
    {
        var connectionA = new object();
        var connectionB = new object();
        var manager = new ClientSettingsManager();

        AmbientConnectionToken.SetCurrent(connectionA);
        manager.Update(ClientAdvancedSettings.Default with { CodeBlockBraceOnNextLine = true });

        AmbientConnectionToken.SetCurrent(connectionB);
        manager.Update(ClientAdvancedSettings.Default with { CodeBlockBraceOnNextLine = false });

        AmbientConnectionToken.SetCurrent(connectionA);
        Assert.True(manager.GetClientSettings().AdvancedSettings.CodeBlockBraceOnNextLine);

        AmbientConnectionToken.SetCurrent(connectionB);
        Assert.False(manager.GetClientSettings().AdvancedSettings.CodeBlockBraceOnNextLine);
    }

    [Fact]
    public void ClientSettingsManager_NoAmbientConnection_FallsBackToSharedSlot()
    {
        // Mirrors ConnectionScopedOptionOverrides's fallback: a genuinely connection-less caller (e.g. a test
        // driving this type directly) must still see its own writes, not have them silently dropped.
        var manager = new ClientSettingsManager();

        manager.Update(ClientAdvancedSettings.Default with { CodeBlockBraceOnNextLine = true });

        Assert.True(manager.GetClientSettings().AdvancedSettings.CodeBlockBraceOnNextLine);
    }

    [Fact]
    public void RazorClientServerManagerProvider_TwoConnections_DoNotShareManager()
    {
        var connectionA = new object();
        var connectionB = new object();
        var managerA = new FakeClientLanguageServerManager();
        var managerB = new FakeClientLanguageServerManager();
        var provider = new RazorClientServerManagerProvider();

        AmbientConnectionToken.SetCurrent(connectionA);
        provider.GetTestAccessor().SetManagerForCurrentConnection(managerA);

        AmbientConnectionToken.SetCurrent(connectionB);
        provider.GetTestAccessor().SetManagerForCurrentConnection(managerB);

        AmbientConnectionToken.SetCurrent(connectionA);
        Assert.Same(managerA, provider.ClientLanguageServerManager);

        AmbientConnectionToken.SetCurrent(connectionB);
        Assert.Same(managerB, provider.ClientLanguageServerManager);
    }

    [Fact]
    public void RazorClientServerManagerProvider_NoAmbientConnection_FallsBackToSharedSlot()
    {
        var manager = new FakeClientLanguageServerManager();
        var provider = new RazorClientServerManagerProvider();

        provider.GetTestAccessor().SetManagerForCurrentConnection(manager);

        Assert.Same(manager, provider.ClientLanguageServerManager);
    }

    private sealed class FakeClientLanguageServerManager : IClientLanguageServerManager
    {
        public Task<TResponse> SendRequestAsync<TParams, TResponse>(string methodName, TParams @params, CancellationToken cancellationToken)
            => throw new System.NotImplementedException();

        public ValueTask SendRequestAsync(string methodName, CancellationToken cancellationToken)
            => throw new System.NotImplementedException();

        public ValueTask SendRequestAsync<TParams>(string methodName, TParams @params, CancellationToken cancellationToken)
            => throw new System.NotImplementedException();

        public ValueTask SendNotificationAsync(string methodName, CancellationToken cancellationToken)
            => throw new System.NotImplementedException();

        public ValueTask SendNotificationAsync<TParams>(string methodName, TParams @params, CancellationToken cancellationToken)
            => throw new System.NotImplementedException();
    }
}
