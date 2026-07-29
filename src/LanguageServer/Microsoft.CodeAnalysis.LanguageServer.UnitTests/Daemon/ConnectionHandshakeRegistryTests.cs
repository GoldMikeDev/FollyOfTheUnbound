// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.Daemon;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

/// <summary>
/// Regression coverage for a Codex finding on PR #3: <see cref="ConnectionHandshakeRegistry.Current"/> must
/// distinguish "no handshake was ever registered for this connection" (single-server mode, or a genuinely
/// connection-less caller -- callers should fall back to daemon-wide configuration) from "a real daemon
/// connection registered a handshake whose field was omitted" (callers must treat that as an explicit absence,
/// never fall back to daemon-wide configuration, since that configuration reflects an unrelated client's
/// choice now that fields like <see cref="ConnectionHandshake.ExtensionLogDirectory"/> no longer split clients
/// into separate daemons). Collapsing both cases to the same sentinel -- as an earlier version of this type
/// did, always returning <see cref="ConnectionHandshake.Empty"/> for both -- made every caller's fallback
/// silently leak one connection's configuration into another's.
/// </summary>
public sealed class ConnectionHandshakeRegistryTests
{
    [Fact]
    public void NoAmbientConnection_ReturnsNull()
    {
        Assert.Null(AmbientConnectionToken.Current);
        Assert.Null(ConnectionHandshakeRegistry.Current);
    }

    [Fact]
    public void AmbientConnectionWithNoRegisteredHandshake_ReturnsNull()
    {
        AmbientConnectionToken.SetCurrent(new object());
        Assert.Null(ConnectionHandshakeRegistry.Current);
    }

    [Fact]
    public void RegisteredHandshakeWithOmittedField_IsDistinctFromNoHandshake()
    {
        AmbientConnectionToken.SetCurrent(new object());
        ConnectionHandshakeRegistry.Register(new ConnectionHandshake(ExtensionLogDirectory: null, SourceGeneratorExecutionPreference: null));

        var current = ConnectionHandshakeRegistry.Current;
        Assert.NotNull(current);
        Assert.Null(current.ExtensionLogDirectory);
    }

    [Fact]
    public void RegisteredHandshakeWithValue_IsReturned()
    {
        AmbientConnectionToken.SetCurrent(new object());
        ConnectionHandshakeRegistry.Register(new ConnectionHandshake(ExtensionLogDirectory: "/tmp/logs", SourceGeneratorExecutionPreference: null));

        Assert.Equal("/tmp/logs", ConnectionHandshakeRegistry.Current?.ExtensionLogDirectory);
    }

    [Fact]
    public void DifferentConnections_DoNotObserveEachOthersHandshake()
    {
        var connectionA = new object();
        var connectionB = new object();

        AmbientConnectionToken.SetCurrent(connectionA);
        ConnectionHandshakeRegistry.Register(new ConnectionHandshake(ExtensionLogDirectory: "/tmp/a", SourceGeneratorExecutionPreference: null));

        AmbientConnectionToken.SetCurrent(connectionB);
        // connectionB never registered a handshake of its own -- must not see connectionA's.
        Assert.Null(ConnectionHandshakeRegistry.Current);
    }
}
