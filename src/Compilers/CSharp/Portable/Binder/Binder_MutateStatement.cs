// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class Binder
    {
        private BoundStatement BindMutateStatement(MutateStatementSyntax node, BindingDiagnosticBag diagnostics)
        {
            var varNameText = node.VariableName.Identifier.ValueText;

            // Look up the variable being mutated
            var lookupResult = LookupResult.GetInstance();
            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            this.LookupSymbolsWithFallback(lookupResult, varNameText, arity: 0, useSiteInfo: ref useSiteInfo, options: LookupOptions.Default);

            LocalSymbol originalLocal = null;
            if (lookupResult.IsSingleViable && lookupResult.SingleSymbolOrDefault is LocalSymbol loc)
            {
                originalLocal = loc;
            }
            lookupResult.Free();

            if (originalLocal is null)
            {
                diagnostics.Add(ErrorCode.ERR_UseDefViolation, node.VariableName.Location, varNameText);
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

            // Create the new local symbol with the target type, named identically to the original
            SourceLocalSymbol newLocal = SourceLocalSymbol.MakeLocal(
                this.ContainingMemberOrLambda,
                this,
                allowRefKind: false,
                allowScoped: false,
                typeSyntax: node.Type,
                identifierToken: node.VariableName.Identifier,
                declarationKind: LocalDeclarationKind.MutationTarget,
                initializer: null);
            newLocal.SetTypeWithAnnotations(targetTypeWithAnnotations);

            // Build the source expression (old local)
            BoundExpression originalRef = new BoundLocal(
                node.VariableName,
                originalLocal,
                BoundLocalDeclarationKind.None,
                constantValueOpt: null,
                isNullableUnknown: false,
                type: sourceType);

            // Build the conversion expression
            BoundExpression conversionExpr = BuildMutationConversionExpression(
                node, originalRef, sourceType, targetType, validity);

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

        private BoundExpression BuildMutationConversionExpression(
            MutateStatementSyntax node,
            BoundExpression source,
            TypeSymbol sourceType,
            TypeSymbol targetType,
            MutationValidityKind validity)
        {
            if (validity == MutationValidityKind.AlwaysValid)
            {
                CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                Conversion conversion = this.Conversions.ClassifyConversionFromType(sourceType, targetType, isChecked: false, ref useSiteInfo);
                return new BoundConversion(
                    node,
                    source,
                    conversion,
                    isBaseConversion: false,
                    @checked: CheckOverflowAtRuntime,
                    explicitCastInCode: false,
                    constantValueOpt: null,
                    conversionGroupOpt: null,
                    inConversionGroupFlags: InConversionGroupFlags.Unspecified,
                    type: targetType);
            }
            else
            {
                // Conditional: explicit cast - will be lowered with a checked conversion
                return new BoundConversion(
                    node,
                    source,
                    Conversion.ExplicitReference,
                    isBaseConversion: false,
                    @checked: true,
                    explicitCastInCode: true,
                    constantValueOpt: null,
                    conversionGroupOpt: null,
                    inConversionGroupFlags: InConversionGroupFlags.Unspecified,
                    type: targetType);
            }
        }
    }
}
