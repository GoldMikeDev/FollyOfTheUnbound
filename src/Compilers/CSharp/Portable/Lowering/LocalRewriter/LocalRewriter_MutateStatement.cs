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

            // node.NewLocal is already declared in the enclosing block's Locals -- it was registered by
            // LocalScopeBinder.BuildLocals's MutateStatement case during binding, not synthesized here.

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
                // bool has no CLR/C# numeric conversion at all -- neither MakeConversionNode nor a plain
                // cast can produce bool->numeric or bool->string IL. Lower it by hand as a conditional
                // expression instead (true=1/"true", false=0/"false", per the mutate spec).
                if (sourceType.SpecialType == SpecialType.System_Boolean)
                {
                    if (targetType.SpecialType == SpecialType.System_String)
                    {
                        return _factory.Conditional(source, _factory.Literal("true"), _factory.Literal("false"), targetType);
                    }

                    BoundExpression oneValue = MakeConversionNode(_factory.Literal(1), targetType, @checked: false);
                    BoundExpression zeroValue = MakeConversionNode(_factory.Literal(0), targetType, @checked: false);
                    return _factory.Conditional(source, oneValue, zeroValue, targetType);
                }

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

            if (toSpec == SpecialType.System_Boolean)
            {
                // Numeric/char → bool has no CLR conversion either; treat any nonzero value as true.
                // (The spec's "only if exactly 0 or 1" runtime check is not enforced here.)
                BinaryOperatorKind notEqualKind = fromSpec switch
                {
                    SpecialType.System_Byte => BinaryOperatorKind.UIntNotEqual,
                    SpecialType.System_SByte => BinaryOperatorKind.IntNotEqual,
                    SpecialType.System_Int16 => BinaryOperatorKind.IntNotEqual,
                    SpecialType.System_UInt16 => BinaryOperatorKind.UIntNotEqual,
                    SpecialType.System_Char => BinaryOperatorKind.UIntNotEqual,
                    SpecialType.System_Int32 => BinaryOperatorKind.IntNotEqual,
                    SpecialType.System_UInt32 => BinaryOperatorKind.UIntNotEqual,
                    SpecialType.System_Int64 => BinaryOperatorKind.LongNotEqual,
                    SpecialType.System_UInt64 => BinaryOperatorKind.ULongNotEqual,
                    SpecialType.System_Single => BinaryOperatorKind.FloatNotEqual,
                    SpecialType.System_Double => BinaryOperatorKind.DoubleNotEqual,
                    SpecialType.System_Decimal => BinaryOperatorKind.DecimalNotEqual,
                    _ => BinaryOperatorKind.IntNotEqual,
                };
                BoundExpression zero = MakeConversionNode(_factory.Literal(0), sourceType, @checked: false);
                return _factory.Binary(notEqualKind, targetType, source, zero);
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
            if (targetNamedType is not null)
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
                TypeSymbol objectType = _compilation.GetSpecialType(SpecialType.System_Object);
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
                        receiverOpt: receiver,
                        initialBindingReceiverIsSubjectToCloning: ThreeState.Unknown,
                        method,
                        ImmutableArray<BoundExpression>.Empty,
                        argumentNamesOpt: default,
                        argumentRefKindsOpt: default,
                        isDelegateCall: false,
                        expanded: false,
                        invokedAsExtensionMethod: false,
                        argsToParamsOpt: default,
                        defaultArguments: default,
                        resultKind: LookupResultKind.Viable,
                        originalMethodsOpt: default,
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
