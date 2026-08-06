// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Typed, daemon-project-local view of <see cref="AmbientConnectionToken"/> for consumers that need to resolve
/// the ambient token back to a <see cref="LanguageServerHost"/> (e.g. <see cref="Logging.GlobalLogMessageLogger"/>).
/// See <see cref="AmbientConnectionToken"/>'s own remarks for why the underlying primitive lives in the
/// lower-layer Protocol project instead of here.
/// <para>
/// The ambient token is deliberately <em>not</em> the <see cref="LanguageServerHost"/> instance itself in the
/// real connection-startup path (<see cref="LanguageServerConnectionManager"/>): <see cref="LanguageServerHost"/>'s
/// constructor synchronously spins up <c>RequestExecutionQueue</c>'s background dispatch loop (via
/// <c>AbstractLanguageServer.Initialize()</c>), which captures whatever ambient token is current *at that
/// point*. A token set only after construction (i.e. after that loop already started and captured its
/// <see cref="System.Threading.ExecutionContext"/>) would never be seen by anything the queue later dispatches --
/// the loop's continuations, and everything they schedule, keep the context captured when the loop itself
/// began. So the token is minted and made ambient *before* <see cref="LanguageServerHost"/> is constructed, and
/// <see cref="Associate"/> maps it to the server only once construction succeeds.
/// </para>
/// </summary>
internal static class DaemonConnectionContext
{
    private static readonly ConditionalWeakTable<object, LanguageServerHost> s_serversByToken = new();

    // Reverse of s_serversByToken, populated alongside it in Associate. Exists only so tests can recover the
    // real per-connection marker token for a known LanguageServerHost -- e.g. to verify state (like
    // ConnectionScopedOptionOverrides entries) written under that token during real connection startup, which
    // SetCurrent's server-as-its-own-token shortcut below cannot reach, since it's a different token identity.
    private static readonly ConditionalWeakTable<LanguageServerHost, object> s_tokensByServer = new();

    /// <summary>
    /// The connection the currently-executing code is running on behalf of, or <see langword="null"/> when
    /// there isn't one -- e.g. genuine process-wide startup work that happens before any client has connected,
    /// or single-server (non-daemon) mode, where there is only ever one connection and no ambiguity to resolve.
    /// </summary>
    public static LanguageServerHost? Current
        => AmbientConnectionToken.Current is { } token && s_serversByToken.TryGetValue(token, out var server)
            ? server
            : null;

    /// <summary>
    /// Associates <paramref name="server"/> with whatever token is currently ambient (see
    /// <see cref="AmbientConnectionToken.Current"/>), so <see cref="Current"/> resolves to it from here on for
    /// that same connection's continuing execution. The token itself must already be ambient -- established by
    /// <see cref="LanguageServerConnectionManager"/> *before* it constructs the corresponding
    /// <see cref="LanguageServerHost"/>, per this type's remarks -- and is expected to be a lightweight marker
    /// object minted for that purpose, not <paramref name="server"/> itself.
    /// </summary>
    public static void Associate(LanguageServerHost server)
    {
        if (AmbientConnectionToken.Current is { } token)
        {
            s_serversByToken.AddOrUpdate(token, server);
            s_tokensByServer.AddOrUpdate(server, token);
        }
    }

    /// <summary>
    /// Test-only: the real per-connection marker token <see cref="LanguageServerConnectionManager"/> minted and
    /// made ambient before constructing <paramref name="server"/>, if any (only real daemon-connection startup
    /// populates this; the <see cref="SetCurrent"/> shortcut does not). Lets a test re-establish that exact
    /// ambient value (via <see cref="AmbientConnectionToken.SetCurrent"/>) to observe state written while it was
    /// ambient during real startup -- <see cref="SetCurrent"/>'s server-as-its-own-token shortcut is a
    /// <em>different</em> token identity and cannot see that state.
    /// </summary>
    internal static object? GetAmbientTokenForTesting(LanguageServerHost server)
        => s_tokensByServer.TryGetValue(server, out var token) ? token : null;

    /// <summary>
    /// Test-only convenience: establishes <paramref name="server"/> as both the ambient token and its own
    /// resolved connection, for tests that want <see cref="Current"/> to resolve to a specific server directly
    /// without separately minting a token and calling <see cref="Associate"/> -- acceptable there because tests
    /// using this call it before doing anything that itself schedules background work depending on the ambient
    /// value, unlike the real early-token/<see cref="Associate"/> sequence real connection startup requires.
    /// </summary>
    public static void SetCurrent(LanguageServerHost server)
    {
        AmbientConnectionToken.SetCurrent(server);
        s_serversByToken.AddOrUpdate(server, server);
    }
}
