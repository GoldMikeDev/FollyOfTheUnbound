// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Semantics;

/// <summary>
/// Tests for this fork's <c>escape;</c> statement: jumps to the end of the nearest enclosing
/// block (including <c>catch</c>/<c>finally</c> bodies).
/// </summary>
public sealed class EscapeStatementTests : CSharpTestBase
{
    // Tests that target Net100 can only execute on a runtime that supports it. On other hosts
    // (e.g. net472 CI) the emitted assembly references Net 10 assemblies that cannot be loaded
    // for execution, so skip the runtime check by passing null for the expected output.
    private static string? IncludeExpectedOutput(string expectedOutput) =>
        ExecutionConditionUtil.IsMonoOrCoreClr ? expectedOutput : null;

    [Fact]
    public void Escape_PlainBlock_SkipsRestOfBlock()
    {
        var source = """
            System.Console.Write("A ");
            {
                System.Console.Write("B ");
                escape;
                System.Console.Write("SKIPPED ");
            }
            System.Console.Write("C");
            """;
        CompileAndVerify(source, expectedOutput: IncludeExpectedOutput("A B C")).VerifyDiagnostics(
            // (5,5): warning CS0162: Unreachable code detected
            //     System.Console.Write("SKIPPED ");
            Diagnostic(ErrorCode.WRN_UnreachableCode, "System").WithLocation(5, 5));
    }

    [Fact]
    public void Escape_NestedBlock_TargetsNearestOnly()
    {
        var source = """
            {
                System.Console.Write("outer1 ");
                {
                    System.Console.Write("inner1 ");
                    escape;
                    System.Console.Write("SKIPPED ");
                }
                System.Console.Write("outer2");
            }
            """;
        CompileAndVerify(source, expectedOutput: IncludeExpectedOutput("outer1 inner1 outer2")).VerifyDiagnostics(
            // (6,9): warning CS0162: Unreachable code detected
            //         System.Console.Write("SKIPPED ");
            Diagnostic(ErrorCode.WRN_UnreachableCode, "System").WithLocation(6, 9));
    }

    [Fact]
    public void Escape_TryBody_StillRunsFinally()
    {
        var source = """
            class C
            {
                static void Main()
                {
                    try
                    {
                        System.Console.Write("try ");
                        escape;
                        System.Console.Write("SKIPPED ");
                    }
                    finally
                    {
                        System.Console.Write("finally ");
                    }
                    System.Console.Write("after");
                }
            }
            """;
        CompileAndVerify(source, expectedOutput: IncludeExpectedOutput("try finally after")).VerifyDiagnostics(
            // (9,13): warning CS0162: Unreachable code detected
            //             System.Console.Write("SKIPPED ");
            Diagnostic(ErrorCode.WRN_UnreachableCode, "System").WithLocation(9, 13));
    }

    [Fact]
    public void Escape_FinallyBody_EndsFinallyEarly()
    {
        var source = """
            class C
            {
                static void Main()
                {
                    try
                    {
                        System.Console.Write("try ");
                    }
                    finally
                    {
                        System.Console.Write("finally ");
                        escape;
                        System.Console.Write("SKIPPED ");
                    }
                    System.Console.Write("after");
                }
            }
            """;
        CompileAndVerify(source, expectedOutput: IncludeExpectedOutput("try finally after")).VerifyDiagnostics(
            // (13,13): warning CS0162: Unreachable code detected
            //             System.Console.Write("SKIPPED ");
            Diagnostic(ErrorCode.WRN_UnreachableCode, "System").WithLocation(13, 13));
    }

    [Fact]
    public void Escape_CatchBody_EndsCatchEarly()
    {
        var source = """
            class C
            {
                static void Main()
                {
                    try
                    {
                        throw new System.InvalidOperationException();
                    }
                    catch (System.InvalidOperationException)
                    {
                        System.Console.Write("catch ");
                        escape;
                        System.Console.Write("SKIPPED ");
                    }
                    System.Console.Write("after");
                }
            }
            """;
        CompileAndVerify(source, expectedOutput: IncludeExpectedOutput("catch after")).VerifyDiagnostics(
            // (13,13): warning CS0162: Unreachable code detected
            //             System.Console.Write("SKIPPED ");
            Diagnostic(ErrorCode.WRN_UnreachableCode, "System").WithLocation(13, 13));
    }

