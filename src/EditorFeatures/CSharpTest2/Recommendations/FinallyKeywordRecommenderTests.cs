// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Recommendations;

[Trait(Traits.Feature, Traits.Features.KeywordRecommending)]
public sealed class FinallyKeywordRecommenderTests : KeywordRecommenderTests
{
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
    public Task TestNotAfterGlobalStatement_Interactive()
        => VerifyAbsenceAsync(SourceCodeKind.Script,
            """
            System.Console.WriteLine();
            $$
            """);

    [Fact]
    public Task TestNotAfterGlobalVariableDeclaration_Interactive()
        => VerifyAbsenceAsync(SourceCodeKind.Script,
            """
            int i = 0;
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
    public Task TestNotInEmptyStatement()
        => VerifyAbsenceAsync(AddInsideMethod(
@"$$"));

    [Fact]
    public Task TestAfterTry()
        => VerifyKeywordAsync(AddInsideMethod(
            """
            try {
            } $$
            """));

    [Fact]
    public Task TestAfterTryCatch()
        => VerifyKeywordAsync(AddInsideMethod(
            """
            try {
            } catch {
            } $$
            """));

    [Fact]
    public Task TestNotAfterFinallyBlock()
        => VerifyAbsenceAsync(AddInsideMethod(
            """
            try {
            } finally {
            } $$
            """));

    [Fact]
    public Task TestNotInStatement()
        => VerifyAbsenceAsync(AddInsideMethod(
@"$$"));

    [Fact]
    public Task TestNotAfterBlock()
        => VerifyAbsenceAsync(AddInsideMethod(
            """
            if (true)
            {
                Console.WriteLine();
            }
            $$
            """));

    [Fact]
    public Task TestNotAfterFinallyKeyword()
        => VerifyAbsenceAsync(AddInsideMethod(
            """
            try {
            } finally $$
            """));

    [Fact]
    public Task TestNotInClass()
        => VerifyAbsenceAsync("""
            class C {
                $$
            }
            """);

    [Fact]
    public Task TestAfterIfCatchChainBlockConditionArmWithTrailingCatch()
        // A block-condition arm (`if { ... } { ... }`) is always parsed as an if/catch chain
        // (IfCatchStatementSyntax) regardless of whether a catch/finally is present yet -- unlike the
        // classic parenthesized form, which only becomes an if/catch chain once a catch/finally is
        // already there (see ParseIfStatementOrIfCatchStatement). So 'finally' is offered here even
        // before typing it, same as for a real try-block.
        => VerifyKeywordAsync(AddInsideMethod(
            """
            if {
                var b = true;
                ifout = b;
            }
            {
            } $$
            catch (System.Exception e) { }
            """));

    [Fact]
    public Task TestNotAfterOrdinaryIfBlockNoTrailingCatchOrFinally()
        // A plain if-block with nothing else present stays a classic IfStatementSyntax (matching
        // TestNotAfterBlock above) -- catch/finally are deliberately not offered speculatively here,
        // unlike 'else', to avoid suggesting them after every ordinary if-statement in existing code.
        => VerifyAbsenceAsync(AddInsideMethod(
            """
            if (true) {
            } $$
            """));
}
