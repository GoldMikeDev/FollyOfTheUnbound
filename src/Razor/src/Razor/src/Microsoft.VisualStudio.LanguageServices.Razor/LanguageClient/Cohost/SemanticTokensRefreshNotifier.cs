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
/// <para>
/// A per-connection <see cref="ConnectionState"/> and its <see cref="IClientSettingsManager.ClientSettingsChanged"/>
/// subscription would otherwise outlive that connection for as long as the daemon runs: the subscription is a
/// strong reference held by <c>_clientSettingsManager</c> itself, independent of whether the connection's own
/// <see cref="AmbientConnectionToken"/> object (the <see cref="ConditionalWeakTable{TKey, TValue}"/> key) is
/// still reachable, so it -- and everything the handler closes over, including the connection's
/// <see cref="IClientLanguageServerManager"/> -- would leak for every past connection a long-lived daemon ever
/// served. <see cref="IRazorCohostConnectionScopedCleanup.ConnectionEnded"/> is called by
/// <c>RazorStartupServiceFactory</c> as part of that connection's own teardown, so state actually gets released
/// per-connection instead of only at process-wide <see cref="IDisposable.Dispose"/> (daemon/composition exit).
/// </para>
/// </summary>
[Export(typeof(IRazorCohostStartupService))]
[method: ImportingConstructor]
internal sealed class SemanticTokensRefreshNotifier(IClientSettingsManager clientSettingsManager)
    : IRazorCohostStartupService, IRazorCohostConnectionScopedCleanup, IDisposable
{
    private sealed class ConnectionState
    {
        public IClientLanguageServerManager? RazorClientLanguageServerManager;
        public bool LastColorBackground;
        public EventHandler<EventArgs>? Handler;
        public object? Token;

        /// <summary>
        /// Guards every read/write of this state's other fields, so <see cref="StartupAsync"/> subscribing and
        /// <see cref="ConnectionEnded"/> unsubscribing for the *same* connection can never interleave -- see
        /// their remarks for why a plain <see cref="CancellationToken"/> check wasn't enough on its own.
        /// </summary>
        public readonly object Lock = new();

        /// <summary>Set once <see cref="ConnectionEnded"/> has processed this connection; once true, nothing may
        /// mutate this state or subscribe a new handler for it again.</summary>
        public bool Ended;
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
        // RazorStartupServiceFactory realizes every IRazorCohostStartupService's Lazy<T> up front (to sort by
        // Order) before calling StartupAsync on any of them, so RazorStartupService.Dispose()'s ConnectionEnded
        // sweep (which iterates that same already-realized set) can run concurrently with this call for a
        // connection that's torn down immediately after being created. A plain cancellationToken check here
        // isn't atomic with the subscribe below -- ConnectionEnded could still run in the gap between the check
        // and the subscribe -- so state.Lock below is what actually closes the race; this check is just a fast
        // path that skips the lock and work entirely once cancellation is already visible.
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var state = GetState();

        lock (state.Lock)
        {
            // ConnectionEnded (on another thread, for this same connection) may have already run and set this
            // under the lock -- including for a state that was never subscribed at all, if it ran before this
            // method got here. Once true, nothing may touch this state or subscribe a handler for it again;
            // whatever ConnectionEnded already did (or found nothing to do) is final.
            if (state.Ended)
            {
                return Task.CompletedTask;
            }

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

    /// <summary>
    /// Called once for the currently-ending connection (identified by <see cref="AmbientConnectionToken.Current"/>,
    /// still ambient at this point -- see <see cref="IRazorCohostConnectionScopedCleanup"/>'s remarks). Releases
    /// that connection's <see cref="ConnectionState"/> and, critically, unsubscribes its handler from
    /// <see cref="IClientSettingsManager.ClientSettingsChanged"/> so it stops being invoked (and stops keeping
    /// the state, including that connection's <see cref="IClientLanguageServerManager"/>, alive) once the
    /// connection is gone.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="ConditionalWeakTable{TKey, TValue}.GetOrCreateValue"/>, not <c>TryGetValue</c>: this can
    /// legitimately run *before* <see cref="StartupAsync"/> ever does for this connection (see that method's
    /// remarks on the realize-then-start ordering), in which case there's no entry yet -- but a late
    /// <see cref="StartupAsync"/> call still needs to observe that this connection already ended, which means
    /// this must create (and permanently mark <see cref="ConnectionState.Ended"/> on) the same state object
    /// <see cref="GetState"/> would hand back later, not silently no-op.
    /// </remarks>
    public void ConnectionEnded()
    {
        if (AmbientConnectionToken.Current is not { } token)
        {
            return;
        }

        var state = _stateByConnection.GetOrCreateValue(token);

        lock (state.Lock)
        {
            state.Ended = true;

            if (state.Handler is { } handler)
            {
                _clientSettingsManager.ClientSettingsChanged -= handler;
            }
        }

        lock (_allStatesLock)
        {
            _allStates.Remove(state);
        }
    }

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
