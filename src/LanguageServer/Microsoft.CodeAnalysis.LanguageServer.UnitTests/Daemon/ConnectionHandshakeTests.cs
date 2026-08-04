// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class ConnectionHandshakeTests
{
    [Fact]
    public void FromServerArguments_ExtractsBothOptions_TwoTokenForm()
    {
        var handshake = ConnectionHandshake.FromServerArguments(
            ["--extensionLogDirectory", "/tmp/logs", "--sourceGeneratorExecutionPreference", "Balanced"]);

        Assert.Equal("/tmp/logs", handshake.ExtensionLogDirectory);
        Assert.Equal("Balanced", handshake.SourceGeneratorExecutionPreference);
    }

    [Fact]
    public void FromServerArguments_ExtractsBothOptions_InlineForm()
    {
        var handshake = ConnectionHandshake.FromServerArguments(
            ["--extensionLogDirectory=/tmp/logs", "--sourceGeneratorExecutionPreference=Balanced"]);

        Assert.Equal("/tmp/logs", handshake.ExtensionLogDirectory);
        Assert.Equal("Balanced", handshake.SourceGeneratorExecutionPreference);
    }

    [Fact]
    public void FromServerArguments_NeitherOptionPresent_BothNull()
    {
        var handshake = ConnectionHandshake.FromServerArguments(["--extension", "foo.dll"]);

        Assert.Null(handshake.ExtensionLogDirectory);
        Assert.Null(handshake.SourceGeneratorExecutionPreference);
    }

    [Theory]
    [InlineData("--extensionLogDirectory")]
    [InlineData("--sourceGeneratorExecutionPreference")]
    public void FromServerArguments_BareFlagAtEndOfArguments_Throws(string option)
    {
        // A bare flag with no following value token requires a value on the daemon's own command-line parsing
        // (System.CommandLine's default ArgumentArity.ExactlyOne), so a cold daemon launch would reject this
        // outright. Silently treating it as "not specified" here -- routing to a compatible already-running
        // daemon, which then applies its own default -- would let the identical malformed invocation succeed or
        // fail purely depending on daemon reuse happenstance.
        Assert.Throws<InvalidOperationException>(() => ConnectionHandshake.FromServerArguments([option]));
    }
}
