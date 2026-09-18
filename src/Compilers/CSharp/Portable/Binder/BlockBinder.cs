// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed class BlockBinder : LocalScopeBinder
    {
        private readonly BlockSyntax _block;
        private GeneratedLabelSymbol _lazyEscapeLabel;

        public BlockBinder(Binder enclosing, BlockSyntax block)
            : this(enclosing, block, enclosing.Flags)
        {
        }

        public BlockBinder(Binder enclosing, BlockSyntax block, BinderFlags additionalFlags)
            : base(enclosing, enclosing.Flags | additionalFlags)
        {
            Debug.Assert(block != null);
            _block = block;
        }

        protected override ImmutableArray<LocalSymbol> BuildLocals()
        {
            return BuildLocals(_block.Statements, this);
        }

        protected override ImmutableArray<LocalFunctionSymbol> BuildLocalFunctions()
        {
            return BuildLocalFunctions(_block.Statements);
        }

        internal override bool IsLocalFunctionsScopeBinder
        {
            get
            {
                return true;
            }
        }

        protected override ImmutableArray<LabelSymbol> BuildLabels()
        {
            ArrayBuilder<LabelSymbol> labels = null;
            base.BuildLabels(_block.Statements, ref labels);
            return (labels != null) ? labels.ToImmutableAndFree() : ImmutableArray<LabelSymbol>.Empty;
        }

        internal override bool IsLabelsScopeBinder
        {
            get
            {
                return true;
            }
        }

        internal override ImmutableArray<LocalSymbol> GetDeclaredLocalsForScope(SyntaxNode scopeDesignator)
        {
            if (ScopeDesignator == scopeDesignator)
            {
                return this.Locals;
            }

            throw ExceptionUtilities.Unreachable();
        }

        internal override SyntaxNode ScopeDesignator
        {
            get
            {
                return _block;
            }
        }

        internal override ImmutableArray<LocalFunctionSymbol> GetDeclaredLocalFunctionsForScope(CSharpSyntaxNode scopeDesignator)
        {
            if (ScopeDesignator == scopeDesignator)
            {
                return this.LocalFunctions;
            }

            throw ExceptionUtilities.Unreachable();
        }

        /// <summary>
        /// The label an <c>escape;</c> statement lexically inside this block (and not inside any
        /// nested block) branches to. Always answers for itself -- unlike break/continue's search up
        /// through enclosing loops/switches, <c>escape;</c> always targets the *nearest* block.
        /// Allocated lazily and cached on <see cref="_lazyEscapeLabel"/>; <see
        /// cref="EscapeLabelIfAllocated"/> lets <c>BindBlockParts</c> avoid emitting an unused label
        /// statement when no <c>escape;</c> in this block ever asked for it.
        ///
        /// <para><b>Speculative-binding safety:</b> caching on <see cref="_lazyEscapeLabel"/> is only
        /// correct when <paramref name="escapeStatementSyntax"/> is a genuine descendant of this
        /// binder's real <see cref="_block"/> -- i.e. an actual <c>escape;</c> lexically present in the
        /// source this <see cref="BlockBinder"/> was constructed for. A speculative bind (e.g.
        /// <c>SemanticModel.TryGetSpeculativeSemanticModel(int, StatementSyntax, out SemanticModel)</c>
        /// for a synthetic `escape;` inserted at some position) reuses this *same, long-lived* real
        /// <see cref="BlockBinder"/> instance from the enclosing
        /// binder chain, but the speculative statement is a free-standing node that is never actually
        /// part of <see cref="_block"/>'s <c>Statements</c>. Allocating/caching in that case would
        /// permanently corrupt this real binder: a later *real* bind of the same block would see
        /// <see cref="EscapeLabelIfAllocated"/> non-null, append an unused synthesized label statement
        /// nobody's `escape;` ever asked for, and <c>ControlFlowPass</c> would report a spurious
        /// <c>WRN_UnreferencedLabel</c> -- nondeterministically, depending on whether some unrelated
        /// speculative-binding query (e.g. IDE completion or quick info) happened to run first. So a
        /// speculative <paramref name="escapeStatementSyntax"/> gets its own fresh,
        /// <em>uncached</em> label each time instead of touching <see cref="_lazyEscapeLabel"/>.
        /// </para>
        /// </summary>
        internal override GeneratedLabelSymbol GetEscapeLabel(SyntaxNode escapeStatementSyntax)
        {
            if (!IsDescendantOfThisBlock(escapeStatementSyntax))
            {
                // Speculative (or otherwise foreign) node: answer without mutating real binder state.
                return new GeneratedLabelSymbol("escape", _block.CloseBraceToken.GetLocation());
            }

            if (_lazyEscapeLabel is null)
            {
                var label = new GeneratedLabelSymbol("escape", _block.CloseBraceToken.GetLocation());
                Interlocked.CompareExchange(ref _lazyEscapeLabel, label, null);
            }

            return _lazyEscapeLabel;
        }

        private bool IsDescendantOfThisBlock(SyntaxNode node)
        {
            for (var current = node; current is not null; current = current.Parent)
            {
                if (current == _block)
                {
                    return true;
                }
            }

            return false;
        }

        internal GeneratedLabelSymbol EscapeLabelIfAllocated => _lazyEscapeLabel;
    }
}
