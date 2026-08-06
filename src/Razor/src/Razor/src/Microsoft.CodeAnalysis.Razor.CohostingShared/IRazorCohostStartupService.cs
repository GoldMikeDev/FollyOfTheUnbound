// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageServer.Handler;

namespace Microsoft.VisualStudio.Razor.LanguageClient.Cohost;

internal interface IRazorCohostStartupService
{
    int Order { get; }

    Task StartupAsync(VSInternalClientCapabilities clientCapabilities, RequestContext requestContext, CancellationToken cancellationToken);
}

/// <summary>
/// Optional companion interface for an <see cref="IRazorCohostStartupService"/> that keeps per-connection state
/// (see GoldMikeDev/roslyn#9): the service itself is a process-wide singleton for the lifetime of the daemon's
/// MEF composition -- <see cref="IRazorCohostStartupService.StartupAsync"/> is called once per connection, but
/// there's no corresponding per-connection teardown call on that interface, only the singleton's own
/// process-wide <see cref="System.IDisposable.Dispose"/> (if it implements that), which only runs once at
/// daemon/composition shutdown. A service that keys state off <c>AmbientConnectionToken.Current</c> (e.g. via a
/// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey, TValue}"/>) and also holds anything
/// that outlives the connection through a different strong root -- most commonly an event subscription, which
/// keeps its handler (and whatever it closes over) alive independent of the token's own reachability -- needs
/// an explicit per-connection cleanup call, or that state and its subscription leak for the daemon's entire
/// remaining lifetime. Implement this and <c>RazorStartupServiceFactory</c>'s per-connection
/// <c>RazorStartupService.Dispose()</c> will call it once that connection's own <c>ILspServices</c> are torn
/// down (while its <c>AmbientConnectionToken</c> is still the ambient one, so the implementation can use it to
/// know which connection's state to release).
/// </summary>
internal interface IRazorCohostConnectionScopedCleanup
{
    void ConnectionEnded();
}
