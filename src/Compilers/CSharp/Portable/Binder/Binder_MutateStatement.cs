// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class Binder
    {
        private BoundStatement BindMutateStatement(MutateStatementSyntax node, BindingDiagnosticBag diagnostics)
        {
            var varNameText = node.VariableName.Identifier.ValueText;

            // Look up the variable being mutated via the ordinary identifier-binding path (rather than a
            // raw LookupSymbolsWithFallback) so this identifier gets recorded in the binder's
            // IdentifierMap like any other expression -- MethodCompiler's debug-only consistency check
            // (used by SynthesizedPrimaryConstructor.GetCapturedParameters) asserts that every
            // IdentifierNameSyntax predicted to need binding was actually bound through that path.
            BoundExpression variableExpr = this.BindExpression(node.VariableName, diagnostics);

            LocalSymbol originalLocal = variableExpr is BoundLocal boundLocal ? boundLocal.LocalSymbol : null;

            if (originalLocal is null)
            {
                // BindExpression already reported a diagnostic (e.g. name-not-found) unless the
                // identifier resolved to something other than a local (e.g. a field or method), in
                // which case we add our own explanatory error.
                if (!variableExpr.HasErrors)
                {
                    diagnostics.Add(ErrorCode.ERR_UseDefViolation, node.VariableName.Location, varNameText);
                }
                return new BoundBadStatement(node, ImmutableArray<BoundNode>.Empty, hasErrors: true);
            }

            // Bind the target type
            TypeWithAnnotations targetTypeWithAnnotations = this.BindType(node.Type, diagnostics);
            TypeSymbol targetType = targetTypeWithAnnotations.Type;
            TypeSymbol sourceType = originalLocal.Type;

            // Validate the mutation at compile time
            MutationValidityKind validity = MutationValidity.GetValidity(sourceType, targetType, this.Compilation);

            if (validity == MutationValidityKind.NeverValid)
            {
                diagnostics.Add(ErrorCode.ERR_InvalidMutation, node.Location, varNameText, targetType, sourceType);
                return new BoundBadStatement(node, ImmutableArray<BoundNode>.Empty, hasErrors: true);
            }

            if (validity == MutationValidityKind.Conditional)
            {
                diagnostics.Add(ErrorCode.WRN_MutationMayFail, node.Location, varNameText, targetType);
            }

            // The target local was already created and registered in the enclosing block's Locals by
            // LocalScopeBinder.BuildLocals's MutateStatement case (during the pre-scan that runs before
            // any statement in the block is bound) -- look it up rather than creating a second, unrelated
            // symbol here.
            SourceLocalSymbol newLocal = this.LookupLocal(node.VariableName.Identifier);
            Debug.Assert(newLocal is not null && newLocal.DeclarationKind == LocalDeclarationKind.MutationTarget);

            // Build the source expression (old local). This is a hand-constructed reference (never
            // went through the ordinary expression-binding/conversion pipeline), so it must be marked
            // compiler-generated -- otherwise DefiniteAssignmentPass's debug-only strictness check
            // (BoundLocal nodes must be WasConverted or WasCompilerGenerated) asserts.
            BoundExpression originalRef = new BoundLocal(
                node.VariableName,
                originalLocal,
                BoundLocalDeclarationKind.None,
                constantValueOpt: null,
                isNullableUnknown: false,
                type: sourceType).MakeCompilerGenerated();

            // BoundMutateStatement.ConversionExpression only needs to represent "reads the original
            // local's value" for binder-time flow analysis (definite assignment, nullable). The
            // actual conversion (parse/ToString/checked-cast) is synthesized independently by
            // LocalRewriter_MutateStatement, which does not consult this expression at all -- so we
            // deliberately keep it as the plain local reference rather than building a synthetic
            // BoundConversion here (which previously mis-classified conversions like string->int
            // and crashed NullableWalker).
            BoundExpression conversionExpr = originalRef;

            // Register the mutation in the enclosing LocalScopeBinder so subsequent
            // lookups of varNameText resolve to newLocal
            RegisterMutationOverride(varNameText, newLocal);

            return new BoundMutateStatement(node, originalLocal, newLocal, conversionExpr);
        }

        private void RegisterMutationOverride(string name, LocalSymbol newLocal)
        {
            for (var binder = this; binder != null; binder = binder.Next)
            {
                if (binder is LocalScopeBinder lsb)
                {
                    lsb.AddMutationOverride(name, newLocal);
                    return;
                }
            }
        }

    }
}
