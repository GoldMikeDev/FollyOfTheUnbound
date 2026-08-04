// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Composition;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.VisualStudio.Razor.LanguageClient.Cohost;

namespace Microsoft.AspNetCore.Razor.LanguageServer;

/// <summary>
/// This is a <c>[Shared]</c> MEF part -- every daemon connection's Razor cohost startup resolves the
/// same instance (see GoldMikeDev/roslyn#9: the daemon has one process-wide MEF composition, shared across
/// every connection). <see cref="StartupAsync"/> used to overwrite one shared field, so whichever connection
/// initialized most recently silently became "the" client for every other connection's HTML synchronization
/// and logging. Keyed per <see cref="AmbientConnectionToken.Current"/> instead -- the same ambient-context
/// primitive <c>ConnectionScopedOptionOverrides</c> uses for the equivalent problem on the Roslyn LSP side --
/// so each connection's own <see cref="IClientLanguageServerManager"/> is only ever visible to that
/// connection's own request-handling code path. Matches that same facade's fallback too: a genuinely
/// connection-less caller (no ambient token at all, e.g. a test driving this type directly rather than
/// through real request dispatch) reads and writes a single shared slot, same as this type's behavior before
/// this fix, rather than <see cref="StartupAsync"/> silently discarding the manager it was given.
/// </summary>
[Shared]
[Export(typeof(IRazorCohostStartupService))]
[Export(typeof(RazorClientServerManagerProvider))]
[method: ImportingConstructor]
internal class RazorClientServerManagerProvider() : IRazorCohostStartupService
{
    private readonly ConditionalWeakTable<object, IClientLanguageServerManager> _managersByConnection = new();
    private IClientLanguageServerManager? _managerWithNoAmbientConnection;

    /// <summary>
    /// The calling connection's own <see cref="IClientLanguageServerManager"/>, or <see langword="null"/> if
    /// that connection hasn't run <see cref="StartupAsync"/> yet.
    /// </summary>
    public IClientLanguageServerManager? ClientLanguageServerManager
        => AmbientConnectionToken.Current is { } token
            ? _managersByConnection.TryGetValue(token, out var manager) ? manager : null
            : _managerWithNoAmbientConnection;

    public int Order => WellKnownStartupOrder.ClientServerManager;

    public Task StartupAsync(VSInternalClientCapabilities clientCapabilities, RequestContext requestContext, CancellationToken cancellationToken)
    {
        SetManagerForCurrentConnection(requestContext.GetRequiredService<IClientLanguageServerManager>());
        return Task.CompletedTask;
    }

    private void SetManagerForCurrentConnection(IClientLanguageServerManager manager)
    {
        if (AmbientConnectionToken.Current is { } token)
        {
            _managersByConnection.AddOrUpdate(token, manager);
        }
        else
        {
            _managerWithNoAmbientConnection = manager;
        }
    }

    internal readonly struct TestAccessor(RazorClientServerManagerProvider instance)
    {
        /// <summary>Sets the manager for the calling test's currently-ambient connection, bypassing the need to construct a real <see cref="RequestContext"/>.</summary>
        public void SetManagerForCurrentConnection(IClientLanguageServerManager manager)
            => instance.SetManagerForCurrentConnection(manager);
    }

    internal TestAccessor GetTestAccessor() => new(this);
}
