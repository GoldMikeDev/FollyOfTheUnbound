// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Recommendations;

[Trait(Traits.Feature, Traits.Features.KeywordRecommending)]
public sealed class UntilKeywordRecommenderTests : KeywordRecommenderTests
{
    [Fact]
    public Task TestAfterDoBlock()
        => VerifyKeywordAsync(AddInsideMethod(
            """
            do {
            } $$
            """));

    [Fact]
    public Task TestNotAtEmptyStatement()
        => VerifyAbsenceAsync(AddInsideMethod(
@"$$"));

    [Fact]
    public Task TestNotAfterOrdinaryStatement()
        => VerifyAbsenceAsync(AddInsideMethod(
            """
            int i = 0;
            $$
            """));

    [Fact]
    public Task TestNotAfterWhileBlock()
        => VerifyAbsenceAsync(AddInsideMethod(
            """
            while (true) {
            } $$
            """));

    [Fact]
    public Task TestNotAfterIfBlock()
        => VerifyAbsenceAsync(AddInsideMethod(
            """
            if (true) {
            } $$
            """));

    [Fact]
    public Task TestNotAfterDo()
        => VerifyAbsenceAsync(AddInsideMethod(
            """
            do
                Console.WriteLine();
            $$
            """));

    [Fact]
    public Task TestNotAtRoot_Interactive()
        => VerifyAbsenceAsync(SourceCodeKind.Script,
@"$$");

    [Fact]
    public Task TestNotAfterClass_Interactive()
        => VerifyAbsenceAsync(SourceCodeKind.Script,
            """
            class C { }
            $$
            """);

    [Fact]
    public Task TestNotInUsingAlias()
        => VerifyAbsenceAsync(
@"using Goo = $$");

    [Fact]
    public Task TestNotInGlobalUsingAlias()
        => VerifyAbsenceAsync(
@"global using Goo = $$");

    [Fact]
    public Task TestNotInClass()
        => VerifyAbsenceAsync("""
            class C
            {
              $$
            }
            """);
}
