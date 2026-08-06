// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Ambient "which connection is this work happening on behalf of" signal, opaque at this layer so it can be
/// shared between the daemon-hosting project (<c>Microsoft.CodeAnalysis.LanguageServer</c>, which sets it to
/// the connection's <c>LanguageServerHost</c> instance and reads it back for daemon-specific routing like
/// <c>GlobalLogMessageLogger</c>) and this project (which can't reference <c>LanguageServerHost</c> -- the
/// daemon project depends on this one, not the reverse -- but still needs a per-connection key for
/// <c>ConnectionScopedOptionOverrides</c>).
/// <para>
/// Backed by <see cref="AsyncLocal{T}"/>; see <c>AsyncLocalPropagationTests</c> (in the daemon project's test
/// suite) for verification that this reliably flows through the daemon's actual request-handling path
/// (including <c>StreamJsonRpc</c> dispatch) without leaking between concurrent connections. See
/// docs/ide/specs/daemon-per-connection-isolation.md for the design this is part of.
/// </para>
/// </summary>
internal static class AmbientConnectionToken
{
    private static readonly AsyncLocal<object?> s_current = new();

    /// <summary>
    /// An opaque token identifying the connection the currently-executing code is running on behalf of, or
    /// <see langword="null"/> when there isn't one -- e.g. genuine process-wide startup work that happens
    /// before any client has connected. Callers that need to key per-connection state should use this value's
    /// identity only (e.g. as a <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey, TValue}"/>
    /// key); it carries no other meaning at this layer.
    /// </summary>
    public static object? Current => s_current.Value;

    /// <summary>
    /// Establishes <paramref name="connection"/> as <see cref="Current"/> for the rest of the calling method's
    /// own execution and anything it synchronously starts from here on. Per normal <see cref="AsyncLocal{T}"/>
    /// semantics, this does not leak back out to the caller once the calling method returns.
    /// </summary>
    public static void SetCurrent(object connection)
        => s_current.Value = connection;
}
