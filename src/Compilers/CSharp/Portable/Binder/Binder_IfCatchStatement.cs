// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class Binder
    {
        /// <summary>
        /// Binds an if/else-if/else chain with optional trailing catch/finally clauses
        /// (<see cref="IfCatchStatementSyntax"/>). This reuses the existing try/catch/finally binding
        /// infrastructure: the whole chain is wrapped in a <see cref="BoundTryStatement"/> when any
        /// catch/finally clause is present. Plain (parenthesized) conditions bind exactly like a classic
        /// `if` condition. Block conditions (<see cref="IfCatchArmSyntax.ConditionBlock"/>) are lowered by
        /// binding the block's statements followed by an `if (ifout.Value) ... else ...` -- accessing
        /// <c>Nullable&lt;bool&gt;.Value</c> throws when <c>ifout</c> was never assigned true/false, and that
        /// exception is naturally caught by the wrapping try.
        /// </summary>
        private BoundStatement BindIfCatchStatement(IfCatchStatementSyntax node, BindingDiagnosticBag diagnostics)
        {
            BoundStatement? alternative = node.Else != null
                ? BindPossibleEmbeddedStatement(node.Else.Statement, diagnostics)
                : null;

            bool anyBlockCondition = false;

            for (int i = node.Arms.Count - 1; i >= 0; i--)
            {
                var arm = node.Arms[i];

                if (arm.ConditionBlock != null)
                {
                    anyBlockCondition = true;

                    Binder armBinder = this.GetBinder(arm) ?? this;
                    BoundBlock boundConditionBlock = this.BindEmbeddedBlock(arm.ConditionBlock, diagnostics);

                    LocalSymbol ifoutLocal = armBinder.GetDeclaredLocalsForScope(arm)
                        .First(l => l.Name == IfOutLocalSymbol.IfOutName);

                    BoundExpression ifoutValue = MakeIfOutValueAccess(arm.ConditionBlock, ifoutLocal, diagnostics);

                    BoundBlock consequence = this.BindEmbeddedBlock(arm.Consequence, diagnostics);

                    BoundStatement inner = new BoundIfStatement(arm, ifoutValue, consequence, alternative);

                    alternative = new BoundBlock(
                        arm.ConditionBlock,
                        locals: ImmutableArray.Create(ifoutLocal),
                        localFunctions: ImmutableArray<MethodSymbol>.Empty,
                        hasUnsafeModifier: false,
                        instrumentation: null,
                        statements: ImmutableArray.Create<BoundStatement>(boundConditionBlock, inner));
                }
                else
                {
                    BoundExpression condition = this.BindBooleanExpression(arm.Condition!, diagnostics);
                    BoundBlock consequence = this.BindEmbeddedBlock(arm.Consequence, diagnostics);
                    alternative = new BoundIfStatement(arm, condition, consequence, alternative);
                }
            }

            BoundStatement chainResult = alternative
                ?? new BoundBlock(node, ImmutableArray<LocalSymbol>.Empty, ImmutableArray<MethodSymbol>.Empty, hasUnsafeModifier: false, instrumentation: null, statements: ImmutableArray<BoundStatement>.Empty);

            if (anyBlockCondition && node.Catches.Count == 0)
            {
                diagnostics.Add(ErrorCode.ERR_IfBlockConditionRequiresCatch, node.Location);
                return chainResult;
            }

            bool hasCatchesOrFinally = node.Catches.Count > 0 || node.Finally != null;
            if (!hasCatchesOrFinally)
            {
                return chainResult;
            }

            ImmutableArray<BoundCatchBlock> catchBlocks = this.BindCatchBlocks(node.Catches, diagnostics);
            BoundBlock? finallyBlockOpt = node.Finally != null ? this.BindEmbeddedBlock(node.Finally.Block, diagnostics) : null;

            BoundBlock tryBlock = chainResult as BoundBlock
                ?? new BoundBlock(node, ImmutableArray<LocalSymbol>.Empty, ImmutableArray<MethodSymbol>.Empty, hasUnsafeModifier: false, instrumentation: null, statements: ImmutableArray.Create(chainResult));

            return new BoundTryStatement(node, tryBlock, catchBlocks, finallyBlockOpt, finallyLabelOpt: null, preferFaultHandler: false);
        }

        /// <summary>
        /// Builds the expression <c>ifoutLocal.Value</c>, which throws <see cref="System.InvalidOperationException"/>
        /// when <paramref name="ifoutLocal"/> was never assigned (or was assigned <see langword="null"/>) -- exactly
        /// the semantics we want for "skip to catch" behavior of a block condition.
        /// </summary>
        private BoundExpression MakeIfOutValueAccess(SyntaxNode syntax, LocalSymbol ifoutLocal, BindingDiagnosticBag diagnostics)
        {
            var nullableBoolType = (NamedTypeSymbol)ifoutLocal.Type;

            BoundExpression ifoutRef = new BoundLocal(
                syntax,
                ifoutLocal,
                BoundLocalDeclarationKind.None,
                constantValueOpt: null,
                isNullableUnknown: false,
                type: nullableBoolType);

            MethodSymbol? nullableValueGetter = (MethodSymbol?)GetSpecialTypeMember(SpecialMember.System_Nullable_T_get_Value, diagnostics, syntax);
            if (nullableValueGetter is null)
            {
                return new BoundBadExpression(syntax, LookupResultKind.Empty, ImmutableArray<Symbol?>.Empty, ImmutableArray.Create(ifoutRef), Compilation.GetSpecialType(SpecialType.System_Boolean));
            }

            nullableValueGetter = nullableValueGetter.AsMember(nullableBoolType);

            return BoundCall.Synthesized(
                syntax: syntax,
                receiverOpt: ifoutRef,
                initialBindingReceiverIsSubjectToCloning: ThreeState.False,
                method: nullableValueGetter);
        }
    }
}
