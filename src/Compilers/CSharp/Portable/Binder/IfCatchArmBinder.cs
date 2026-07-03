// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// The binder that introduces the synthesized <c>ifout</c> local (of type <c>bool?</c>) into scope for a
    /// single <see cref="IfCatchArmSyntax"/>'s <see cref="IfCatchArmSyntax.ConditionBlock"/>. This local is
    /// independently scoped per-arm: it is not visible to any other arm's condition block or consequence.
    /// </summary>
    internal sealed class IfCatchArmBinder : LocalScopeBinder
    {
        private readonly IfCatchArmSyntax _syntax;

        public IfCatchArmBinder(Binder enclosing, IfCatchArmSyntax syntax)
            : base(enclosing)
        {
            Debug.Assert(syntax != null);
            Debug.Assert(syntax.ConditionBlock != null);
            _syntax = syntax;
        }

        protected override ImmutableArray<LocalSymbol> BuildLocals()
        {
            var locals = ArrayBuilder<LocalSymbol>.GetInstance();

            var boolType = Compilation.GetSpecialType(SpecialType.System_Boolean);
            var nullableBoolType = Compilation.GetSpecialType(SpecialType.System_Nullable_T).Construct(boolType);

            locals.Add(new IfOutLocalSymbol(this.ContainingMemberOrLambda, TypeWithAnnotations.Create(nullableBoolType), _syntax.ConditionBlock));

            return locals.ToImmutableAndFree();
        }

        internal override ImmutableArray<LocalSymbol> GetDeclaredLocalsForScope(SyntaxNode scopeDesignator)
        {
            if (_syntax == scopeDesignator)
            {
                return this.Locals;
            }

            throw ExceptionUtilities.Unreachable();
        }

        internal override ImmutableArray<LocalFunctionSymbol> GetDeclaredLocalFunctionsForScope(CSharpSyntaxNode scopeDesignator)
        {
            throw ExceptionUtilities.Unreachable();
        }

        internal override SyntaxNode ScopeDesignator => _syntax;
    }
}
