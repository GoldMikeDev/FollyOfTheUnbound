// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Semantics
{
    public class DoUntilStatementTests : CompilingTestBase
    {
        [Fact]
        public void BasicExecution_RunsUntilConditionBecomesTrue()
        {
            var text =
@"using System;
class C
{
    static void Main()
    {
        int i = 0;
        do
        {
            i++;
        } until (i >= 3);
        Console.Write(i);
    }
}";
            CompileAndVerify(source: text, expectedOutput: "3");
        }

        [Fact]
        public void BreakExitsLoopImmediately()
        {
            var text =
@"using System;
class C
{
    static void Main()
    {
        int i = 0;
        do
        {
            i++;
            if (i == 2)
            {
                break;
            }
        } until (i >= 10);
        Console.Write(i);
    }
}";
            CompileAndVerify(source: text, expectedOutput: "2");
        }

        [Fact]
        public void ContinueJumpsToUntilConditionCheck()
        {
            // continue should jump to the until-condition check, not past it -- if it
            // incorrectly skipped the condition entirely this would loop forever or misbehave.
            var text =
@"using System;
class C
{
    static void Main()
    {
        int i = 0;
        int sum = 0;
        do
        {
            i++;
            if (i % 2 == 0)
            {
                continue;
            }
            sum += i;
        } until (i >= 5);
        Console.Write(sum);
    }
}";
            // i: 1 (sum=1), 2 (continue), 3 (sum=4), 4 (continue), 5 (sum=9, then condition true)
            CompileAndVerify(source: text, expectedOutput: "9");
        }

        [Fact]
        public void DefiniteAssignment_BodyRunsAtLeastOnce()
        {
            // do/until always runs the body at least once, so a variable only assigned inside
            // the body is definitely assigned after the loop -- no CS0165.
            var text =
@"using System;
class C
{
    static void Main()
    {
        int x;
        int i = 0;
        do
        {
            x = i;
            i++;
        } until (i >= 1);
        Console.Write(x);
    }
}";
            CompileAndVerify(source: text, expectedOutput: "0");
        }

        [Fact]
        public void CS0029ERR_NoImplicitConv_ConditionMustBeBool()
        {
            var text =
@"
class C
{
    static void Main()
    {
        int i = 10;
        do
        {
            i = i - 1;
        } until (i);
    }
}
";
            CreateCompilation(text).VerifyDiagnostics(
                Diagnostic(ErrorCode.ERR_NoImplicitConv, "i").WithArguments("int", "bool"));
        }
    }
}
