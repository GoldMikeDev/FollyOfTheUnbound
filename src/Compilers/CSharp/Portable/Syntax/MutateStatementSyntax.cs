// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp.Syntax
{
    public partial class MutateStatementSyntax
    {
        public MutateStatementSyntax Update(SyntaxToken mutateKeyword, IdentifierNameSyntax variableName, SyntaxToken toKeyword, TypeSyntax type, SyntaxToken semicolonToken)
            => Update(AttributeLists, mutateKeyword, variableName, toKeyword, type, semicolonToken);
    }
}

namespace Microsoft.CodeAnalysis.CSharp
{
    public partial class SyntaxFactory
    {
        public static MutateStatementSyntax MutateStatement(SyntaxToken mutateKeyword, IdentifierNameSyntax variableName, SyntaxToken toKeyword, TypeSyntax type, SyntaxToken semicolonToken)
            => MutateStatement(attributeLists: default, mutateKeyword, variableName, toKeyword, type, semicolonToken);
    }
}
