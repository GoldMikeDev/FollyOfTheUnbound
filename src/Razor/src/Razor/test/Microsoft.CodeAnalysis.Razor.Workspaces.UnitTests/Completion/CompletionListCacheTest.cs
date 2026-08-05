// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Razor.Test.Common;
using Microsoft.CodeAnalysis.LanguageServer;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.Razor.Completion;

public class CompletionListCacheTest(ITestOutputHelper testOutput) : ToolingTestBase(testOutput)
{
    private readonly CompletionListCache _completionListCache = new CompletionListCache();
    private readonly ICompletionResolveContext _context = StrictMock.Of<ICompletionResolveContext>();

    [Fact]
    public void TryGet_SetCompletionList_ReturnsTrue()
    {
        // Arrange
        var completionList = new RazorVSInternalCompletionList()
        {
            Items = [new VSInternalCompletionItem()]
        };
        var resultId = _completionListCache.Add(completionList, _context);
        completionList.SetResultId(resultId, clientCapabilities: new());

        // Act
        var result = _completionListCache.TryGetOriginalRequestData((VSInternalCompletionItem)completionList.Items[0], out var cachedCompletionList, out var context);

        // Assert
        Assert.True(result);
        Assert.Same(completionList, cachedCompletionList);
        Assert.Same(_context, context);
    }

    [Fact]
    public void TryGet_SetCompletionListOnFullCache_ReturnsTrue()
    {
        // Arrange

        // Fill the completion list cache up until its cache max so the next entry causes eviction.
        for (var i = 0; i < CompletionListCache.MaxCacheSize; i++)
        {
            _completionListCache.Add(new VSInternalCompletionList(), _context);
        }

        var completionList = new RazorVSInternalCompletionList()
        {
            Items = [new VSInternalCompletionItem()]
        };
        var resultId = _completionListCache.Add(completionList, _context);
        completionList.SetResultId(resultId, clientCapabilities: new());

        // Act
        var result = _completionListCache.TryGetOriginalRequestData((VSInternalCompletionItem)completionList.Items[0], out var cachedCompletionList, out var context);

        // Assert
        Assert.True(result);
        Assert.Same(completionList, cachedCompletionList);
        Assert.Same(_context, context);
    }

    [Fact]
    public void TryGet_UnknownCompletionList_ReturnsTrue()
    {
        // Act
        var result = _completionListCache.TryGetOriginalRequestData(new VSInternalCompletionItem(), out var cachedCompletionList, out var context);

        // Assert
        Assert.False(result);
        Assert.Null(cachedCompletionList);
        Assert.Null(context);
    }

    [Fact]
    public void TryGet_LastCompletionList_ReturnsTrue()
    {
        // Arrange
        var initialCompletionList = new RazorVSInternalCompletionList()
        {
            Items = [new VSInternalCompletionItem()]
        };
        var initialCompletionListResultId = _completionListCache.Add(initialCompletionList, _context);
        initialCompletionList.SetResultId(initialCompletionListResultId, clientCapabilities: new());

        for (var i = 0; i < CompletionListCache.MaxCacheSize - 1; i++)
        {
            // We now fill the completion list cache up to its last slot.
            _completionListCache.Add(new VSInternalCompletionList(), _context);
        }

        // Act
        var result = _completionListCache.TryGetOriginalRequestData((VSInternalCompletionItem)initialCompletionList.Items[0], out var cachedCompletionList, out var context);

        // Assert
        Assert.True(result);
        Assert.Same(initialCompletionList, cachedCompletionList);
        Assert.Same(_context, context);
    }

    [Fact]
    public void TryGet_EvictedCompletionList_ReturnsFalse()
    {
        // Arrange
        var initialCompletionList = new RazorVSInternalCompletionList()
        {
            Items = [new VSInternalCompletionItem()]
        };
        var initialCompletionListResultId = _completionListCache.Add(initialCompletionList, _context);
        initialCompletionList.SetResultId(initialCompletionListResultId, clientCapabilities: new());

        // We now fill the completion list cache up until its cache max so that the initial completion list we set gets evicted.
        for (var i = 0; i < CompletionListCache.MaxCacheSize; i++)
        {
            _completionListCache.Add(new VSInternalCompletionList(), _context);
        }

        // Act
        var result = _completionListCache.TryGetOriginalRequestData((VSInternalCompletionItem)initialCompletionList.Items[0], out var cachedCompletionList, out var context);

        // Assert
        Assert.False(result);
        Assert.Null(cachedCompletionList);
        Assert.Null(context);
    }

    // Regression coverage for GoldMikeDev/roslyn#9: CohostCompletionListCache subclasses this type and is
    // resolved from a [Shared] MEF part shared by every daemon connection, so without per-connection keying
    // one connection's completion entries could evict, or be resolved against, another connection's. Simulates
    // two connections directly (same technique as RazorPerConnectionIsolationTests) since the leak lives
    // entirely within this type's cache lookup and doesn't need real request dispatch to reproduce.
    //
    // Against the pre-fix single shared circular buffer, this test fails: connection B's Add calls would land
    // in the *same* buffer as connection A's, so by the time connection A looks its entry up, connection B's
    // fill loop has evicted it -- TryGetOriginalRequestData would return false instead of finding connection
    // A's own completion list.
    [Fact]
    public void TryGet_TwoConnections_DoNotShareOrEvictEachOthersEntries()
    {
        var connectionA = new object();
        var connectionB = new object();
        var cache = new CompletionListCache();

        AmbientConnectionToken.SetCurrent(connectionA);
        var completionListA = new RazorVSInternalCompletionList()
        {
            Items = [new VSInternalCompletionItem()]
        };
        var resultIdA = cache.Add(completionListA, _context);
        completionListA.SetResultId(resultIdA, clientCapabilities: new());

        // Connection B fills its own cache to its max size -- enough to evict connection A's entry from a
        // *shared* buffer, but not from B's own isolated one.
        AmbientConnectionToken.SetCurrent(connectionB);
        for (var i = 0; i < CompletionListCache.MaxCacheSize; i++)
        {
            cache.Add(new VSInternalCompletionList() { Items = [] }, _context);
        }

        // Connection A can still resolve its own entry.
        AmbientConnectionToken.SetCurrent(connectionA);
        var resultA = cache.TryGetOriginalRequestData((VSInternalCompletionItem)completionListA.Items[0], out var cachedListA, out var contextA);
        Assert.True(resultA);
        Assert.Same(completionListA, cachedListA);
        Assert.Same(_context, contextA);

        // Connection B still on its own cache: looking up connection A's item under B's ambient token never
        // resolves to connection A's actual completion list object (isolation is symmetric, not just A being
        // protected from B) -- a coincidental id collision (both connections start numbering ids at 0) may
        // still "find" one of B's own slots, but it can never be A's list instance.
        AmbientConnectionToken.SetCurrent(connectionB);
        cache.TryGetOriginalRequestData((VSInternalCompletionItem)completionListA.Items[0], out var cachedListFromB, out _);
        Assert.NotSame(completionListA, cachedListFromB);
    }
}
