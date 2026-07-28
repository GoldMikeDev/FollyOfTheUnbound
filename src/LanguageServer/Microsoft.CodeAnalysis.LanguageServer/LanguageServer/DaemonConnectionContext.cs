// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Ambient "which connection is this work happening on behalf of" signal for daemon mode, where a single
/// process hosts multiple independent <see cref="LanguageServerHost"/> instances (one per connected client)
/// sharing process-global infrastructure (the MEF <see cref="Microsoft.VisualStudio.Composition.ExportProvider"/>,
/// <see cref="Logging.GlobalLogMessageLogger"/>, etc.). Consumers that would otherwise have to guess which
/// connection to attribute a piece of shared-infrastructure activity to can instead read <see cref="Current"/>.
/// <para>
/// Backed by <see cref="AsyncLocal{T}"/>; see
/// <c>AsyncLocalPropagationTests</c> for verification that this reliably flows through the daemon's actual
/// request-handling path (including <c>StreamJsonRpc</c> dispatch) without leaking between concurrent
/// connections. See docs/ide/specs/daemon-per-connection-isolation.md for the design this is phase 2 of.
/// </para>
/// </summary>
internal static class DaemonConnectionContext
{
    private static readonly AsyncLocal<LanguageServerHost?> s_current = new();

    /// <summary>
    /// The connection the currently-executing code is running on behalf of, or <see langword="null"/> when
    /// there isn't one -- e.g. genuine process-wide startup work that happens before any client has connected,
    /// or single-server (non-daemon) mode, where there is only ever one connection and no ambiguity to resolve.
    /// </summary>
    public static LanguageServerHost? Current => s_current.Value;

    /// <summary>
    /// Establishes <paramref name="server"/> as <see cref="Current"/> for the rest of the calling method's own
    /// execution and anything it synchronously starts from here on (in particular, this must be called before
    /// <see cref="LanguageServerHost.Start"/> so the JSON-RPC dispatch loop that call spins up captures this
    /// connection as its ambient context). Per normal <see cref="AsyncLocal{T}"/> semantics, this does not
    /// leak back out to the caller once the calling method returns.
    /// </summary>
    public static void SetCurrent(LanguageServerHost server)
        => s_current.Value = server;
}
