// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using Microsoft.CodeAnalysis.Host.Mef;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler;

/// <summary>
/// This is a <c>[Shared]</c> MEF part -- every daemon connection resolves the same instance (see
/// GoldMikeDev/roslyn#9) -- but its subscribers (<see cref="AbstractRefreshQueue"/> instances) are themselves
/// per-connection <see cref="ILspService"/>s. A plain event would fan out a
/// <c>workspace/featureProviders/_vs_refresh</c> notification meant for one connection to every other
/// connection's refresh queues too, causing unrelated editors to recompute semantic tokens/diagnostics/code
/// lenses/inlay hints/project context for no reason. <see cref="RequestProviderRefresh"/> instead only invokes
/// the subscriber(s) registered under the same <see cref="AmbientConnectionToken.Current"/> that was ambient
/// when they subscribed (i.e. the requesting connection's own queues) -- falling back to invoking everyone
/// only when there's genuinely no ambient connection at call time, the same "no ambient context" broadcast
/// fallback <c>GlobalLogMessageLogger</c> uses.
/// </summary>
[Export(typeof(FeatureProviderRefresher)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class FeatureProviderRefresher()
{
    private readonly object _gate = new();
    private readonly List<(object? ConnectionToken, Action<DocumentUri?> Handler)> _subscribers = [];

    public void Subscribe(Action<DocumentUri?> handler)
    {
        lock (_gate)
            _subscribers.Add((AmbientConnectionToken.Current, handler));
    }

    public void Unsubscribe(Action<DocumentUri?> handler)
    {
        lock (_gate)
        {
            var index = _subscribers.FindIndex(s => s.Handler == handler);
            if (index >= 0)
                _subscribers.RemoveAt(index);
        }
    }

    public void RequestProviderRefresh(DocumentUri? documentUri)
    {
        var currentToken = AmbientConnectionToken.Current;

        Action<DocumentUri?>[] targets;
        lock (_gate)
        {
            targets = currentToken is null
                ? [.. _subscribers.Select(s => s.Handler)]
                : [.. _subscribers.Where(s => Equals(s.ConnectionToken, currentToken)).Select(s => s.Handler)];
        }

        foreach (var handler in targets)
            handler(documentUri);
    }
}
