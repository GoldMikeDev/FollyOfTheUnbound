// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Completion.KeywordRecommenders;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Recommendations;

// MutateKeywordRecommender implements IKeywordRecommender<CSharpSyntaxContext> directly rather than
// AbstractSyntacticSingleKeywordRecommender (there's no real SyntaxKind.MutateKeyword to key off of --
// "mutate" is matched by identifier text in the parser), so this can't derive from the shared
// KeywordRecommenderTests base, which looks up its recommender via reflection over SyntaxKind.
[Trait(Traits.Feature, Traits.Features.KeywordRecommending)]
public sealed class MutateKeywordRecommenderTests : RecommenderTests
{
    protected override string KeywordText => "mutate";

    public MutateKeywordRecommenderTests()
    {
        var recommender = new MutateKeywordRecommender();
        RecommendKeywordsAsync = (position, context) => Task.FromResult(recommender.RecommendKeywords(position, context, CancellationToken.None));
    }

    [Fact]
    public Task TestEmptyStatement()
        => VerifyKeywordAsync(AddInsideMethod(
@"$$"));

    [Fact]
    public Task TestAfterStatement()
        => VerifyKeywordAsync(AddInsideMethod(
            """
            int i = 0;
            $$
            """));

    [Fact]
    public Task TestAfterBlock()
        => VerifyKeywordAsync(AddInsideMethod(
            """
            if (true) {
            }
            $$
            """));

    [Fact]
    public Task TestNotInClass()
        => VerifyAbsenceAsync("""
            class C
            {
                $$
            }
            """);

    [Fact]
    public Task TestNotAsExpression()
        => VerifyAbsenceAsync(AddInsideMethod(
@"var y = $$"));

    [Fact]
    public Task TestNotInParenthesizedExpression()
        => VerifyAbsenceAsync(AddInsideMethod(
@"var y = ($$"));

    [Fact]
    public Task TestAtRoot_Interactive()
        // Top-level statements in a script are IsGlobalStatementContext, same as any other
        // statement-starting keyword (e.g. WhileKeywordRecommenderTests.TestAtRoot_Interactive).
        => VerifyKeywordAsync(SourceCodeKind.Script,
@"$$");

    [Fact]
    public Task TestNotInUsingAlias()
        => VerifyAbsenceAsync(
@"using Goo = $$");
}
