// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        public override BoundNode VisitDoUntilStatement(BoundDoUntilStatement node)
        {
            Debug.Assert(node != null);

            var rewrittenCondition = VisitExpression(node.Condition);
            var rewrittenBody = VisitStatement(node.Body);
            Debug.Assert(rewrittenBody is { });
            var startLabel = new GeneratedLabelSymbol("start");

            var syntax = node.Syntax;

            // do...until loops until the condition is true (exits when condition becomes true).
            // This is equivalent to do...while(!condition).
            // Negate the condition for the goto-if-true check.
            var negatedCondition = _factory.Not(rewrittenCondition);

            BoundStatement ifConditionGotoStart = new BoundConditionalGoto(syntax, negatedCondition, true, startLabel);

            // do
            //   body
            // until (condition);
            //
            // becomes
            //
            // start:
            // {
            //   body
            //   continue:
            //   GotoIfTrue !condition start;  (i.e. keep looping while condition is false)
            // }
            // break:

            if (node.Locals.IsEmpty)
            {
                return BoundStatementList.Synthesized(syntax, node.HasErrors,
                    new BoundLabelStatement(syntax, startLabel),
                    rewrittenBody,
                    new BoundLabelStatement(syntax, node.ContinueLabel),
                    ifConditionGotoStart,
                    new BoundLabelStatement(syntax, node.BreakLabel));
            }

            return BoundStatementList.Synthesized(syntax, node.HasErrors,
                new BoundLabelStatement(syntax, startLabel),
                new BoundBlock(syntax,
                               node.Locals,
                               ImmutableArray.Create<BoundStatement>(rewrittenBody,
                                                                     new BoundLabelStatement(syntax, node.ContinueLabel),
                                                                     ifConditionGotoStart)),
                new BoundLabelStatement(syntax, node.BreakLabel));
        }
    }
}
