// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Extensions.ContextQuery;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp.Completion.KeywordRecommenders;

internal sealed class UntilKeywordRecommender() : AbstractSyntacticSingleKeywordRecommender(SyntaxKind.UntilKeyword)
{
    protected override bool IsValidContext(int position, CSharpSyntaxContext context, CancellationToken cancellationToken)
    {
        // Unlike `while`, `until` can never begin a fresh statement -- it is only ever valid as the tail
        // of a `do <statement> until (...)` construct, so (unlike WhileKeywordRecommender) we deliberately
        // do NOT recommend it for every IsStatementContext/IsGlobalStatementContext position.

        // do {
        // } |

        // do {
        // } u|

        // The parser (ParseDoOrDoUntilStatement) parses `do` followed by any embedded statement -- braced
        // or not -- before deciding whether the tail is `while` or `until`; until that tail is parsed, the
        // statement comes back as a DoStatement with a missing WhileKeyword. So this is also valid
        // immediately after an unbraced embedded statement, not just after a block:

        // do
        //     Work();
        // |

        var token = context.TargetToken;

        if (token.Parent?.FirstAncestorOrSelf<DoStatementSyntax>() is { WhileKeyword.IsMissing: true } doStatement &&
            doStatement.Statement.GetLastToken() == token)
        {
            return true;
        }

        return false;
    }
}
