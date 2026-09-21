// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Debugging;

internal sealed partial class CSharpProximityExpressionsService
{
    private sealed class RelevantExpressionsCollector(bool includeDeclarations, IList<string> expressions) : CSharpSyntaxVisitor
    {
        private readonly bool _includeDeclarations = includeDeclarations;
        private readonly IList<string> _expressions = expressions;

        public override void VisitLabeledStatement(LabeledStatementSyntax node)
            => AddRelevantExpressions(node.Statement, _expressions, _includeDeclarations);

        public override void VisitExpressionStatement(ExpressionStatementSyntax node)
            => AddExpressionTerms(node.Expression, _expressions);

        public override void VisitReturnStatement(ReturnStatementSyntax node)
            => AddExpressionTerms(node.Expression, _expressions);

        public override void VisitThrowStatement(ThrowStatementSyntax node)
            => AddExpressionTerms(node.Expression, _expressions);

        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            // Here, we collect expression expressions from any/all initialization expressions
            AddVariableExpressions(node.Declaration.Variables, _expressions);
        }

        public override void VisitDoStatement(DoStatementSyntax node)
            => AddExpressionTerms(node.Condition, _expressions);

        public override void VisitDoUntilStatement(DoUntilStatementSyntax node)
            => AddExpressionTerms(node.Condition, _expressions);

        public override void VisitMutateStatement(MutateStatementSyntax node)
        {
            // A mutate statement re-declares its variable under the same name with a new type, and also
            // *reads* the variable's existing value to perform the conversion -- so unlike an ordinary
            // local declaration's name (which AddVariableExpressions only records when
            // _includeDeclarations is set, for a breakpoint on a *following* statement), this variable
            // needs to show up in Autos/proximity expressions unconditionally: both for a breakpoint on
            // the mutate statement itself (_includeDeclarations: false, via Worker.Do's initial
            // AddRelevantExpressions(_parentStatement, ...) call, where it's read) and for one on the
            // following statement (_includeDeclarations: true, where it's the re-declared local).
            //
            // AddExpressionTerms (not a raw Identifier.ValueText add, as an ordinary declaration would
            // use) also preserves the source token's actual spelling, which matters for an escaped
            // identifier like `mutate @class to long;` -- ValueText would give the debugger-invalid
            // "class" instead of "@class".
            AddExpressionTerms(node.VariableName, _expressions);
        }

        public override void VisitLockStatement(LockStatementSyntax node)
            => AddExpressionTerms(node.Expression, _expressions);

        public override void VisitWhileStatement(WhileStatementSyntax node)
            => AddExpressionTerms(node.Condition, _expressions);

        public override void VisitIfStatement(IfStatementSyntax node)
            => AddExpressionTerms(node.Condition, _expressions);

        public override void VisitForStatement(ForStatementSyntax node)
        {
            if (node.Declaration != null)
            {
                AddVariableExpressions(node.Declaration.Variables, _expressions);
            }

            node.Initializers.Do(i => AddExpressionTerms(i, _expressions));
            AddExpressionTerms(node.Condition, _expressions);
            node.Incrementors.Do(i => AddExpressionTerms(i, _expressions));
        }

        public override void VisitForEachStatement(ForEachStatementSyntax node)
        {
            _expressions.Add(node.Identifier.ValueText);
            AddExpressionTerms(node.Expression, _expressions);
        }

        public override void VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        {
            AddVariableExpressions(node.Variable, _expressions);
            AddExpressionTerms(node.Expression, _expressions);
        }

        public override void VisitUsingStatement(UsingStatementSyntax node)
        {
            if (node.Declaration != null)
            {
                AddVariableExpressions(node.Declaration.Variables, _expressions);
            }

            AddExpressionTerms(node.Expression, _expressions);
        }

        public override void VisitSwitchStatement(SwitchStatementSyntax node)
            => AddExpressionTerms(node.Expression, _expressions);

        private void AddVariableExpressions(
            SeparatedSyntaxList<VariableDeclaratorSyntax> declarators,
            IList<string> expressions)
        {
            foreach (var declarator in declarators)
            {
                if (_includeDeclarations)
                {
                    expressions.Add(declarator.Identifier.ValueText);
                }

                if (declarator.Initializer != null)
                {
                    AddExpressionTerms(declarator.Initializer.Value, expressions);
                }
            }
        }

        private void AddVariableExpressions(
            ExpressionSyntax component,
            IList<string> expressions)
        {
            if (!_includeDeclarations)
                return;

            switch (component.Kind())
            {
                case SyntaxKind.TupleExpression:
                    {
                        var t = (TupleExpressionSyntax)component;
                        foreach (var a in t.Arguments)
                        {
                            AddVariableExpressions(a.Expression, expressions);
                        }

                        break;
                    }
                case SyntaxKind.DeclarationExpression:
                    {
                        var t = (DeclarationExpressionSyntax)component;
                        AddVariableExpressions(t.Designation, expressions);
                        break;
                    }
            }
        }

        private void AddVariableExpressions(
            VariableDesignationSyntax component,
            IList<string> expressions)
        {
            if (!_includeDeclarations)
                return;

            switch (component.Kind())
            {
                case SyntaxKind.ParenthesizedVariableDesignation:
                    {
                        var t = (ParenthesizedVariableDesignationSyntax)component;
                        foreach (var v in t.Variables)
                            AddVariableExpressions(v, expressions);
                        break;
                    }
                case SyntaxKind.SingleVariableDesignation:
                    {
                        var t = (SingleVariableDesignationSyntax)component;
                        expressions.Add(t.Identifier.ValueText);
                        break;
                    }
            }
        }
    }
}