    [Fact]
    public void Escape_AsIdentifier_StillBindsAsExpression()
    {
        // "escape" followed by anything other than ';' is not the escape statement -- it must
        // still be usable as an ordinary identifier (method/local name).
        var source = """
            class C
            {
                static void escape() => System.Console.Write("called");
                static void Main() => escape();
            }
            """;
        CompileAndVerify(source, expectedOutput: IncludeExpectedOutput("called")).VerifyDiagnostics();
    }

    [Fact]
    public void Escape_BeforeUsingDeclaration_SkipsDeclarationAndDisposesNothing()
    {
        // A regression test for https://github.com/GoldMikeDev/FollyOfTheUnbound/pull/96: an
        // `escape;` that precedes a `using var` declaration in the same block must be able to
        // jump past it (like `break`/`return` legitimately can) without the compiler reporting
        // CS8641 (ERR_GoToForwardJumpOverUsingVar) and without ever constructing/disposing the
        // resource, since the declaration is never reached.
        var source = """
            class D : System.IDisposable
            {
                public void Dispose() => System.Console.Write("SKIPPED-DISPOSE ");
            }

            class C
            {
                static void Main()
                {
                    System.Console.Write("A ");
                    {
                        System.Console.Write("B ");
                        escape;
                        using var d = new D();
                        System.Console.Write("SKIPPED ");
                    }
                    System.Console.Write("C");
                }
            }
            """;
        CompileAndVerify(source, expectedOutput: IncludeExpectedOutput("A B C")).VerifyDiagnostics(
            // (14,13): warning CS0162: Unreachable code detected
            //             using var d = new D();
            Diagnostic(ErrorCode.WRN_UnreachableCode, "using").WithLocation(14, 13));
    }

    [Fact]
    public void Escape_BeforeUsingDeclaration_ConditionallyReached_DisposesCorrectly()
    {
        // Same shape as above, but the `escape;` is conditional, so both the "jump past the
        // using declaration" path and the "reach and dispose the resource normally" path are
        // exercised in the same compiled method.
        // Note: "if (escapeEarly) escape;" (no braces) is deliberate -- `escape;` always targets
        // the *nearest* enclosing `{ }` block (see the type's doc comment), so wrapping it in the
        // `if`'s own braces would make it target that inner block instead of the outer one that
        // contains the using declaration, defeating the point of this test.
        var source = """
            class D : System.IDisposable
            {
                public void Dispose() => System.Console.Write("disposed ");
            }

            class C
            {
                static void M(bool escapeEarly)
                {
                    System.Console.Write("A ");
                    {
                        if (escapeEarly) escape;
                        using var d = new D();
                        System.Console.Write("used ");
                    }
                    System.Console.Write("C ");
                }

                static void Main()
                {
                    M(escapeEarly: true);
                    M(escapeEarly: false);
                }
            }
            """;
        CompileAndVerify(source, expectedOutput: IncludeExpectedOutput("A C A used disposed C ")).VerifyDiagnostics();
    }

