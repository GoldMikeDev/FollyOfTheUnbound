// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Threading;
using Roslyn.LanguageServer.Protocol;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.TextDocumentContent;

/// <summary>
/// Abstract refresh queue for text document content providers. Subclasses specify which URI scheme they handle
/// and implement custom change detection logic via <see cref="AbstractRefreshQueue.OnLspSolutionChanged"/>.
/// Refresh notifications are sent for any open document matching the specified scheme.
/// </summary>
internal abstract class AbstractTextDocumentContentRefreshQueue :
    IOnInitialized,
    ILspService,
    IDisposable
{
    private readonly IAsynchronousOperationListener _asyncListener;
    private readonly CancellationTokenSource _disposalTokenSource = new();
    private readonly LspWorkspaceRegistrationService _lspWorkspaceRegistrationService;
    private readonly LspWorkspaceManager _lspWorkspaceManager;
    private readonly IClientLanguageServerManager _notificationManager;
    private readonly AsyncBatchingWorkQueue _refreshQueue;

    /// <summary>
    /// This connection's ambient token, captured here because this instance is itself constructed within that
    /// connection's own ambient scope (LSP service construction happens under the same connection-scoped
    /// <see cref="AmbientConnectionToken"/> as the request that triggers it) -- unlike
    /// <see cref="OnLspSolutionChanged"/> below, which is invoked directly by <see cref="Workspace.WorkspaceChanged"/>
    /// (via <see cref="LspWorkspaceRegistrationService"/>), a raw event whose caller (e.g. a file watcher or
    /// background project reload, not necessarily an LSP request being dispatched) has no reason to be flowing
    /// this connection's ambient token at all. Without restoring it, a connection-scoped read in
    /// <see cref="ShouldEnqueueRefreshNotificationAsync"/> (e.g. <c>IWorkspaceConfigurationService.Options</c>)
    /// would silently fall back to shared/default values instead of this connection's own.
    /// </summary>
    private readonly object? _connectionToken = AmbientConnectionToken.Current;

    public AbstractTextDocumentContentRefreshQueue(
        IAsynchronousOperationListenerProvider asynchronousOperationListenerProvider,
        LspWorkspaceRegistrationService lspWorkspaceRegistrationService,
        LspWorkspaceManager lspWorkspaceManager,
        IClientLanguageServerManager notificationManager)
    {
        _lspWorkspaceRegistrationService = lspWorkspaceRegistrationService;
        _lspWorkspaceManager = lspWorkspaceManager;
        _notificationManager = notificationManager;
        _asyncListener = asynchronousOperationListenerProvider.GetListener(FeatureAttribute.Workspace);

        // Batch up workspace notifications so that we only send a notification to refresh virtual files
        // every 2 seconds - long enough to avoid spamming the client with notifications, but short enough to refresh
        // the virtual files relatively frequently.
        _refreshQueue = _refreshQueue = new AsyncBatchingWorkQueue(
            delay: DelayTimeSpan.Idle,
            processBatchAsync: RefreshVirtualDocumentsAsync,
            asyncListener: _asyncListener,
            _disposalTokenSource.Token);
    }

    public async Task OnInitializedAsync(ClientCapabilities clientCapabilities, RequestContext context, CancellationToken cancellationToken)
    {
        if (clientCapabilities.Workspace?.TextDocumentContent == null)
        {
            return;
        }

        // After we have initialized we can start listening for workspace changes.
        _lspWorkspaceRegistrationService.LspSolutionChanged += OnLspSolutionChanged;
    }

    private void OnLspSolutionChanged(object? sender, WorkspaceChangeEventArgs e)
    {
        var asyncToken = _asyncListener.BeginAsyncOperation($"{nameof(AbstractTextDocumentContentRefreshQueue)}.{nameof(OnLspSolutionChanged)}");

        // Task.Run captures a *copy* of the current ExecutionContext for its delegate, so setting the ambient
        // token inside it (restoring this connection's own token, captured at construction -- see
        // _connectionToken's remarks) cannot leak back out to whatever raised WorkspaceChanged, unlike setting
        // it directly here would (AmbientConnectionToken.SetCurrent mutates the current logical call context,
        // which a plain synchronous method call does not isolate from its caller).
        _ = Task.Run(() =>
        {
            if (_connectionToken is not null)
                AmbientConnectionToken.SetCurrent(_connectionToken);

            return OnLspSolutionChangedAsync(e);
        }, _disposalTokenSource.Token)
            .CompletesAsyncOperation(asyncToken)
            .ReportNonFatalErrorUnlessCancelledAsync(_disposalTokenSource.Token);
    }

    protected async Task OnLspSolutionChangedAsync(WorkspaceChangeEventArgs e)
    {
        var shouldQueue = await ShouldEnqueueRefreshNotificationAsync(e, _disposalTokenSource.Token).ConfigureAwait(false);
        if (shouldQueue)
        {
            _refreshQueue.AddWork();
        }
    }

    protected abstract Task<bool> ShouldEnqueueRefreshNotificationAsync(WorkspaceChangeEventArgs e, CancellationToken cancellationToken);

    /// <summary>
    /// The scheme that this queue is responsible for.
    /// </summary>
    protected abstract string Scheme { get; }

    private async ValueTask RefreshVirtualDocumentsAsync(
        CancellationToken cancellationToken)
    {
        var trackedDocuments = _lspWorkspaceManager.GetTrackedLspText();

        foreach (var kvp in trackedDocuments)
        {
            var uri = kvp.Key;
            if (uri.ParsedDocumentUri is { } parsedUri && parsedUri.Scheme == Scheme)
            {
                try
                {
                    await _notificationManager.SendRequestAsync(
                        Methods.WorkspaceTextDocumentContentRefreshName,
                        new TextDocumentContentRefreshParams { Uri = uri },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException)
                {
                    // Connection may be lost during shutdown.
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        _lspWorkspaceRegistrationService.LspSolutionChanged -= OnLspSolutionChanged;
        _disposalTokenSource.Cancel();
        _disposalTokenSource.Dispose();
    }
}
