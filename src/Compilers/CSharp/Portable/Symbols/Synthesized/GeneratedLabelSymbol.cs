// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    internal sealed class GeneratedLabelSymbol : LabelSymbol
    {
        private readonly string _name;
        private readonly Location? _locationOpt;

        public GeneratedLabelSymbol(string name)
            : this(name, locationOpt: null)
        {
        }

        /// <summary>
        /// A generated label that is itself the direct target of a real <see cref="BoundGotoStatement"/>
        /// (as opposed to only being referenced structurally, e.g. as a loop's <c>BreakLabel</c>) needs a
        /// real <paramref name="locationOpt"/>: <see cref="ControlFlowPass.VisitGotoStatement"/> calls
        /// <c>Label.GetFirstLocation()</c> for using-declaration jump checks, which would otherwise throw
        /// (the base <see cref="Locations"/> override is empty/unsupported for an ordinary generated label).
        /// </summary>
        public GeneratedLabelSymbol(string name, Location? locationOpt)
        {
            _name = LabelName(name);
            _locationOpt = locationOpt;
#if DEBUG
            NameNoSequence = $"<{name}>";
#endif
        }

        public override string Name
        {
            get
            {
                return _name;
            }
        }

#if DEBUG
        internal string NameNoSequence { get; }

        private static int s_sequence = 1;
#endif
        private static string LabelName(string name)
        {
#if DEBUG
            int seq = System.Threading.Interlocked.Add(ref s_sequence, 1);
            return "<" + name + "-" + (seq & 0xffff) + ">";
#else
            return name;
#endif
        }

        public override ImmutableArray<Location> Locations
        {
            get
            {
                return _locationOpt is null ? ImmutableArray<Location>.Empty : ImmutableArray.Create(_locationOpt);
            }
        }

        public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences
        {
            get
            {
                return ImmutableArray<SyntaxReference>.Empty;
            }
        }

        public override bool IsImplicitlyDeclared
        {
            get { return true; }
        }
    }
}
