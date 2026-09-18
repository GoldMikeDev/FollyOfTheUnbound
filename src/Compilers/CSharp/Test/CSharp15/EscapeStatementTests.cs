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
}
