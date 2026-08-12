// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using Xunit;

namespace Microsoft.VisualStudio.LanguageServices.LiveShare.UnitTests;

// This assembly otherwise contains a single fast test, and vstest's Blame data collector
// (/Blame:CollectDump;CollectHangDump, see ProcessTestExecutor.BuildRspFileContents) has been
// observed intermittently misreporting the testhost.net472 process as crashed when it exits
// almost immediately after that lone test completes, even though the run actually passed and
// results were written successfully. Keeping the testhost alive a little longer avoids the
// race. Safe to remove if this assembly gains other longer-running tests, or if the underlying
// vstest/Blame race gets fixed upstream.
public sealed class TestHostKeepAliveTests
{
    [Fact]
    public void KeepTestHostAliveBriefly()
        => Thread.Sleep(2000);
}
