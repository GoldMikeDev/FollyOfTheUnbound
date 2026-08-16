// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests
{
    /// <summary>
    /// Regression tests for the `receiver?.Call(...) ?? fallback;` void-coalescing shorthand's
    /// receiver-type split: the statement-level interception in Binder_Statements only takes over
    /// for reference-type receivers; a value-type (<c>Nullable&lt;T&gt;</c>) receiver must fall
    /// through to ordinary `??` binding and report
    /// <see cref="ErrorCode.ERR_VoidCoalesceRequiresReferenceTypeReceiver"/>.
    /// </summary>
    public class VoidCoalesceTests : CSharpTestBase
    {
        [Fact]
        public void ReferenceTypeReceiver_BindsSuccessfully()
        {
            var source = @"
class C
{
    void M() { }
    void Test(C c)
    {
        c?.M() ?? System.Console.WriteLine();
    }
}";

            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();
        }

        [Fact]
        public void NullableValueTypeReceiver_ReportsReferenceTypeRequiredError()
        {
            var source = @"
struct S
{
    public void M() { }
}
class C
{
    void Test(S? s)
    {
        s?.M() ?? System.Console.WriteLine();
    }
}";

            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics(
                // (10,9): error CS10004: The 'expr?.Member(...) ?? fallback' void-coalescing shorthand requires the receiver of '?.' to be a reference type.
                //         s?.M() ?? System.Console.WriteLine();
                Diagnostic(ErrorCode.ERR_VoidCoalesceRequiresReferenceTypeReceiver, "s?.M()").WithLocation(10, 9));
        }
    }
}