    [Fact]
    public void Escape_SpeculativeBind_DoesNotMutateRealBlockBinder()
    {
        // PR #96 review: SemanticModel.TryGetSpeculativeSemanticModel for a synthetic `escape;`
        // reused the *real* enclosing BlockBinder chain. The real (old) BlockBinder.EscapeLabel
        // getter lazily allocated and cached its label directly on that real, long-lived binder as a
        // side effect of answering the speculative query -- even though the real source has no
        // `escape;` at all. A later real bind of the same block then saw the (spuriously) allocated
        // label and would append an unused synthesized label statement nobody's `escape;` asked for.
        //
        // This is exercised directly against BlockBinder.GetEscapeLabel (rather than through
        // SemanticModel.GetDiagnostics()/Compilation.GetDiagnostics(), which in this codebase's
        // binder-caching architecture turn out to each construct their own independent binder chain
        // for a method body -- meaning the corruption doesn't happen to surface through *those*
        // specific entry points, even though the underlying real BlockBinder instance obtained via
        // SemanticModel.GetEnclosingBinder -- the same one TryGetSpeculativeSemanticModelCore reuses
        // -- absolutely does get corrupted, as this test demonstrates. A binder-caching layer other
        // than the two checked here (e.g. an IDE's own reuse of GetEnclosingBinder's result across a
        // completion/quick-info session) could very plausibly observe the corrupted state.) This test
        // reproduces the exact mechanism: obtain the real per-block Binder chain the same way
        // TryGetSpeculativeSemanticModelCore does (SemanticModel.GetEnclosingBinder), then simulate
        // the speculative bind's call with a free-standing (non-descendant) `escape;` node.
        var source = """
            class C
            {
                void M()
                {
                    System.Console.Write("no escape here");
                }
            }
            """;
        var comp = CreateCompilation(source);
        var tree = comp.SyntaxTrees.Single();
        var model = (CSharpSemanticModel)comp.GetSemanticModel(tree);

        var block = tree.GetRoot().DescendantNodes().OfType<BlockSyntax>().Single(b => b.Statements.Count == 1);
        var position = block.Statements[0].SpanStart;

        var binder = model.GetEnclosingBinder(position);
        var blockBinder = Assert.IsType<BlockBinder>(FindBlockBinder(binder));

        // Sanity: nothing has asked for the escape label yet.
        Assert.Null(blockBinder.EscapeLabelIfAllocated);

        // A free-standing `escape;` node -- structurally identical to what
        // TryGetSpeculativeSemanticModelCore hands to the real enclosing binder chain for a
        // speculative bind -- must NOT be treated as a descendant of the real block, and must not
        // mutate/cache onto it.
        var foreignEscape = SyntaxFactory.ParseStatement("escape;");
        Assert.NotSame(block, FindAncestorOfKind(foreignEscape, SyntaxKind.Block));

        var speculativeLabel = blockBinder.GetEscapeLabel(foreignEscape);
        Assert.NotNull(speculativeLabel);

        // The real, long-lived BlockBinder must be untouched by that foreign query.
        Assert.Null(blockBinder.EscapeLabelIfAllocated);

        // Calling it again for another foreign node gets its own fresh label each time (no caching
        // for foreign nodes at all), confirming nothing was cached under the hood.
        var anotherForeignEscape = SyntaxFactory.ParseStatement("escape;");
        var anotherSpeculativeLabel = blockBinder.GetEscapeLabel(anotherForeignEscape);
        Assert.NotSame(speculativeLabel, anotherSpeculativeLabel);

        // Control case: a real `escape;` actually lexically inside the block DOES get cached (this is
        // the legitimate, intended behavior the fix must not have broken).
        var realEscapeSource = """
            class C
            {
                void M()
                {
                    escape;
                }
            }
            """;
        var realComp = CreateCompilation(realEscapeSource);
        var realTree = realComp.SyntaxTrees.Single();
        var realModel = (CSharpSemanticModel)realComp.GetSemanticModel(realTree);
        var realBlock = realTree.GetRoot().DescendantNodes().OfType<BlockSyntax>().Single(b => b.Statements.Count == 1);
        var realEscapeStatement = realBlock.Statements[0];
        var realBlockBinder = Assert.IsType<BlockBinder>(FindBlockBinder(realModel.GetEnclosingBinder(realEscapeStatement.SpanStart)));

        Assert.Null(realBlockBinder.EscapeLabelIfAllocated);
        var realLabel1 = realBlockBinder.GetEscapeLabel(realEscapeStatement);
        Assert.NotNull(realBlockBinder.EscapeLabelIfAllocated);
        var realLabel2 = realBlockBinder.GetEscapeLabel(realEscapeStatement);
        Assert.Same(realLabel1, realLabel2);
    }

    private static Binder FindBlockBinder(Binder binder)
    {
        for (var current = binder; current is not null; current = current.Next)
        {
            if (current is BlockBinder)
                return current;
        }

        throw ExceptionUtilities.Unreachable();
    }

    private static SyntaxNode? FindAncestorOfKind(SyntaxNode node, SyntaxKind kind)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current.IsKind(kind))
                return current;
        }

        return null;
    }
}
