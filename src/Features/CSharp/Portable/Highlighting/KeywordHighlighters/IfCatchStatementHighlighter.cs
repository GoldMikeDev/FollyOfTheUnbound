// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Highlighting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.KeywordHighlighting;

[ExportHighlighter(LanguageNames.CSharp), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class IfCatchStatementHighlighter() : AbstractKeywordHighlighter<IfCatchStatementSyntax>(findInsideTrivia: false)
{
    protected override bool ContainsHighlightableToken(ref TemporaryArray<SyntaxToken> tokens)
        => tokens.Any(static t => t.Kind()
            is SyntaxKind.IfKeyword
            or SyntaxKind.ElseKeyword
            or SyntaxKind.CatchKeyword
            or SyntaxKind.FinallyKeyword
            or SyntaxKind.WhenKeyword);

    protected override void AddHighlights(
        IfCatchStatementSyntax ifCatchStatement, List<TextSpan> highlights, CancellationToken cancellationToken)
    {
        foreach (var arm in ifCatchStatement.Arms)
        {
            var elseKeyword = arm.ElseKeyword;
            if (elseKeyword.IsKind(SyntaxKind.None))
            {
                highlights.Add(arm.IfKeyword.Span);
            }
            else if (IfStatementHighlighter.OnlySpacesBetween(elseKeyword, arm.IfKeyword))
            {
                // Highlight both else and if tokens together if they are on the same line
                highlights.Add(TextSpan.FromBounds(elseKeyword.SpanStart, arm.IfKeyword.Span.End));
            }
            else
            {
                highlights.Add(elseKeyword.Span);
                highlights.Add(arm.IfKeyword.Span);
            }
        }

        if (ifCatchStatement.Else is { } elseClause)
            highlights.Add(elseClause.ElseKeyword.Span);

        foreach (var catchClause in ifCatchStatement.Catches)
        {
            highlights.Add(catchClause.CatchKeyword.Span);

            if (catchClause.Filter != null)
                highlights.Add(catchClause.Filter.WhenKeyword.Span);
        }

        if (ifCatchStatement.Finally != null)
            highlights.Add(ifCatchStatement.Finally.FinallyKeyword.Span);
    }
}
