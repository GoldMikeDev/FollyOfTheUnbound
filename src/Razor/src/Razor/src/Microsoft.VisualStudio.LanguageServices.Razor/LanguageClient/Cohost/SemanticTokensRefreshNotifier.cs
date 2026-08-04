// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.Razor.Workspaces.Settings;
using Microsoft.VisualStudio.Razor.LanguageClient.Cohost;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.AspNetCore.Razor.LanguageServer;

/// <summary>
/// This part is effectively a singleton for the lifetime of a daemon's MEF composition (see GoldMikeDev/roslyn#9):
/// the importing <c>RazorStartupServiceFactory</c> is <c>[Shared]</c> and only imports its
/// <c>IEnumerable&lt;Lazy&lt;IRazorCohostStartupService&gt;&gt;</c> once, so each <see cref="Lazy{T}"/>'s
/// <c>.Value</c> memoizes to one instance shared by every connection. <c>_razorClientLanguageServerManager</c> and
/// <c>_lastColorBackground</c> used to be plain fields, so a later connection's <see cref="StartupAsync"/> could
/// silently repoint an earlier, unrelated connection's refresh notifications at the wrong LSP manager. Keyed per
/// <see cref="AmbientConnectionToken.Current"/> instead, same pattern as <c>ClientSettingsManager</c>.
/// <see cref="IClientSettingsManager.ClientSettingsChanged"/> still fires every connection's handler on every
/// connection's settings change (it's not itself per-connection routed -- the same lower-severity gap already
/// tracked for <c>FeatureProviderRefresher</c> before that was fixed); each handler re-establishes its own
/// connection's <see cref="AmbientConnectionToken"/> before reading settings so it only ever reacts to its own
/// connection's actual value, not whichever connection's change happened to trigger the broadcast.
/// </summary>
[Export(typeof(IRazorCohostStartupService))]
[method: ImportingConstructor]
internal sealed class SemanticTokensRefreshNotifier(IClientSettingsManager clientSettingsManager) : IRazorCohostStartupService, IDisposable
{
    private sealed class ConnectionState
    {
        public IClientLanguageServerManager? RazorClientLanguageServerManager;
        public bool LastColorBackground;
        public EventHandler<EventArgs>? Handler;
        public object? Token;
    }

    private readonly IClientSettingsManager _clientSettingsManager = clientSettingsManager;

    private readonly ConditionalWeakTable<object, ConnectionState> _stateByConnection = new();
    private readonly ConnectionState _stateWithNoAmbientConnection = new();

    // Tracked separately since ConditionalWeakTable isn't enumerable on every target this project builds for;
    // used only to unsubscribe every connection's event handler on Dispose.
    private readonly object _allStatesLock = new();
    private readonly List<ConnectionState> _allStates = [];

    public int Order => WellKnownStartupOrder.Default;

    public Task StartupAsync(VSInternalClientCapabilities clientCapabilities, RequestContext requestContext, CancellationToken cancellationToken)
    {
        var state = GetState();
        state.RazorClientLanguageServerManager = requestContext.GetRequiredService<IClientLanguageServerManager>();

        if (clientCapabilities.Workspace?.SemanticTokens?.RefreshSupport ?? false)
        {
            state.LastColorBackground = _clientSettingsManager.GetClientSettings().AdvancedSettings.ColorBackground;

            if (state.Handler is null)
            {
                state.Token = AmbientConnectionToken.Current;
                // Task.Run so the AmbientConnectionToken.SetCurrent inside OnClientSettingsChanged is scoped to
                // that background task's own copy of the ExecutionContext -- it must not leak into whichever
                // connection's context happened to be ambient when ClientSettingsChanged fired (which triggers
                // every connection's handler synchronously, in that firing connection's own context) or into
                // any other handler invoked afterward in that same synchronous dispatch.
                state.Handler = (sender, e) => _ = Task.Run(() => OnClientSettingsChanged(state));
                _clientSettingsManager.ClientSettingsChanged += state.Handler;

                lock (_allStatesLock)
                {
                    _allStates.Add(state);
                }
            }
        }

        return Task.CompletedTask;
    }

    private void OnClientSettingsChanged(ConnectionState state)
    {
        if (state.Token is { } token)
        {
            AmbientConnectionToken.SetCurrent(token);
        }

        var colorBackground = _clientSettingsManager.GetClientSettings().AdvancedSettings.ColorBackground;
        if (colorBackground == state.LastColorBackground)
        {
            return;
        }

        state.LastColorBackground = colorBackground;
        state.RazorClientLanguageServerManager.AssumeNotNull().SendNotificationAsync(Methods.WorkspaceSemanticTokensRefreshName, CancellationToken.None).Forget();
    }

    private ConnectionState GetState()
        => AmbientConnectionToken.Current is { } token
            ? _stateByConnection.GetOrCreateValue(token)
            : _stateWithNoAmbientConnection;

    public void Dispose()
    {
        if (_stateWithNoAmbientConnection.Handler is { } handler)
        {
            _clientSettingsManager.ClientSettingsChanged -= handler;
        }

        lock (_allStatesLock)
        {
            foreach (var state in _allStates)
            {
                if (state.Handler is { } connectionHandler)
                {
                    _clientSettingsManager.ClientSettingsChanged -= connectionHandler;
                }
            }
        }
    }
}
