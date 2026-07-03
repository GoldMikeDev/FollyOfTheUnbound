// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        public override BoundNode VisitMutateStatement(BoundMutateStatement node)
        {
            var sourceType = node.OriginalLocal.Type;
            var targetType = node.NewLocal.Type;
            var validity = MutationValidity.GetValidity(sourceType, targetType, _compilation);

            // Add the new local to the block's local list via _additionalLocals
            _additionalLocals?.Add(node.NewLocal);

            var oldLocalRef = new BoundLocal(node.Syntax, node.OriginalLocal,
                BoundLocalDeclarationKind.None, null, false, sourceType);
            var newLocalRef = new BoundLocal(node.Syntax, node.NewLocal,
                BoundLocalDeclarationKind.None, null, false, targetType);

            BoundExpression convertedValue = BuildLoweredConversion(node.Syntax, oldLocalRef, sourceType, targetType, validity);

            return new BoundExpressionStatement(node.Syntax,
                new BoundAssignmentOperator(node.Syntax, newLocalRef, convertedValue, isRef: false, targetType));
        }

        private BoundExpression BuildLoweredConversion(
            SyntaxNode syntax,
            BoundExpression source,
            TypeSymbol sourceType,
            TypeSymbol targetType,
            MutationValidityKind validity)
        {
            if (validity == MutationValidityKind.AlwaysValid)
            {
                // Always-valid: use direct IL conversion
                return MakeConversionNode(source, targetType, @checked: false);
            }

            // Conditional: use checked conversion for numeric types.
            // For string source or string target, use a special path.
            var fromSpec = sourceType.SpecialType;
            var toSpec = targetType.SpecialType;

            if (fromSpec == SpecialType.System_String && toSpec != SpecialType.None)
            {
                // string → primitive: call TargetType.Parse(string)
                return BuildStringToPrimitiveConversion(syntax, source, targetType, toSpec);
            }

            if (toSpec == SpecialType.System_String)
            {
                // anything → string: call source.ToString()
                return BuildToStringConversion(syntax, source, sourceType);
            }

            // Numeric → Numeric (conditional, e.g. narrowing): use checked explicit cast
            return MakeConversionNode(source, targetType, @checked: true);
        }

        private BoundExpression BuildStringToPrimitiveConversion(
            SyntaxNode syntax,
            BoundExpression source,
            TypeSymbol targetType,
            SpecialType toSpec)
        {
            // Try to find a static Parse(string) method on the target type
            var targetNamedType = targetType as NamedTypeSymbol;
            if (targetNamedType != null)
            {
                foreach (var member in targetNamedType.GetMembers("Parse"))
                {
                    if (member is MethodSymbol method &&
                        method.IsStatic &&
                        method.ParameterCount == 1 &&
                        method.Parameters[0].Type.SpecialType == SpecialType.System_String)
                    {
                        return BoundCall.Synthesized(syntax, receiverOpt: null, 
                            initialBindingReceiverIsSubjectToCloning: ThreeState.Unknown,
                            method, source);
                    }
                }
            }

            // Fallback: use System.Convert.ChangeType and cast
            return BuildChangeTypeConversion(syntax, source, targetType);
        }

        private BoundExpression BuildToStringConversion(
            SyntaxNode syntax,
            BoundExpression source,
            TypeSymbol sourceType)
        {
            // Call source.ToString()
            TypeSymbol stringType = _compilation.GetSpecialType(SpecialType.System_String);

            // Box if value type
            BoundExpression receiver = source;
            if (sourceType.IsValueType)
            {
                receiver = new BoundConversion(syntax, source, Conversion.Boxing,
                    isBaseConversion: false, @checked: false, explicitCastInCode: false,
                    constantValueOpt: null, conversionGroupOpt: null, 
                    inConversionGroupFlags: InConversionGroupFlags.Unspecified, type: objectType);
            }

            // Look up ToString() on the source type
            foreach (var member in sourceType.GetMembers("ToString"))
            {
                if (member is MethodSymbol method && method.ParameterCount == 0 && !method.IsStatic)
                {
                    return new BoundCall(
                        syntax,
                        receiverOpt: source,
                        initialBindingReceiverIsSubjectToCloning: ThreeState.Unknown,
                        method,
                        ImmutableArray<BoundExpression>.Empty,
                        default,
                        default,
                        invokedAsExtensionMethod: false,
                        resultKind: LookupResultKind.Viable,
                        type: stringType);
                }
            }

            // Fallback
            return BuildChangeTypeConversion(syntax, source, stringType);
        }

        private BoundExpression BuildChangeTypeConversion(SyntaxNode syntax, BoundExpression source, TypeSymbol targetType)
        {
            // Fallback: just try a direct checked conversion
            // This handles most numeric narrowing cases; string conversions handled by BuildStringToPrimitiveConversion
            return MakeConversionNode(source, targetType, @checked: true);
        }
    }
}
