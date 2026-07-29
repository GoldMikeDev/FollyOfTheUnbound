// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.UnitTests;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests.Options;

/// <summary>
/// Phase 4 of the daemon per-connection isolation work (see
/// docs/ide/specs/daemon-per-connection-isolation.md): verifies that
/// <see cref="ConnectionScopedOptionOverrides"/> keeps one connection's
/// <c>workspace/didChangeConfiguration</c>-driven option write from bleeding into another connection's reads of
/// the same, process-wide shared <see cref="IGlobalOptionService"/>.
/// </summary>
[UseExportProvider]
public sealed class ConnectionScopedOptionOverridesTests
{
    private static TestWorkspace CreateWorkspace()
        => new LspTestWorkspace(LspTestCompositions.LanguageServerProtocol.ExportProviderFactory.CreateExportProvider());

    [Fact]
    public void NoAmbientConnection_WritesAndReadsFallBackToSharedService()
    {
        using var workspace = CreateWorkspace();
        var globalOptions = workspace.GetService<IGlobalOptionService>();

        Assert.Null(AmbientConnectionToken.Current);

        ConnectionScopedOptionOverrides.SetOverrides(
            globalOptions,
            [KeyValuePair.Create(new OptionKey2(FormattingOptions2.InsertFinalNewLine), (object?)true)]);

        Assert.True(globalOptions.GetOption(FormattingOptions2.InsertFinalNewLine));
        Assert.True(globalOptions.GetConnectionScopedOption(FormattingOptions2.InsertFinalNewLine));
    }

    [Fact]
    public void AmbientConnectionSet_WriteIsScopedToThatConnectionOnly()
    {
        using var workspace = CreateWorkspace();
        var globalOptions = workspace.GetService<IGlobalOptionService>();

        var connectionA = new object();
        var connectionB = new object();

        // Default value on the shared service, seen by both "connections" before any override is set.
        Assert.False(globalOptions.GetOption(FormattingOptions2.InsertFinalNewLine));

        AmbientConnectionToken.SetCurrent(connectionA);
        ConnectionScopedOptionOverrides.SetOverrides(
            globalOptions,
            [KeyValuePair.Create(new OptionKey2(FormattingOptions2.InsertFinalNewLine), (object?)true)]);

        // Connection A observes its own override...
        Assert.True(globalOptions.GetConnectionScopedOption(FormattingOptions2.InsertFinalNewLine));

        // ...but connection B does not see A's write: it still falls through to the shared, unmodified default.
        AmbientConnectionToken.SetCurrent(connectionB);
        Assert.False(globalOptions.GetConnectionScopedOption(FormattingOptions2.InsertFinalNewLine));

        // And the underlying shared service was never actually mutated by A's write.
        Assert.False(globalOptions.GetOption(FormattingOptions2.InsertFinalNewLine));
    }

    [Fact]
    public void AmbientConnectionSet_PerLanguageOptionOverrideIsScopedToThatConnectionOnly()
    {
        using var workspace = CreateWorkspace();
        var globalOptions = workspace.GetService<IGlobalOptionService>();

        var connectionA = new object();
        var connectionB = new object();

        AmbientConnectionToken.SetCurrent(connectionA);
        ConnectionScopedOptionOverrides.SetOverrides(
            globalOptions,
            [KeyValuePair.Create(new OptionKey2(FormattingOptions2.IndentationSize, LanguageNames.CSharp), (object?)8)]);

        Assert.Equal(8, globalOptions.GetConnectionScopedOption(FormattingOptions2.IndentationSize, LanguageNames.CSharp));

        AmbientConnectionToken.SetCurrent(connectionB);
        Assert.Equal(4, globalOptions.GetConnectionScopedOption(FormattingOptions2.IndentationSize, LanguageNames.CSharp));
    }

    [Fact]
    public void GetConnectionScopedOption_OptionKey2Overload_HonorsOverride()
    {
        using var workspace = CreateWorkspace();
        var globalOptions = workspace.GetService<IGlobalOptionService>();

        var connectionA = new object();
        AmbientConnectionToken.SetCurrent(connectionA);

        var optionKey = new OptionKey2(FormattingOptions2.InsertFinalNewLine);
        ConnectionScopedOptionOverrides.SetOverrides(
            globalOptions,
            [KeyValuePair.Create(optionKey, (object?)true)]);

        Assert.True(globalOptions.GetConnectionScopedOption<bool>(optionKey));
    }
}
