// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Semantics
{
    public class MutateStatementTests : CompilingTestBase
    {
        [Fact]
        public void BasicValidMutation_BoolToInt()
        {
            var text =
@"using System;
class C
{
    static void Main()
    {
        bool b = true;
        mutate b to int;
        Console.WriteLine(b);
    }
}";
            CompileAndVerify(source: text, expectedOutput: "1");
        }

        [Fact]
        public void ConditionalMutation_ReportsWarningButStillCompiles()
        {
            // int -> byte can fail at runtime for out-of-range values, so it's Conditional:
            // a warning, not an error, and the program still compiles and runs.
            var text =
@"using System;
class C
{
    static void Main()
    {
        int i = 300;
        mutate i to byte;
        Console.WriteLine(i);
    }
}";
            CreateCompilation(text).VerifyDiagnostics(
                Diagnostic(ErrorCode.WRN_MutationMayFail, "mutate i to byte;").WithArguments("i", "byte").WithLocation(7, 9));
        }

        // No genuine `NeverValid` (from, to) pair is reachable through ordinary source: per
        // MutationValidity.GetValidity, NeverValid is only returned when `from` or `to` is null,
        // which BindMutateStatement never observes for a successfully-bound local and a
        // successfully-bound type -- every real (from, to) SpecialType combination falls through
        // to AlwaysValid or Conditional. So this test is skipped rather than faked; see
        // src/Compilers/CSharp/Portable/Binder/MutationValidity.cs for the reachability argument.

        [Fact]
        public void Regression_NullableWalker_TracksMutatedLocalNullability()
        {
            // Before the fix, NullableWalker never tracked the mutate-declared local's nullable
            // state, so no CS8602 warning fired here. After the fix it should.
            var text =
@"#nullable enable
using System;
class C
{
    static void M()
    {
        string? s = null;
        mutate s to string;
        s.ToString();
    }
}";
            CreateCompilation(text).VerifyDiagnostics(
                Diagnostic(ErrorCode.HDN_UnusedUsingDirective, "using System;").WithLocation(2, 1),
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "s").WithLocation(9, 9));
        }

        [Fact]
        public void Regression_NullableWalker_MutationConversionResultIsNotSourceValue()
        {
            // object? -> string is a genuine runtime conversion (object.ToString()), not a same-type
            // re-view of the source local: its result is never null on success even though the source
            // local was maybe-null, so no CS8602 should fire on the mutated local afterward. Before the
            // fix, NullableWalker copied the source local's maybe-null state onto the mutated local for
            // this case too (treating every mutation like the same-type case tested above).
            var text =
@"#nullable enable
class C
{
    static void M()
    {
        object? value = null;
        mutate value to string;
        value.ToString();
    }
}";
            CreateCompilation(text).VerifyDiagnostics(
                Diagnostic(ErrorCode.WRN_MutationMayFail, "mutate value to string;").WithArguments("value", "string").WithLocation(7, 9));
        }

        [Fact]
        public void Regression_NullableWalker_ReferenceToReferenceMutationPreservesSourceNullability()
        {
            // Unlike object? -> string above, object? -> string[] (a different reference type, but not
            // 'string' itself) is NOT lowered through .ToString() -- LocalRewriter_MutateStatement.
            // BuildLoweredConversion falls through to a plain checked cast for this case, which either
            // throws or preserves the exact same reference (including its null-ness). So the mutated
            // local's nullability should track the source local here, unlike the ToString() case: CS8602
            // should still fire.
            //
            // Uses an array type (string[]) rather than a user-defined class as the mutation target: the
            // latter hits an unrelated, pre-existing MethodCompiler debug-assert
            // (assertBindIdentifierTargets) because Binder_MutateStatement binds the target Type via
            // BindType rather than the tracked BindExpression path the identifier-prediction pass expects
            // every plain-identifier type reference to go through -- a separate bug, out of scope here.
            var text =
@"#nullable enable
class C
{
    static void M()
    {
        object? value = null;
        mutate value to string[];
        value.ToString();
    }
}";
            CreateCompilation(text).VerifyDiagnostics(
                Diagnostic(ErrorCode.WRN_MutationMayFail, "mutate value to string[];").WithArguments("value", "string[]").WithLocation(7, 9),
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "value").WithLocation(8, 9));
        }

        [Fact]
        public void Regression_NullableWalker_BoxingMutationIsNeverNull()
        {
            // int -> object? boxes a non-null value type: the result can never be null even though the
            // mutated local's own declared type is annotated nullable, so no CS8602 should fire. Before
            // this fix, the opaque fallback derived the result's state from the target's own annotation
            // (MaybeNull for `object?`) instead of recognizing this as a never-null boxing conversion.
            var text =
@"#nullable enable
class C
{
    static void M()
    {
        int value = 1;
        mutate value to object?;
        value.ToString();
    }
}";
            CreateCompilation(text).VerifyDiagnostics();
        }

        [Fact]
        public void Regression_NullableWalker_BoxingNullableValueTypeMutationCanBeNull()
        {
            // Unlike int -> object? above, int? -> object? boxes a *nullable* value type: an empty
            // Nullable<T> boxes to an actual null reference, so (unlike the non-nullable-value-type
            // boxing case) this can genuinely be null and CS8602 should still fire. Before this fix, the
            // "boxing is never null" rule didn't exclude Nullable<T> sources and incorrectly suppressed
            // the warning here too.
            var text =
@"#nullable enable
class C
{
    static void M()
    {
        int? value = null;
        mutate value to object?;
        value.ToString();
    }
}";
            CreateCompilation(text).VerifyDiagnostics(
                Diagnostic(ErrorCode.WRN_NullReferenceReceiver, "value").WithLocation(8, 9));
        }
    }
}
