// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

namespace Microsoft.CodeAnalysis.CSharp
{
    internal enum MutationValidityKind
    {
        AlwaysValid,
        Conditional,   // may fail at runtime → warning
        NeverValid,    // will always fail → error
    }

    internal static class MutationValidity
    {
        internal static MutationValidityKind GetValidity(Symbols.TypeSymbol from, Symbols.TypeSymbol to, CSharpCompilation compilation)
        {
            if (from == null || to == null)
                return MutationValidityKind.NeverValid;

            // Same type is always valid
            if (Symbols.SymbolEqualityComparer.Default.Equals(from, to))
                return MutationValidityKind.AlwaysValid;

            var fromSpec = from.SpecialType;
            var toSpec = to.SpecialType;

            return (fromSpec, toSpec) switch
            {
                // bool → numeric/string: always valid
                (SpecialType.System_Boolean, SpecialType.System_Int32) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_UInt32) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_Int64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_UInt64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_Single) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_Double) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_Decimal) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_Byte) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_SByte) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_Int16) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_UInt16) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_Char) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Boolean, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // byte
                (SpecialType.System_Byte, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_Byte, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_Byte, SpecialType.System_Int16) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Byte, SpecialType.System_UInt16) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Byte, SpecialType.System_Int32) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Byte, SpecialType.System_UInt32) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Byte, SpecialType.System_Int64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Byte, SpecialType.System_UInt64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Byte, SpecialType.System_Single) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Byte, SpecialType.System_Double) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Byte, SpecialType.System_Decimal) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Byte, SpecialType.System_Char) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Byte, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // sbyte
                (SpecialType.System_SByte, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_SByte, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_SByte, SpecialType.System_Int16) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_SByte, SpecialType.System_UInt16) => MutationValidityKind.Conditional,
                (SpecialType.System_SByte, SpecialType.System_Int32) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_SByte, SpecialType.System_UInt32) => MutationValidityKind.Conditional,
                (SpecialType.System_SByte, SpecialType.System_Int64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_SByte, SpecialType.System_UInt64) => MutationValidityKind.Conditional,
                (SpecialType.System_SByte, SpecialType.System_Single) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_SByte, SpecialType.System_Double) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_SByte, SpecialType.System_Decimal) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_SByte, SpecialType.System_Char) => MutationValidityKind.Conditional,
                (SpecialType.System_SByte, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // short
                (SpecialType.System_Int16, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_Int16, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_Int16, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_Int16, SpecialType.System_UInt16) => MutationValidityKind.Conditional,
                (SpecialType.System_Int16, SpecialType.System_Int32) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int16, SpecialType.System_UInt32) => MutationValidityKind.Conditional,
                (SpecialType.System_Int16, SpecialType.System_Int64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int16, SpecialType.System_UInt64) => MutationValidityKind.Conditional,
                (SpecialType.System_Int16, SpecialType.System_Single) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int16, SpecialType.System_Double) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int16, SpecialType.System_Decimal) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int16, SpecialType.System_Char) => MutationValidityKind.Conditional,
                (SpecialType.System_Int16, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // ushort
                (SpecialType.System_UInt16, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt16, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt16, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt16, SpecialType.System_Int16) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt16, SpecialType.System_Int32) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt16, SpecialType.System_UInt32) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt16, SpecialType.System_Int64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt16, SpecialType.System_UInt64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt16, SpecialType.System_Single) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt16, SpecialType.System_Double) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt16, SpecialType.System_Decimal) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt16, SpecialType.System_Char) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt16, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // int
                (SpecialType.System_Int32, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_Int32, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_Int32, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_Int32, SpecialType.System_Int16) => MutationValidityKind.Conditional,
                (SpecialType.System_Int32, SpecialType.System_UInt16) => MutationValidityKind.Conditional,
                (SpecialType.System_Int32, SpecialType.System_UInt32) => MutationValidityKind.Conditional,
                (SpecialType.System_Int32, SpecialType.System_Int64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int32, SpecialType.System_UInt64) => MutationValidityKind.Conditional,
                (SpecialType.System_Int32, SpecialType.System_Single) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int32, SpecialType.System_Double) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int32, SpecialType.System_Decimal) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int32, SpecialType.System_Char) => MutationValidityKind.Conditional,
                (SpecialType.System_Int32, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // uint
                (SpecialType.System_UInt32, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt32, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt32, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt32, SpecialType.System_Int16) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt32, SpecialType.System_UInt16) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt32, SpecialType.System_Int32) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt32, SpecialType.System_Int64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt32, SpecialType.System_UInt64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt32, SpecialType.System_Single) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt32, SpecialType.System_Double) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt32, SpecialType.System_Decimal) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt32, SpecialType.System_Char) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt32, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // long
                (SpecialType.System_Int64, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_Int64, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_Int64, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_Int64, SpecialType.System_Int16) => MutationValidityKind.Conditional,
                (SpecialType.System_Int64, SpecialType.System_UInt16) => MutationValidityKind.Conditional,
                (SpecialType.System_Int64, SpecialType.System_Int32) => MutationValidityKind.Conditional,
                (SpecialType.System_Int64, SpecialType.System_UInt32) => MutationValidityKind.Conditional,
                (SpecialType.System_Int64, SpecialType.System_UInt64) => MutationValidityKind.Conditional,
                (SpecialType.System_Int64, SpecialType.System_Single) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int64, SpecialType.System_Double) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int64, SpecialType.System_Decimal) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Int64, SpecialType.System_Char) => MutationValidityKind.Conditional,
                (SpecialType.System_Int64, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // ulong
                (SpecialType.System_UInt64, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt64, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt64, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt64, SpecialType.System_Int16) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt64, SpecialType.System_UInt16) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt64, SpecialType.System_Int32) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt64, SpecialType.System_UInt32) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt64, SpecialType.System_Int64) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt64, SpecialType.System_Single) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt64, SpecialType.System_Double) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt64, SpecialType.System_Decimal) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_UInt64, SpecialType.System_Char) => MutationValidityKind.Conditional,
                (SpecialType.System_UInt64, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // float
                (SpecialType.System_Single, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_Single, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_Single, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_Single, SpecialType.System_Int16) => MutationValidityKind.Conditional,
                (SpecialType.System_Single, SpecialType.System_UInt16) => MutationValidityKind.Conditional,
                (SpecialType.System_Single, SpecialType.System_Int32) => MutationValidityKind.Conditional,
                (SpecialType.System_Single, SpecialType.System_UInt32) => MutationValidityKind.Conditional,
                (SpecialType.System_Single, SpecialType.System_Int64) => MutationValidityKind.Conditional,
                (SpecialType.System_Single, SpecialType.System_UInt64) => MutationValidityKind.Conditional,
                (SpecialType.System_Single, SpecialType.System_Double) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Single, SpecialType.System_Decimal) => MutationValidityKind.Conditional,
                (SpecialType.System_Single, SpecialType.System_Char) => MutationValidityKind.Conditional,
                (SpecialType.System_Single, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // double
                (SpecialType.System_Double, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_Int16) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_UInt16) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_Int32) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_UInt32) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_Int64) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_UInt64) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_Single) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_Decimal) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_Char) => MutationValidityKind.Conditional,
                (SpecialType.System_Double, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // decimal
                (SpecialType.System_Decimal, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_Int16) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_UInt16) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_Int32) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_UInt32) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_Int64) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_UInt64) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_Single) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_Double) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_Char) => MutationValidityKind.Conditional,
                (SpecialType.System_Decimal, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // char
                (SpecialType.System_Char, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_Char, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_Char, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_Char, SpecialType.System_Int16) => MutationValidityKind.Conditional,
                (SpecialType.System_Char, SpecialType.System_UInt16) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Char, SpecialType.System_Int32) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Char, SpecialType.System_UInt32) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Char, SpecialType.System_Int64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Char, SpecialType.System_UInt64) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Char, SpecialType.System_Single) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Char, SpecialType.System_Double) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Char, SpecialType.System_Decimal) => MutationValidityKind.AlwaysValid,
                (SpecialType.System_Char, SpecialType.System_String) => MutationValidityKind.AlwaysValid,

                // string → anything: conditional
                (SpecialType.System_String, SpecialType.System_Boolean) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_Byte) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_SByte) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_Int16) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_UInt16) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_Int32) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_UInt32) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_Int64) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_UInt64) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_Single) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_Double) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_Decimal) => MutationValidityKind.Conditional,
                (SpecialType.System_String, SpecialType.System_Char) => MutationValidityKind.Conditional,

                // Reference type casts
                _ when to.IsReferenceType && from.IsReferenceType => MutationValidityKind.Conditional,
                _ when to.IsValueType && from.IsReferenceType => MutationValidityKind.Conditional,
                _ when to.IsReferenceType && from.IsValueType => MutationValidityKind.AlwaysValid,

                // Everything else
                _ => MutationValidityKind.Conditional,
            };
        }
    }
}
