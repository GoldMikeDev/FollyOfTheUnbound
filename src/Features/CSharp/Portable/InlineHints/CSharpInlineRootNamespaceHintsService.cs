// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.InlineHints;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.InlineHints;

/// <summary>
/// Shows the namespace a '*.' root-namespace placeholder qualifier (see <see cref="RootNamespaceQualifierSyntax"/>)
/// resolves to, painted inline over the '*' without changing the buffer.
/// </summary>
[ExportLanguageService(typeof(IInlineRootNamespaceHintsService), LanguageNames.CSharp), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class CSharpInlineRootNamespaceHintsService() : IInlineRootNamespaceHintsService
{
    public async Task AddInlineHintsAsync(
        Document document,
        TextSpan textSpan,
        ArrayBuilder<InlineHint> result,
        CancellationToken cancellationToken)
    {
        if (document.Project.CompilationOptions is not CSharpCompilationOptions { RootNamespace: { Length: > 0 } rootNamespace })
            return;

        var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        foreach (var node in root.DescendantNodes(n => n.Span.IntersectsWith(textSpan)))
        {
            if (node is not RootNamespaceQualifierSyntax qualifier)
                continue;

            var span = qualifier.AsteriskToken.Span;
            if (!textSpan.IntersectsWith(span))
                continue;

            result.Add(new InlineHint(
                span,
                [new TaggedText(TextTags.Namespace, rootNamespace)],
                replacementTextChange: null));
        }
    }
}
