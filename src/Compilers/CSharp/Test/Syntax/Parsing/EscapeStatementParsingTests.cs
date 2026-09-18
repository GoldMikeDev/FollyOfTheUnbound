// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests;

public sealed class EscapeStatementParsingTests : ParsingTests
{
    public EscapeStatementParsingTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void Escape_Statement_Parses()
    {
        UsingStatement("escape;");
        N(SyntaxKind.EscapeStatement);
        {
            N(SyntaxKind.IdentifierToken, "escape");
            N(SyntaxKind.SemicolonToken);
        }
        EOF();
    }

    [Fact]
    public void Escape_AsMethodCall_DoesNotParseAsEscapeStatement()
    {
        // Only "escape" immediately followed by ';' is the escape statement; "escape();" must
        // still parse as an ordinary invocation-expression statement.
        UsingStatement("escape();");
        N(SyntaxKind.ExpressionStatement);
        {
            N(SyntaxKind.InvocationExpression);
            {
                N(SyntaxKind.IdentifierName);
                {
                    N(SyntaxKind.IdentifierToken, "escape");
                }
                N(SyntaxKind.ArgumentList);
                {
                    N(SyntaxKind.OpenParenToken);
                    N(SyntaxKind.CloseParenToken);
                }
            }
            N(SyntaxKind.SemicolonToken);
        }
        EOF();
    }

    [Fact]
    public void Escape_AsAssignmentTarget_DoesNotParseAsEscapeStatement()
    {
        UsingStatement("escape = 1;");
        N(SyntaxKind.ExpressionStatement);
        {
            N(SyntaxKind.SimpleAssignmentExpression);
            {
                N(SyntaxKind.IdentifierName);
                {
                    N(SyntaxKind.IdentifierToken, "escape");
                }
                N(SyntaxKind.EqualsToken);
                N(SyntaxKind.NumericLiteralExpression);
                {
                    N(SyntaxKind.NumericLiteralToken, "1");
                }
            }
            N(SyntaxKind.SemicolonToken);
        }
        EOF();
    }

    [Fact]
    public void EscapeVerbatim_DoesNotParseAsEscapeStatement()
    {
        // PR #96 review: `@escape;` is the verbatim-identifier escape hatch (same as `@if`, `@mutate`,
        // etc.) and must parse as a plain identifier-expression statement, not EscapeStatementSyntax --
        // the parser must check the token's raw Text ("@escape"), not its unescaped ValueText
        // ("escape"), when deciding whether this is the escape statement.
        UsingStatement("@escape;");
        N(SyntaxKind.ExpressionStatement);
        {
            N(SyntaxKind.IdentifierName);
            {
                N(SyntaxKind.IdentifierToken, "@escape");
            }
            N(SyntaxKind.SemicolonToken);
        }
        EOF();
    }
}
