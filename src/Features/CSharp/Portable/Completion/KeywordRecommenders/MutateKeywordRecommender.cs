// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.CSharp.Extensions.ContextQuery;

namespace Microsoft.CodeAnalysis.CSharp.Completion.KeywordRecommenders;

// "mutate" is recognized by the parser via identifier text rather than through a
// dedicated contextual SyntaxKind (see LanguageParser.ParseMutateStatement), so this
// recommender can't build on AbstractSyntacticSingleKeywordRecommender, which requires
// a real SyntaxKind for SyntaxFacts.GetText.
internal sealed class MutateKeywordRecommender() : IKeywordRecommender<CSharpSyntaxContext>
{
    private static readonly ImmutableArray<RecommendedKeyword> s_keywords = [new RecommendedKeyword("mutate")];

    public ImmutableArray<RecommendedKeyword> RecommendKeywords(int position, CSharpSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.IsPreProcessorDirectiveContext)
        {
            return [];
        }

        return context.IsStatementContext || context.IsGlobalStatementContext
            ? s_keywords
            : [];
    }
}
