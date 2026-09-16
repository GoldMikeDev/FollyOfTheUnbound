// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Semantics
{
    public class InlineExpressionDeclarationTests : CompilingTestBase
    {
        [Fact]
        public void BasicUsage_ObjectCreationFollowedByIdentifier()
        {
            // A bare `new-expr identifier;` as a top-level statement expression isn't itself one of
            // the statement-valid expression kinds (assignment/call/increment/decrement/await/new), so
            // it's used here as a local declaration's initializer, which is a perfectly ordinary
            // statement form.
            var text =
@"using System;
using System.Text;
class C
{
    static void Main()
    {
        StringBuilder result = new StringBuilder(""hi"") sb;
        Console.Write(sb.ToString());
    }
}";
            CompileAndVerify(source: text, expectedOutput: "hi");
        }

        [Fact]
        public void ScopeCheck_NotVisibleBeforeDeclarationPoint()
        {
            var text =
@"using System;
using System.Text;
class C
{
    static void Main()
    {
        Console.Write(sb.ToString());
        StringBuilder result = new StringBuilder(""hi"") sb;
    }
}";
            CreateCompilation(text).VerifyDiagnostics(
                // (7,23): error CS0841: Cannot use local variable 'sb' before it is declared
                Diagnostic(ErrorCode.ERR_VariableUsedBeforeDeclaration, "sb").WithArguments("sb").WithLocation(7, 23));
        }

        [Fact]
        public void Regression_RefSafety_EscapeScopeDerivedFromOperand()
        {
            // Before the fix, the inline-declared local's escape scope was hard-pinned to the
            // declaring block instead of derived from the operand's actual escape scope, so this
            // was incorrectly rejected with a span-escape diagnostic. After the fix it compiles clean.
            var text =
@"using System;
class C
{
    static Span<byte> Get(byte[] arr)
    {
        Span<byte> result = new Span<byte>(arr) span;
        return span;
    }
}";
            CreateCompilationWithSpan(text).VerifyDiagnostics();
        }
    }
}
