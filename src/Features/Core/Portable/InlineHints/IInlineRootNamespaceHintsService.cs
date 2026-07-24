// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.InlineHints;

/// <summary>
/// Gets inline hints showing the namespace a '*.' root-namespace placeholder qualifier resolves to. This is an
/// internal service only for C# (VB has no equivalent construct).
/// </summary>
internal interface IInlineRootNamespaceHintsService : ILanguageService
{
    Task AddInlineHintsAsync(
        Document document,
        TextSpan textSpan,
        ArrayBuilder<InlineHint> result,
        CancellationToken cancellationToken);
}
