// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Recommendations;

[Trait(Traits.Feature, Traits.Features.KeywordRecommending)]
public sealed class ToKeywordRecommenderTests : KeywordRecommenderTests
{
    [Fact]
    public Task TestAfterMutateVariableName()
        => VerifyKeywordAsync(AddInsideMethod(
@"mutate x $$"));

    [Fact]
    public Task TestNotAfterOrdinaryIdentifierInExpression()
        => VerifyAbsenceAsync(AddInsideMethod(
@"int y = x $$"));

    [Fact]
    public Task TestNotAtEmptyStatement()
        => VerifyAbsenceAsync(AddInsideMethod(
@"$$"));

    [Fact]
    public Task TestNotInClass()
        => VerifyAbsenceAsync("""
            class C
            {
                $$
            }
            """);

    [Fact]
    public Task TestNotAtRoot_Interactive()
        => VerifyAbsenceAsync(SourceCodeKind.Script,
@"$$");
}
