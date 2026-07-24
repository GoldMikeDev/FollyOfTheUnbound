// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        // BoundVoidCoalesceExpression only ever appears as the direct expression of a BoundExpressionStatement --
        // the binder never lets it surface anywhere else, since it's void-typed like any other void expression.
        // RewriteExpressionStatement intercepts it before generic expression visiting would reach this override.
        public override BoundNode VisitVoidCoalesceExpression(BoundVoidCoalesceExpression node)
        {
            throw ExceptionUtilities.Unreachable();
        }

        /// <summary>
        /// Lowers <c>receiver?.VoidMethod(...) ?? fallback;</c> to:
        /// <code>
        /// { T temp = receiver; if (temp != null) { temp.VoidMethod(...); } else { fallback; } }
        /// </code>
        /// The receiver is always captured in a temp (rather than reusing the existing
        /// LocalRewriter_ConditionalAccess.cs perf optimizations for trivially-repeatable receivers) for
        /// simplicity -- this shorthand is only bound for reference-type receivers, so no Nullable&lt;T&gt;
        /// HasValue dance is needed here, unlike BoundLoweredConditionalAccess.
        /// </summary>
        private BoundStatement RewriteVoidCoalesceExpressionStatement(BoundExpressionStatement node, BoundVoidCoalesceExpression voidCoalesce)
        {
            BoundConditionalAccess access = voidCoalesce.Access;

            BoundExpression loweredReceiver = VisitExpression(access.Receiver);
            TypeSymbol receiverType = loweredReceiver.Type!;

            LocalSymbol temp = _factory.SynthesizedLocal(receiverType, access.Receiver.Syntax);
            BoundStatement tempAssignment = _factory.ExpressionStatement(_factory.AssignmentExpression(_factory.Local(temp), loweredReceiver));

            BoundExpression? previousTarget = _currentConditionalAccessTarget;
            int previousId = _currentConditionalAccessID;
            _currentConditionalAccessTarget = _factory.Local(temp);
            ++_currentConditionalAccessID;
            BoundExpression loweredAccessExpression = VisitExpression(access.AccessExpression);
            _currentConditionalAccessTarget = previousTarget;
            _currentConditionalAccessID = previousId;

            BoundExpression? loweredFallback = VisitUnusedExpression(voidCoalesce.WhenNull);
            RoslynDebug.Assert(loweredFallback is not null);

            BoundStatement consequence = _factory.ExpressionStatement(loweredAccessExpression);
            BoundStatement alternative = _factory.ExpressionStatement(loweredFallback);
            BoundExpression condition = _factory.ObjectNotEqual(_factory.Local(temp), _factory.Null(receiverType));

            // BoundIfStatement is marked DoesNotSurvive="LocalRewriting" -- it must never appear in the tree
            // once lowering is done, so build the goto/label form directly (mirroring
            // LocalRewriter_IfStatement.VisitIfStatement's own if/else shape) instead of constructing one.
            //
            //   GotoIfFalse condition, alt;
            //   consequence;
            //   goto afterif;
            //   alt:
            //   alternative;
            //   afterif:
            var altLabel = new GeneratedLabelSymbol("alternative");
            var afterIfLabel = new GeneratedLabelSymbol("afterif");

            BoundStatement ifStatement = new BoundStatementList(node.Syntax, ImmutableArray.Create(
                new BoundConditionalGoto(condition.Syntax, condition, jumpIfTrue: false, altLabel),
                consequence,
                new BoundGotoStatement(node.Syntax, afterIfLabel),
                new BoundLabelStatement(node.Syntax, altLabel),
                alternative,
                new BoundLabelStatement(node.Syntax, afterIfLabel)));

            BoundStatement result = new BoundBlock(
                node.Syntax,
                ImmutableArray.Create(temp),
                ImmutableArray.Create(tempAssignment, ifStatement));

            if (this.Instrument && !node.WasCompilerGenerated)
            {
                result = Instrumenter.InstrumentExpressionStatement(node, result);
            }

            return result;
        }
    }
}
