// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Composition;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.Razor.Logging;
using Microsoft.CodeAnalysis.Razor.Remote;
using Microsoft.CodeAnalysis.Razor.SemanticTokens;
using Microsoft.CodeAnalysis.Razor.Workspaces;
using Microsoft.CodeAnalysis.Razor.Workspaces.Settings;
using Microsoft.CodeAnalysis.Remote.Razor;
using Microsoft.VisualStudio.Razor.LanguageClient.Cohost;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudioCode.RazorExtension.Services;

/// <summary>
/// This is a <c>[Shared]</c> MEF part -- every daemon connection resolves the same instance (see
/// GoldMikeDev/roslyn#9). <c>_clientSettingsService</c> used to be a plain field overwritten by whichever
/// connection's <see cref="StartupAsync"/> ran last, and <c>ClientSettingsChanged</c> was subscribed with the
/// same method-group delegate on every call -- since C# doesn't deduplicate identical target+method
/// subscriptions, each new connection appended another copy to the invocation list instead of replacing it, so
/// a long-lived daemon serving N connections would run <see cref="UpdateClientSettingsAsync"/> N times (against
/// whichever service the *last* connection happened to create) for every single settings change, with that
/// count only growing. Keyed per <see cref="AmbientConnectionToken.Current"/> instead, one subscription per
/// connection, cleaned up via <see cref="IRazorCohostConnectionScopedCleanup.ConnectionEnded"/> -- same pattern
/// as <c>SemanticTokensRefreshNotifier</c>, including that type's re-establish-the-connection's-own-token step
/// before reading settings inside the shared event handler (see <see cref="ClientSettingsManager_ClientSettingsChanged"/>),
/// since <see cref="IClientSettingsManager.GetClientSettings"/> is itself ambient-token-scoped and the handler
/// otherwise runs under whichever connection's change happened to trigger the broadcast, not its own.
/// </summary>
[Shared]
[Export(typeof(IRazorCohostStartupService))]
[method: ImportingConstructor]
internal sealed class VSCodeRemoteServicesInitializer(
    ISemanticTokensLegendService semanticTokensLegendService,
    IWorkspaceProvider workspaceProvider,
    IClientSettingsManager clientSettingsManager,
    ILoggerFactory loggerFactory) : IRazorCohostStartupService, IRazorCohostConnectionScopedCleanup, IDisposable
{
    private sealed class ConnectionState
    {
        public IRemoteClientSettingsService? ClientSettingsService;
        public EventHandler<EventArgs>? Handler;
        public object? Token;
        public readonly object Lock = new();
        public bool Ended;
    }

    private readonly ISemanticTokensLegendService _semanticTokensLegendService = semanticTokensLegendService;
    private readonly IWorkspaceProvider _workspaceProvider = workspaceProvider;
    private readonly IClientSettingsManager _clientSettingsManager = clientSettingsManager;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    private readonly ConditionalWeakTable<object, ConnectionState> _stateByConnection = new();
    private readonly ConnectionState _stateWithNoAmbientConnection = new();

    public int Order => WellKnownStartupOrder.RemoteServices;

    public async Task StartupAsync(VSInternalClientCapabilities clientCapabilities, RequestContext requestContext, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Normal remote service invoker logic requires a solution, but we don't have one here. Fortunately we don't need one, and since
        // we know this is VS Code specific, its all just smoke and mirrors anyway. We can avoid the smoke :)
        var serviceInterceptor = new VSCodeBrokeredServiceInterceptor();

        // First things first, set the cache directory for the MEF composition.
        RemoteMefComposition.CacheDirectory = Path.Combine(Path.GetDirectoryName(this.GetType().Assembly.Location)!, "cache");

        var logger = _loggerFactory.GetOrCreateLogger<VSCodeRemoteServicesInitializer>();
        logger.LogDebug("Initializing remote services.");
        var service = await InProcServiceFactory.CreateServiceAsync<IRemoteClientInitializationService>(serviceInterceptor, _workspaceProvider, _loggerFactory).ConfigureAwait(false);
        logger.LogDebug("Initialized remote services.");

        await service.InitializeLspAsync(new RemoteClientLSPInitializationOptions
        {
            ClientCapabilities = clientCapabilities,
            TokenTypes = _semanticTokensLegendService.TokenTypes.All,
            TokenModifiers = _semanticTokensLegendService.TokenModifiers.All,
        }, cancellationToken).ConfigureAwait(false);

        var clientSettingsService = await InProcServiceFactory.CreateServiceAsync<IRemoteClientSettingsService>(serviceInterceptor, _workspaceProvider, _loggerFactory).ConfigureAwait(false);

        var state = GetState();
        lock (state.Lock)
        {
            if (state.Ended)
            {
                return;
            }

            state.ClientSettingsService = clientSettingsService;

            if (state.Handler is null)
            {
                state.Token = AmbientConnectionToken.Current;
                // Client settings are initialized after this service, so there is no point updating settings at startup.
                state.Handler = (sender, e) => ClientSettingsManager_ClientSettingsChanged(state);
                _clientSettingsManager.ClientSettingsChanged += state.Handler;
            }
        }
    }

    private ConnectionState GetState()
        => AmbientConnectionToken.Current is { } token
            ? _stateByConnection.GetOrCreateValue(token)
            : _stateWithNoAmbientConnection;

    public void ConnectionEnded()
    {
        if (AmbientConnectionToken.Current is not { } token)
        {
            return;
        }

        // GetOrCreateValue, not TryGetValue: see SemanticTokensRefreshNotifier.ConnectionEnded's remarks for why
        // this must create (and permanently mark Ended on) the same state object GetState would hand back to a
        // StartupAsync call that hasn't run yet for this connection.
        var state = _stateByConnection.GetOrCreateValue(token);

        lock (state.Lock)
        {
            state.Ended = true;

            if (state.Handler is { } handler)
            {
                _clientSettingsManager.ClientSettingsChanged -= handler;
            }
        }
    }

    public void Dispose()
    {
        if (_stateWithNoAmbientConnection.Handler is { } handler)
        {
            _clientSettingsManager.ClientSettingsChanged -= handler;
        }
    }

    private void ClientSettingsManager_ClientSettingsChanged(ConnectionState state)
    {
        // Task.Run so the AmbientConnectionToken.SetCurrent inside UpdateClientSettingsAsync is scoped to that
        // background task's own copy of the ExecutionContext -- it must not leak into whichever connection's
        // context happened to be ambient when ClientSettingsChanged fired (which triggers every connection's
        // handler synchronously, in that firing connection's own context) or into any sibling handler invoked
        // afterward in that same synchronous dispatch. Same pattern as SemanticTokensRefreshNotifier.
        _ = Task.Run(() => UpdateClientSettingsAsync(state, CancellationToken.None));
    }

    private Task UpdateClientSettingsAsync(ConnectionState state, CancellationToken cancellationToken)
    {
        if (state.Token is { } token)
        {
            AmbientConnectionToken.SetCurrent(token);
        }

        if (state.ClientSettingsService is not { } clientSettingsService)
        {
            throw new InvalidOperationException($"{nameof(VSCodeRemoteServicesInitializer)} has not been started.");
        }

        return clientSettingsService.UpdateAsync(_clientSettingsManager.GetClientSettings(), cancellationToken).AsTask();
    }
}
