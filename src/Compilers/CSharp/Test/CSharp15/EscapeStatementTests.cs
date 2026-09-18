// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
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
}
