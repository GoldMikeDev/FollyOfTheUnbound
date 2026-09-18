// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.SplitOrMergeIfStatements;

namespace Microsoft.CodeAnalysis.CSharp.SplitOrMergeIfStatements;

[ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = PredefinedCodeRefactoringProviderNames.SplitIntoNestedIfStatements), Shared]
[ExtensionOrder(After = PredefinedCodeRefactoringProviderNames.InvertLogical, Before = PredefinedCodeRefactoringProviderNames.IntroduceVariable)]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class CSharpSplitIntoNestedIfStatementsCodeRefactoringProvider()
    : AbstractSplitIntoNestedIfStatementsCodeRefactoringProvider
{
    // Folly of the Unbound: splitting `if (a && b) S;` into `if (a) { if (b) S; }` introduces a new
    // block around S (via WithStatementInBlock). If S is (or contains, through a chain of further
    // unbraced embedded statements) a top-level `escape;`, that new block silently becomes its new
    // target -- the same hazard AddBraces/ConvertForEachToFor were fixed for. Suppress the
    // refactoring in that case.
    protected override bool IsSafeToRefactor(SyntaxNode ifOrElseIf)
        => ifOrElseIf is not IfStatementSyntax { Statement: { } statement } || !statement.ContainsTopLevelEscapeStatement();
}
