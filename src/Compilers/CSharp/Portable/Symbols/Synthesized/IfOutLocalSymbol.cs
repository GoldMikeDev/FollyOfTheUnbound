// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Immutable;
using System.Diagnostics;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// The synthesized <c>ifout</c> local that is implicitly declared inside the condition block of an
    /// <see cref="Syntax.IfCatchArmSyntax"/> that uses a block condition. Its type is always
    /// <c>System.Nullable&lt;System.Boolean&gt;</c>. Unlike other synthesized locals, this one has a real
    /// user-visible name ("ifout") so that ordinary identifier lookup inside the condition block resolves to it.
    /// </summary>
    internal sealed class IfOutLocalSymbol : LocalSymbol
    {
        internal const string IfOutName = "ifout";

        private readonly Symbol _containingSymbol;
        private readonly TypeWithAnnotations _type;
        private readonly SyntaxNode _syntax;

        internal IfOutLocalSymbol(Symbol containingSymbol, TypeWithAnnotations type, SyntaxNode syntax)
        {
            Debug.Assert(syntax != null);
            _containingSymbol = containingSymbol;
            _type = type;
            _syntax = syntax;
        }

        public override string Name => IfOutName;

        public override Symbol ContainingSymbol => _containingSymbol;

        public override TypeWithAnnotations TypeWithAnnotations => _type;

        public override RefKind RefKind => RefKind.None;

        public override ImmutableArray<Location> Locations => ImmutableArray.Create(_syntax.GetLocation());

        public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => ImmutableArray.Create(_syntax.GetReference());

        public override bool IsImplicitlyDeclared => true;

        internal override LocalDeclarationKind DeclarationKind => LocalDeclarationKind.IfOutVariable;

        internal override SynthesizedLocalKind SynthesizedKind => SynthesizedLocalKind.UserDefined;

        internal override SyntaxNode ScopeDesignatorOpt => null;

        internal override LocalSymbol WithSynthesizedLocalKindAndSyntax(SynthesizedLocalKind kind, SyntaxNode syntax)
            => throw ExceptionUtilities.Unreachable();

        internal override bool IsImportedFromMetadata => false;

        internal override SyntaxToken IdentifierToken => default;

        internal override bool IsPinned => false;

        internal override bool IsKnownToReferToTempIfReferenceType => false;

        internal override ScopedKind Scope => ScopedKind.None;

        internal override SyntaxNode GetDeclaratorSyntax() => _syntax;

        internal override bool HasSourceLocation => true;

        internal override bool IsCompilerGenerated => false;

        internal override ConstantValue GetConstantValue(SyntaxNode node, LocalSymbol inProgress, BindingDiagnosticBag diagnostics = null) => null;

        internal override ReadOnlyBindingDiagnostic<AssemblySymbol> GetConstantValueDiagnostics(BoundExpression boundInitValue)
            => ReadOnlyBindingDiagnostic<AssemblySymbol>.Empty;
    }
}
