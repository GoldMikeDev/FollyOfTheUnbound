// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Extensions.ContextQuery;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.CSharp.Completion.KeywordRecommenders;

internal sealed class ElseKeywordRecommender() : AbstractSyntacticSingleKeywordRecommender(SyntaxKind.ElseKeyword, isValidInPreprocessorContext: true)
{
    protected override bool IsValidContext(int position, CSharpSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.IsPreProcessorKeywordContext)
        {
            return true;
        }

        var token = context.TargetToken;

        // We have to consider all ancestor if statements of the last token until we find a match for this 'else':
        // while (true)
        //     if (true)
        //         while (true)
        //             if (true)
        //                 Console.WriteLine();
        //             else
        //                 Console.WriteLine();
        //     $$
        foreach (var ifStatement in token.GetAncestors<IfStatementSyntax>())
        {
            // If there's a missing token at the end of the statement, it's incomplete and we do not offer 'else'.
            // context.TargetToken does not include zero width so in that case these will never be equal.
            if (ifStatement.Statement.GetLastToken(includeZeroWidth: true) == token)
            {
                return true;
            }
        }

        // Same idea, but for an if/catch/finally chain's arm-based representation: 'else' is only valid
        // right after the *last* arm's consequence (an 'else if' arm attaches its own trailing 'else' to
        // the next arm instead), and only if the chain doesn't already have a trailing 'else' clause.
        foreach (var ifCatchArm in token.GetAncestors<IfCatchArmSyntax>())
        {
            if (ifCatchArm.Consequence.GetLastToken(includeZeroWidth: true) == token &&
                ifCatchArm.Parent is IfCatchStatementSyntax { Else: null } ifCatchStatement &&
                ifCatchStatement.Arms[^1] == ifCatchArm)
            {
                return true;
            }
        }

        return false;
    }
}
