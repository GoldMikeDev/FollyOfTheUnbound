// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        public override BoundNode VisitBlock(BoundBlock node)
        {
            if (Instrument)
            {
                Instrumenter.PreInstrumentBlock(node, this);
            }

            var builder = ArrayBuilder<BoundStatement>.GetInstance();
            // If _additionalLocals is null, this must be the outermost block of the current function.
            // If so, create a collection where child statements can insert inline array temporaries,
            // and add those temporaries to the generated block.
            var previousLocals = _additionalLocals;
            if (previousLocals is null)
            {
                _additionalLocals = ArrayBuilder<LocalSymbol>.GetInstance();
            }

            try
            {
                VisitStatementSubList(builder, node.Statements);

                var additionalLocals = TemporaryArray<LocalSymbol>.Empty;

                BoundBlockInstrumentation? instrumentation = null;
                if (Instrument)
                {
                    Instrumenter.InstrumentBlock(node, this, ref additionalLocals, out var prologue, out var epilogue, out instrumentation);
                    if (prologue != null)
                    {
                        builder.Insert(0, prologue);
                    }

                    if (epilogue != null)
                    {
                        builder.Add(epilogue);
                    }
                }

                var locals = node.Locals;
                if (previousLocals is null)
                {
                    locals = locals.AddRange(_additionalLocals!);
                }
                locals = locals.AddRange(additionalLocals);
                return new BoundBlock(node.Syntax, locals, node.LocalFunctions, node.HasUnsafeModifier, instrumentation, builder.ToImmutableAndFree(), node.HasErrors);
            }
            finally
            {
                if (previousLocals is null)
                {
                    _additionalLocals!.Free();
                    _additionalLocals = previousLocals;
                }
            }
        }

        /// <summary>
        /// Visit a partial list of statements that possibly contain using declarations
        /// </summary>
        /// <param name="builder">The array builder to append statements to</param>
        /// <param name="statements">The list of statements to visit</param>
        /// <param name="startIndex">The index of the <paramref name="statements"/> to begin visiting at</param>
        /// <returns>An <see cref="ImmutableArray{T}"/> of <see cref="BoundStatement"/></returns>
        public void VisitStatementSubList(ArrayBuilder<BoundStatement> builder, ImmutableArray<BoundStatement> statements, int startIndex = 0)
        {
            for (int i = startIndex; i < statements.Length; i++)
            {
                BoundStatement? statement = VisitPossibleUsingDeclaration(statements[i], statements, i, out var replacedUsingDeclarations, out var extraTrailingStatement);
                if (statement != null)
                {
                    builder.Add(statement);
                }

                // See the "escape;" remarks on VisitPossibleUsingDeclaration: this must be added as
                // a genuine sibling in *this* list (not nested inside the statement returned above),
                // and at whichever nesting level this call is running at -- which is exactly what
                // happens here, since every recursive/nested using-declaration wrapping funnels its
                // own trailing statements through this same loop.
                if (extraTrailingStatement != null)
                {
                    builder.Add(extraTrailingStatement);
                }

                if (replacedUsingDeclarations)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Visits a node that is possibly a <see cref="BoundUsingLocalDeclarations"/>
        /// </summary>
        /// <param name="node">The node to visit</param>
        /// <param name="statements">All statements in the block containing this node</param>
        /// <param name="statementIndex">The current statement being visited in <paramref name="statements"/></param>
        /// <param name="replacedLocalDeclarations">Set to true if this visited a <see cref="BoundUsingLocalDeclarations"/> node</param>
        /// <param name="extraTrailingStatement">
        /// Non-null only when this visit pulled the compiler-generated `escape;` landing label (see
        /// remarks) out of a using declaration's generated try/finally. The caller must append it as
        /// a sibling statement in its own output list immediately after the statement this method
        /// returns -- never nest it inside another node -- so the label stays genuinely outside every
        /// using declaration's protected region no matter how many are nested.
        /// </param>
        /// <returns>A <see cref="BoundStatement"/></returns>
        /// <remarks>
        /// The node being visited is not necessarily equal to statements[startIndex].
        /// When traversing down a set of labels, we set node to the label.body and recurse, but statements[startIndex] still refers to the original parent label
        /// as we haven't actually moved down the original statement list
        ///
        /// <para><b>escape;</b>: if some `escape;` earlier in this same block requested a landing
        /// label (see Binder_Statements.BindBlock / BlockBinder.GetEscapeLabel), that label is
        /// synthesized as the block's *last* statement, so it can end up as the last of the "trailing
        /// statements after a using declaration" this method collects into the try body it builds. It
        /// must not stay there: the escape's `BoundGotoStatement` originates *before* the using
        /// declaration (outside the try entirely), so a label nested inside the try would make it an
        /// illegal jump into protected code (verified by hand against the ILBuilder's exception-handler
        /// invariants -- nesting it that way trips
        /// <c>BasicBlock.RewriteBranchesAcrossExceptionHandlers</c>'s
        /// <c>BranchBlock?.EnclosingHandler == null</c> assertion). So this method pulls it back out via
        /// <paramref name="extraTrailingStatement"/> and the caller re-appends it as a plain sibling,
        /// placing it genuinely outside the try -- structurally the same as how `break`/`return`
        /// already leave a using declaration's scope correctly. Because every nesting level (see
        /// <see cref="VisitStatementSubList"/>) funnels its own trailing statements through this same
        /// out-parameter, this hoists the label past *every* enclosing using declaration in the block,
        /// not just the innermost one.</para>
        /// </remarks>
        public BoundStatement? VisitPossibleUsingDeclaration(BoundStatement node, ImmutableArray<BoundStatement> statements, int statementIndex, out bool replacedLocalDeclarations, out BoundStatement? extraTrailingStatement)
        {
            switch (node.Kind)
            {
                case BoundKind.LabeledStatement:
                    var labelStatement = (BoundLabeledStatement)node;
                    return MakeLabeledStatement(labelStatement, VisitPossibleUsingDeclaration(labelStatement.Body, statements, statementIndex, out replacedLocalDeclarations, out extraTrailingStatement));
                case BoundKind.UsingLocalDeclarations:
                    // The compiler-generated `escape;` landing label, when present, is always
                    // synthesized as the *original* (pre-lowering) statement list's last entry (see
                    // Binder_Statements.BindBlock) -- and `statements` is the same original array
                    // threaded unchanged through every level of this recursion, only `statementIndex`
                    // varies. So this check must run against the *raw* `statements[^1]`, not against
                    // whatever `VisitStatementSubList` below produces: lowering a label rewrites a
                    // `BoundLabeledStatement` into a `BoundStatementList` (see
                    // LocalRewriter_LabeledStatement.MakeLabeledStatement), so by the time a lowered
                    // statement reaches the builder below it no longer looks like a
                    // `BoundLabeledStatement` at all, and a post-lowering check here would silently
                    // never match -- leaving the label nested inside this using declaration's try
                    // after all, with no diagnostic to reveal it (verified: this was a real bug in an
                    // earlier version of this fix, caught by hand-inspecting the emitted IL after an
                    // `ILBuilder` assertion -- "BranchBlock?.EnclosingHandler == null" -- caught the
                    // resulting illegal branch into the try).
                    bool hoistTrailingEscapeLabel = statementIndex < statements.Length - 1 && IsCompilerGeneratedEscapeLabel(statements[^1]);

                    // visit everything after this node
                    ArrayBuilder<BoundStatement> builder = ArrayBuilder<BoundStatement>.GetInstance();
                    VisitStatementSubList(builder, statements, statementIndex + 1);

                    // Pull the (now-lowered) trailing escape label back out before it gets sealed into
                    // this using declaration's try body -- see the remarks above and on this method.
                    extraTrailingStatement = null;
                    if (hoistTrailingEscapeLabel)
                    {
                        Debug.Assert(builder.Count > 0);
                        extraTrailingStatement = builder[^1];
                        builder.RemoveLast();
                    }

                    // make a using declaration with the visited statements as its body
                    replacedLocalDeclarations = true;
                    return MakeLocalUsingDeclarationStatement((BoundUsingLocalDeclarations)node, builder.ToImmutableAndFree());
                default:
                    replacedLocalDeclarations = false;
                    extraTrailingStatement = null;
                    return VisitStatement(node);
            }
        }

        /// <summary>
        /// True if <paramref name="statement"/> is the compiler-generated, otherwise-empty labeled
        /// statement <see cref="Binder"/> appends to a block as a landing site for any `escape;`
        /// that targets it (see Binder_Statements.BindBlock / BlockBinder.GetEscapeLabel).
        /// </summary>
        private static bool IsCompilerGeneratedEscapeLabel(BoundStatement statement)
            => statement is BoundLabeledStatement { WasCompilerGenerated: true, Label: GeneratedLabelSymbol, Body: BoundNoOpStatement { WasCompilerGenerated: true } };

        public override BoundNode VisitNoOpStatement(BoundNoOpStatement node)
        {
            return (node.WasCompilerGenerated || !this.Instrument)
                ? new BoundBlock(node.Syntax, ImmutableArray<LocalSymbol>.Empty, ImmutableArray<BoundStatement>.Empty)
                : Instrumenter.InstrumentNoOpStatement(node, node);
        }
    }
}
