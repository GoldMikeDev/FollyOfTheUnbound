// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.LanguageServer.Protocol;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests.Handler;

/// <summary>
/// Regression coverage for GoldMikeDev/roslyn#9: <see cref="FeatureProviderRefresher"/> is a <c>[Shared]</c> MEF
/// part, so every daemon connection's <c>AbstractRefreshQueue</c>-derived subscribers used to receive every
/// other connection's <c>workspace/featureProviders/_vs_refresh</c> notification too, via a plain shared
/// event. <see cref="FeatureProviderRefresher.RequestProviderRefresh"/> now only invokes the subscriber(s)
/// registered under the same ambient connection token that was current when they subscribed.
/// </summary>
[UseExportProvider]
public sealed class FeatureProviderRefresherTests
{
    private static FeatureProviderRefresher CreateRefresher()
        => LspTestCompositions.LanguageServerProtocol.ExportProviderFactory.CreateExportProvider()
            .GetExportedValue<FeatureProviderRefresher>();

    [Fact]
    public void TwoConnections_OnlyTargetsTheRequestingConnectionsSubscriber()
    {
        var connectionA = new object();
        var connectionB = new object();
        var refresher = CreateRefresher();

        DocumentUri? receivedByA = null;
        var aCallCount = 0;
        DocumentUri? receivedByB = null;
        var bCallCount = 0;

        AmbientConnectionToken.SetCurrent(connectionA);
        refresher.Subscribe(uri => { receivedByA = uri; aCallCount++; });

        AmbientConnectionToken.SetCurrent(connectionB);
        refresher.Subscribe(uri => { receivedByB = uri; bCallCount++; });

        var requestedUri = new DocumentUri("file:///a.cs");
        AmbientConnectionToken.SetCurrent(connectionA);
        refresher.RequestProviderRefresh(requestedUri);

        Assert.Equal(1, aCallCount);
        Assert.Equal(requestedUri, receivedByA);
        Assert.Equal(0, bCallCount);
        Assert.Null(receivedByB);
    }

    [Fact]
    public async Task NoAmbientConnectionAtRequestTime_BroadcastsToAllSubscribers()
    {
        var connectionA = new object();
        var connectionB = new object();
        var refresher = CreateRefresher();

        var aCallCount = 0;
        var bCallCount = 0;

        // Task.Run captures a copy of the ExecutionContext, so SetCurrent inside each doesn't leak back to this
        // method's own (ambient-token-free) context once the task completes -- the same technique used
        // elsewhere in this codebase (see AbstractTextDocumentContentRefreshQueue) to scope an ambient-token
        // mutation to a specific piece of work.
        await Task.Run(() =>
        {
            AmbientConnectionToken.SetCurrent(connectionA);
            refresher.Subscribe(_ => aCallCount++);
        });
        await Task.Run(() =>
        {
            AmbientConnectionToken.SetCurrent(connectionB);
            refresher.Subscribe(_ => bCallCount++);
        });

        Assert.Null(AmbientConnectionToken.Current);
        refresher.RequestProviderRefresh(documentUri: null);

        Assert.Equal(1, aCallCount);
        Assert.Equal(1, bCallCount);
    }

    [Fact]
    public void Unsubscribe_StopsReceivingRefreshes()
    {
        var connectionA = new object();
        var refresher = CreateRefresher();
        var callCount = 0;
        void Handler(DocumentUri? uri) => callCount++;

        AmbientConnectionToken.SetCurrent(connectionA);
        refresher.Subscribe(Handler);
        refresher.RequestProviderRefresh(documentUri: null);
        Assert.Equal(1, callCount);

        refresher.Unsubscribe(Handler);
        refresher.RequestProviderRefresh(documentUri: null);
        Assert.Equal(1, callCount);
    }
}
