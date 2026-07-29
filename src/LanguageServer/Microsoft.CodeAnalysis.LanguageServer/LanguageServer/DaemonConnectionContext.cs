// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Typed, daemon-project-local view of <see cref="AmbientConnectionToken"/> for consumers that know the
/// ambient token is always a <see cref="LanguageServerHost"/> here (e.g. <see cref="Logging.GlobalLogMessageLogger"/>).
/// See <see cref="AmbientConnectionToken"/>'s own remarks for why the underlying primitive lives in the
/// lower-layer Protocol project instead of here.
/// </summary>
internal static class DaemonConnectionContext
{
    /// <summary>
    /// The connection the currently-executing code is running on behalf of, or <see langword="null"/> when
    /// there isn't one -- e.g. genuine process-wide startup work that happens before any client has connected,
    /// or single-server (non-daemon) mode, where there is only ever one connection and no ambiguity to resolve.
    /// </summary>
    public static LanguageServerHost? Current => (LanguageServerHost?)AmbientConnectionToken.Current;

    /// <summary>
    /// Establishes <paramref name="server"/> as <see cref="Current"/> for the rest of the calling method's own
    /// execution and anything it synchronously starts from here on (in particular, this must be called before
    /// <see cref="LanguageServerHost.Start"/> so the JSON-RPC dispatch loop that call spins up captures this
    /// connection as its ambient context). Per normal <see cref="System.Threading.AsyncLocal{T}"/> semantics,
    /// this does not leak back out to the caller once the calling method returns.
    /// </summary>
    public static void SetCurrent(LanguageServerHost server)
        => AmbientConnectionToken.SetCurrent(server);
}
