// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// This portion of the binder converts an <see cref="InlineExpressionDeclarationSyntax"/> into a <see cref="BoundExpression"/>.
    /// </summary>
    internal partial class Binder
    {
        private BoundExpression BindInlineExpressionDeclaration(InlineExpressionDeclarationSyntax node, BindingDiagnosticBag diagnostics)
        {
            // Bind the inner expression
            BoundExpression operand = BindValue(node.Expression, diagnostics, BindValueKind.RValue);

            // Look up the pre-created local symbol that was registered by ExpressionVariableFinder
            SourceLocalSymbol? localSymbol = this.LookupLocal(node.Identifier);

            if (localSymbol is null)
            {
                // Error recovery: local wasn't pre-created, produce an error expression
                return ToBadExpression(operand);
            }

            // Infer the type from the operand
            TypeWithAnnotations operandType = operand.TypeWithAnnotations;
            if (!operandType.HasType)
            {
                operandType = TypeWithAnnotations.Create(CreateErrorType("var"));
            }

            localSymbol.SetTypeWithAnnotations(operandType);

            return new BoundInlineExpressionDeclaration(node, localSymbol, operand, localSymbol.Type);
        }
    }
}
