// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Razor.Test.Common;
using Microsoft.CodeAnalysis.LanguageServer;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.Razor.Protocol;

// Regression coverage for GoldMikeDev/roslyn#9: subclasses of AbstractClientCapabilitiesService (e.g.
// RazorCohostClientCapabilitiesService) are resolved from a [Shared] MEF part shared by every daemon
// connection. SetCapabilities used to mutate one shared field, so a later connection's LSP `initialize` could
// silently overwrite the capabilities an earlier, unrelated connection's ClientCapabilities callers observe.
// Simulates two connections directly (same technique as RazorPerConnectionIsolationTests) since the leak lives
// entirely within this type and doesn't need real request dispatch to reproduce.
//
// Against the pre-fix single shared field, this test fails: connection B's SetCapabilities call overwrites
// the field connection A already set, so re-reading ClientCapabilities under connection A afterward would
// observe connection B's capabilities object instead of its own.
public class AbstractClientCapabilitiesServiceTest(ITestOutputHelper testOutput) : ToolingTestBase(testOutput)
{
    private sealed class TestClientCapabilitiesService : AbstractClientCapabilitiesService;

    [Fact]
    public void TwoConnections_DoNotShareOrOverwriteCapabilities()
    {
        var connectionA = new object();
        var connectionB = new object();
        var service = new TestClientCapabilitiesService();

        var capabilitiesA = new VSInternalClientCapabilities();
        var capabilitiesB = new VSInternalClientCapabilities();

        AmbientConnectionToken.SetCurrent(connectionA);
        service.SetCapabilities(capabilitiesA);

        AmbientConnectionToken.SetCurrent(connectionB);
        service.SetCapabilities(capabilitiesB);

        AmbientConnectionToken.SetCurrent(connectionA);
        Assert.Same(capabilitiesA, service.ClientCapabilities);

        AmbientConnectionToken.SetCurrent(connectionB);
        Assert.Same(capabilitiesB, service.ClientCapabilities);
    }

    [Fact]
    public void NoAmbientConnection_FallsBackToSharedSlot()
    {
        var service = new TestClientCapabilitiesService();
        var capabilities = new VSInternalClientCapabilities();

        service.SetCapabilities(capabilities);

        Assert.Same(capabilities, service.ClientCapabilities);
    }
}
