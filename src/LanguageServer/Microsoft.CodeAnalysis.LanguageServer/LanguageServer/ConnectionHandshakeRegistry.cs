// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Associates each connection's <see cref="ConnectionHandshake"/> (see
/// docs/ide/specs/daemon-per-connection-isolation.md's phase 5) with its ambient
/// <see cref="AmbientConnectionToken"/>, so daemon-project code reachable while handling that connection's
/// requests (e.g. <see cref="Testing.RunTestsHandler"/>) can look up the connecting client's own
/// per-connection configuration instead of the one shared, daemon-wide <see cref="ServerConfiguration"/>.
/// <para>
/// Keyed directly by the ambient token's identity, the same way <c>ConnectionScopedOptionOverrides</c>
/// (Protocol project, phase 4) is, rather than by the resolved <see cref="Microsoft.CodeAnalysis.LanguageServer.LanguageServer.LanguageServerHost"/>
/// via <see cref="DaemonConnectionContext"/> -- avoiding any dependency on that resolution having already
/// happened, and matching where the token becomes ambient (before the server is even constructed; see
/// <see cref="DaemonConnectionContext"/>'s remarks).
/// </para>
/// </summary>
internal static class ConnectionHandshakeRegistry
{
    private static readonly ConditionalWeakTable<object, ConnectionHandshake> s_handshakesByToken = new();

    /// <summary>
    /// Records <paramref name="handshake"/> for whatever token is currently ambient (see
    /// <see cref="AmbientConnectionToken.Current"/>). Called once, when
    /// <see cref="LanguageServerConnectionManager"/> starts that connection's server.
    /// </summary>
    public static void Register(ConnectionHandshake handshake)
    {
        if (AmbientConnectionToken.Current is { } token)
            s_handshakesByToken.AddOrUpdate(token, handshake);
    }

    /// <summary>
    /// The ambient connection's handshake, or <see langword="null"/> if there is no ambient connection or it
    /// never received one -- meaning this is <em>not</em> a daemon connection with a handshake (single-server
    /// mode, or a genuinely connection-less caller), not merely one whose handshake omitted every field.
    /// <para>
    /// Deliberately distinct from a non-null <see cref="ConnectionHandshake"/> whose fields are all
    /// <see langword="null"/> (a real daemon connection that simply didn't pass a value for something): now
    /// that daemon connections can share a daemon regardless of per-connection-routed fields like
    /// <see cref="ConnectionHandshake.ExtensionLogDirectory"/> (see <c>DaemonPipeName</c>'s pipe-key exclusion),
    /// falling back to the *daemon-wide* configuration for an omitted field would silently leak whichever
    /// client happened to launch the daemon's value to every other connection that omitted it -- exactly the
    /// bug this distinction exists to prevent. Consumers should treat a non-null <see cref="Current"/> with a
    /// null field as "this connection explicitly has no value," not "fall through to the shared default."
    /// </para>
    /// </summary>
    public static ConnectionHandshake? Current
        => AmbientConnectionToken.Current is { } token && s_handshakesByToken.TryGetValue(token, out var handshake)
            ? handshake
            : null;
}
