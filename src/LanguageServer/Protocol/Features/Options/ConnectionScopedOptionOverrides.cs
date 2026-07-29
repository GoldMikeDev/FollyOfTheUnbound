// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Per-connection overrides for options a client configured via <c>workspace/didChangeConfiguration</c>.
/// <para>
/// In daemon mode, every connection's LSP request handlers ultimately read from and write to the same single,
/// process-wide <see cref="IGlobalOptionService"/> (MEF composition happens once per daemon; there is no
/// scoped/child <c>ExportProvider</c> to give each connection its own instance -- see
/// docs/ide/specs/daemon-per-connection-isolation.md for why). Without this, one client's
/// <c>workspace/didChangeConfiguration</c> silently changes option values seen by every other concurrently
/// connected client. This type lets the option-reading call sites listed in that design doc's phase 3 audit
/// consult a per-connection override, keyed by <see cref="AmbientConnectionToken.Current"/>, before falling
/// through to the shared service -- without needing a real MEF-level scope.
/// </para>
/// </summary>
internal static class ConnectionScopedOptionOverrides
{
    // Keyed by the ambient connection token's identity (a LanguageServerHost in the daemon project, opaque
    // here); a ConditionalWeakTable lets a connection's overrides be collected once nothing else references
    // its token anymore, without this type needing to know when a connection ends.
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<OptionKey2, object?>> s_overridesByConnection = new();

    /// <summary>
    /// Returns the connection-scoped override for <paramref name="optionKey"/>, if the ambient connection (see
    /// <see cref="AmbientConnectionToken"/>) has one recorded via <see cref="SetOverrides"/>.
    /// </summary>
    public static bool TryGetOverride(OptionKey2 optionKey, out object? value)
    {
        value = null;
        return AmbientConnectionToken.Current is { } connection
            && s_overridesByConnection.TryGetValue(connection, out var overrides)
            && overrides.TryGetValue(optionKey, out value);
    }

    /// <summary>
    /// Records <paramref name="values"/> as overrides scoped to the ambient connection (see
    /// <see cref="AmbientConnectionToken"/>), read back by <see cref="TryGetOverride"/>. If there is no
    /// ambient connection (some genuinely connection-less caller), falls back to writing
    /// <paramref name="fallback"/> directly, preserving the pre-existing (unscoped) behavior for that case
    /// rather than silently dropping the write.
    /// </summary>
    public static void SetOverrides(IGlobalOptionService fallback, IEnumerable<KeyValuePair<OptionKey2, object?>> values)
    {
        if (AmbientConnectionToken.Current is not { } connection)
        {
            fallback.SetGlobalOptions([.. values]);
            return;
        }

        var overrides = s_overridesByConnection.GetOrCreateValue(connection);
        foreach (var (key, value) in values)
            overrides[key] = value;
    }
}
