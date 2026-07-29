// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Threading;
using Roslyn.LanguageServer.Protocol;
using Roslyn.Utilities;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler;

internal abstract class AbstractRefreshQueue :
    IOnInitialized,
    ILspService,
    IDisposable
{
    private AsyncBatchingWorkQueue<DocumentUri?>? _refreshQueue;

    private readonly LspWorkspaceManager _lspWorkspaceManager;
    private readonly IClientLanguageServerManager _notificationManager;

    private readonly IAsynchronousOperationListener _asyncListener;
    private readonly CancellationTokenSource _disposalTokenSource;
    private readonly LspWorkspaceRegistrationService _lspWorkspaceRegistrationService;
    private readonly FeatureProviderRefresher _providerRefresher;

    protected abstract string GetFeatureAttribute();
    protected abstract bool? GetRefreshSupport(ClientCapabilities clientCapabilities);
    protected abstract string GetWorkspaceRefreshName();

    /// <summary>
    /// Whether a change to <paramref name="option"/> should trigger a refresh notification. Only overridden by
    /// subclasses that listen for option changes at all (see <see cref="RefreshIfOptionsChanged"/>); the base
    /// implementation says no option ever triggers a refresh, for subclasses driven purely by workspace/solution
    /// changes instead.
    /// </summary>
    protected virtual bool IsRefreshRelevantOption(IOption2 option) => false;

    /// <summary>
    /// Connection-scoped counterpart to the <see cref="IGlobalOptionService"/> option-changed-event-driven refresh
    /// that subclasses wire up in their constructors. <c>ConnectionScopedOptionOverrides.SetOverrides</c>
    /// deliberately never raises that event for a daemon connection's own option changes (writing directly to the
    /// shared <see cref="IGlobalOptionService"/> would leak one connection's values into every other connection --
    /// see docs/ide/specs/daemon-per-connection-isolation.md) -- so a client that changes, say, an inlay-hint option
    /// via <c>workspace/didChangeConfiguration</c> would otherwise never get a refresh notification for it here.
    /// This instance is itself connection-scoped (<see cref="AbstractRefreshQueue"/> is an <see cref="ILspService"/>,
    /// one instance per connection), so the caller only needs to invoke this on its own connection's instance.
    /// </summary>
    public void RefreshIfOptionsChanged(IReadOnlyList<KeyValuePair<OptionKey2, object?>> changedOptions)
    {
        foreach (var (key, _) in changedOptions)
        {
            if (IsRefreshRelevantOption(key.Option))
            {
                EnqueueRefreshNotification(documentUri: null);
                return;
            }
        }
    }

    public AbstractRefreshQueue(
        IAsynchronousOperationListenerProvider asynchronousOperationListenerProvider,
        LspWorkspaceRegistrationService lspWorkspaceRegistrationService,
        LspWorkspaceManager lspWorkspaceManager,
        IClientLanguageServerManager notificationManager,
        FeatureProviderRefresher providerRefresher)
    {
        _asyncListener = asynchronousOperationListenerProvider.GetListener(GetFeatureAttribute());
        _lspWorkspaceRegistrationService = lspWorkspaceRegistrationService;
        _disposalTokenSource = new();
        _lspWorkspaceManager = lspWorkspaceManager;
        _notificationManager = notificationManager;
        _providerRefresher = providerRefresher;
        _providerRefresher.ProviderRefreshRequested += EnqueueRefreshNotification;
    }

    public async Task OnInitializedAsync(ClientCapabilities clientCapabilities, RequestContext context, CancellationToken cancellationToken)
    {
        Initialize(clientCapabilities);
    }

    public void Initialize(ClientCapabilities clientCapabilities)
    {
        if (_refreshQueue is null && GetRefreshSupport(clientCapabilities) is true)
        {
            // Only send a refresh notification to the client every 2s (if needed) in order to avoid
            // sending too many notifications at once.  This ensures we batch up workspace notifications,
            // but also means we send soon enough after a compilation-computation to not make the user wait
            // an enormous amount of time.
            _refreshQueue = new AsyncBatchingWorkQueue<DocumentUri?>(
                delay: TimeSpan.FromMilliseconds(2000),
                processBatchAsync: (documentUris, cancellationToken)
                    => FilterLspTrackedDocumentsAsync(_lspWorkspaceManager, _notificationManager, documentUris, cancellationToken),
                equalityComparer: EqualityComparer<DocumentUri?>.Default,
                asyncListener: _asyncListener,
                _disposalTokenSource.Token);
            _lspWorkspaceRegistrationService.LspSolutionChanged += OnLspSolutionChanged;
        }
    }

    protected virtual void OnLspSolutionChanged(object? sender, WorkspaceChangeEventArgs e)
    {
        if (e.DocumentId is not null && e.Kind is WorkspaceChangeKind.DocumentChanged)
        {
            var document = e.NewSolution.GetRequiredDocument(e.DocumentId);
            var documentUri = document.GetURI();

            // We enqueue the URI since there's a chance the client is already tracking the
            // document, in which case we don't need to send a refresh notification.
            // We perform the actual check when processing the batch to ensure we have the
            // most up-to-date list of tracked documents.
            EnqueueRefreshNotification(documentUri);
        }
        else
        {
            EnqueueRefreshNotification(documentUri: null);
        }
    }

    /// <summary>
    /// Enqueues a request to refresh the workspace.  If <paramref name="documentUri"/> is null, then the refresh will
    /// always happen.  If non-null, the refresh will only happen if the client is <em>not</em> tracking that document.
    /// If the client is tracking the document, no refresh is necessary as the client clearly knows about the change.
    /// </summary>
    protected void EnqueueRefreshNotification(DocumentUri? documentUri)
        => _refreshQueue?.AddWork(documentUri);

    private async ValueTask FilterLspTrackedDocumentsAsync(
        LspWorkspaceManager lspWorkspaceManager,
        IClientLanguageServerManager notificationManager,
        ImmutableSegmentedList<DocumentUri?> documentUris,
        CancellationToken cancellationToken)
    {
        var trackedDocuments = lspWorkspaceManager.GetTrackedLspText();
        foreach (var documentUri in documentUris)
        {
            if (documentUri is null || !trackedDocuments.ContainsKey(documentUri))
            {
                try
                {
                    // Fire the notification and immediately return.  Refresh notifications are server-wide, and are not
                    // associated with a particular project/document.  So once we've sent one, we can stop processing
                    // entirely.
                    await notificationManager.SendRequestAsync(GetWorkspaceRefreshName(), cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException)
                {
                    // It is entirely possible that we're shutting down and the connection is lost while we're trying to send a notification
                    // as this runs outside of the guaranteed ordering in the queue. We can safely ignore this exception.
                }
            }
        }

        // LSP is already tracking all changed documents so we don't need to send a refresh request.
    }

    public virtual void Dispose()
    {
        _providerRefresher.ProviderRefreshRequested -= EnqueueRefreshNotification;
        _lspWorkspaceRegistrationService.LspSolutionChanged -= OnLspSolutionChanged;
        _disposalTokenSource.Cancel();
        _disposalTokenSource.Dispose();
    }
}
