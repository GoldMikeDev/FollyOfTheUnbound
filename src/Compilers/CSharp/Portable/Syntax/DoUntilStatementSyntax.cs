// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp.Syntax
{
    public partial class DoUntilStatementSyntax
    {
        public DoUntilStatementSyntax Update(SyntaxToken doKeyword, StatementSyntax statement, SyntaxToken untilKeyword, SyntaxToken openParenToken, ExpressionSyntax condition, SyntaxToken closeParenToken, SyntaxToken semicolonToken)
            => Update(AttributeLists, doKeyword, statement, untilKeyword, openParenToken, condition, closeParenToken, semicolonToken);
    }
}

namespace Microsoft.CodeAnalysis.CSharp
{
    public partial class SyntaxFactory
    {
        public static DoUntilStatementSyntax DoUntilStatement(SyntaxToken doKeyword, StatementSyntax statement, SyntaxToken untilKeyword, SyntaxToken openParenToken, ExpressionSyntax condition, SyntaxToken closeParenToken, SyntaxToken semicolonToken)
            => DoUntilStatement(attributeLists: default, doKeyword, statement, untilKeyword, openParenToken, condition, closeParenToken, semicolonToken);
    }
}
