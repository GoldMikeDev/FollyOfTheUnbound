// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.CSharp.Extensions.ContextQuery;

namespace Microsoft.CodeAnalysis.CSharp.Completion.KeywordRecommenders;

// "escape" is recognized by the parser via identifier text rather than through a
// dedicated contextual SyntaxKind (see LanguageParser.ParseEscapeStatement), so this
// recommender can't build on AbstractSyntacticSingleKeywordRecommender, which requires
// a real SyntaxKind for SyntaxFacts.GetText.
internal sealed class EscapeKeywordRecommender() : IKeywordRecommender<CSharpSyntaxContext>
{
    private static readonly ImmutableArray<RecommendedKeyword> s_keywords = [new RecommendedKeyword("escape", matchPriority: MatchPriority.Default - 1)];

    public ImmutableArray<RecommendedKeyword> RecommendKeywords(int position, CSharpSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.IsPreProcessorDirectiveContext)
        {
            return [];
        }

        // Unlike "mutate;", "escape;" only binds successfully inside a BlockBinder chain (it jumps
        // to a label synthesized on the nearest enclosing block). Top-level/global statements bind
        // through SimpleProgramBinder, whose chain bottoms out at BuckStopsHereBinder -- which
        // returns a null EscapeLabel -- so an "escape;" inserted at global-statement scope can never
        // bind and always produces ERR_NoBreakOrCont. Don't offer it there.
        return context.IsStatementContext
            ? s_keywords
            : [];
    }
}
