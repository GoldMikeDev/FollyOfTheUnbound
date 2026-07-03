// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        public override BoundNode VisitInlineExpressionDeclaration(BoundInlineExpressionDeclaration node)
        {
            var rewrittenOperand = VisitExpression(node.Operand);
            var local = new BoundLocal(
                node.Syntax,
                node.LocalSymbol,
                BoundLocalDeclarationKind.None,
                constantValueOpt: null,
                isNullableUnknown: false,
                type: node.LocalSymbol.Type);
            return new BoundAssignmentOperator(
                node.Syntax,
                local,
                rewrittenOperand,
                isRef: false,
                node.LocalSymbol.Type);
        }
    }
}
