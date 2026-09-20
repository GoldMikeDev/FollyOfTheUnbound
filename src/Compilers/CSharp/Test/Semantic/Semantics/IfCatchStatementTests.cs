// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Semantics
{
    public class IfCatchStatementTests : CompilingTestBase
    {
        [Fact]
        public void ClassicForm_NoException_TakesIfBranch()
        {
            var text =
@"using System;
class C
{
    static void Main()
    {
        if (true) { Console.Write(""A""); } catch (Exception) { Console.Write(""B""); }
    }
}";
            CompileAndVerify(source: text, expectedOutput: "A");
        }

        [Fact]
        public void ClassicForm_ExceptionThrownByIfBody_IsCaught()
        {
            var text =
@"using System;
class C
{
    static void Main()
    {
        if (true) { throw new InvalidOperationException(); } catch (InvalidOperationException) { Console.Write(""caught""); }
    }
}";
            CompileAndVerify(source: text, expectedOutput: "caught");
        }

        [Fact]
        public void BlockConditionForm_ConditionPasses_TakesConsequence()
        {
            var text =
@"using System;
class C
{
    static void Main()
    {
        if { var x = 5; ifout = x > 0; } { Console.Write(""pos""); } catch (Exception) { Console.Write(""err""); }
    }
}";
            CompileAndVerify(source: text, expectedOutput: "pos");
        }

        [Fact]
        public void BlockConditionForm_UnassignedIfOut_FallsThroughToCatch()
        {
            // 'ifout' is a bool? local that is never assigned in the condition block, so accessing
            // '.Value' throws InvalidOperationException -- confirming the "skip to catch on condition
            // failure" semantics.
            var text =
@"using System;
class C
{
    static void Main()
    {
        if { var unused = 1; } { Console.Write(""pos""); } catch (InvalidOperationException) { Console.Write(""err""); }
    }
}";
            CompileAndVerify(source: text, expectedOutput: "err");
        }

        [Fact]
        public void CS10000ERR_IfBlockConditionRequiresCatch_NoAttachedCatchOrFinally()
        {
            var text =
@"
class C
{
    static void M()
    {
        if { ifout = true; } { }
    }
}
";
            CreateCompilation(text).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_IfBlockConditionRequiresCatch, @"if { ifout = true; } { }").WithLocation(6, 9));
        }

        [Fact]
        public void Regression_FinallyStillBoundWhenDiagnosticFires()
        {
            // Before the fix, a block-condition arm with a 'finally' but no 'catch' (which triggers
            // ERR_IfBlockConditionRequiresCatch) never had its 'finally' block bound at all, so the
            // type error inside it (CS0029) was silently never diagnosed. After the fix, both
            // diagnostics should be reported.
            var text =
@"
class C
{
    static void M()
    {
        if { ifout = true; } { }
        finally { int x = ""not an int""; }
    }
}
";
            CreateCompilation(text).VerifyDiagnostics(
                // (6,9): error CS10000: An 'if' arm with a block condition requires an attached 'catch' clause
                Diagnostic(ErrorCode.ERR_IfBlockConditionRequiresCatch, @"if { ifout = true; } { }
        finally { int x = ""not an int""; }").WithLocation(6, 9),
                // (7,27): error CS0029: Cannot implicitly convert type 'string' to 'int'
                Diagnostic(ErrorCode.ERR_NoImplicitConv, @"""not an int""").WithArguments("string", "int").WithLocation(7, 27));
        }

        [Fact]
        public void Regression_ClassicFormNonBlockConsequence_DoesNotCrashBinderFactory()
        {
            // Before the fix, LocalBinderFactory.VisitIfCatchArm called Visit(node.Consequence, ...)
            // instead of VisitPossibleEmbeddedStatement(node.Consequence, ...) for the classic
            // (non-block-condition) form's non-block consequence, so no binder was ever registered for
            // it -- Binder.BindPossibleEmbeddedStatement's GetBinder(node) lookup came back null and hit
            // a debug-only Assert (a real crash in any semantic-analysis consumer, including this exact
            // "add braces" scenario). Verified here via a compile + semantic model pull (matching how the
            // AddBraces analyzer reaches this code) rather than just VerifyDiagnostics, since the crash
            // is in binder-factory setup rather than in error reporting itself.
            var text =
@"using System;
class C
{
    static void M(bool cond)
    {
        if (cond) DoWork(); catch (Exception e) { Handle(e); }
    }
    static void DoWork() { }
    static void Handle(Exception e) { }
}";
            var compilation = CreateCompilation(text);
            var tree = compilation.SyntaxTrees[0];
            var model = compilation.GetSemanticModel(tree);
            _ = model.GetDiagnostics();
            compilation.VerifyDiagnostics();
        }
    }
}
