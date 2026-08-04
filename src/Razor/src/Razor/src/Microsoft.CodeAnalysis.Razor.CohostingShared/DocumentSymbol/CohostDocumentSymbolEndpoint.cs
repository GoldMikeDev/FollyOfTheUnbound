// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Composition;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Razor.CohostingShared;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.Razor.Cohost;
using Microsoft.CodeAnalysis.Razor.Remote;

namespace Microsoft.VisualStudio.Razor.LanguageClient.Cohost;

/// <summary>
/// This is a <c>[Shared]</c> MEF part -- every daemon connection resolves the same instance (see
/// GoldMikeDev/roslyn#9). <c>_useHierarchicalSymbols</c> used to be a plain field, so a later connection's
/// <see cref="GetRegistrations"/> call (driven by that connection's client capabilities) could silently change
/// the hierarchical-symbols behavior of an earlier, unrelated connection's document symbol requests. Keyed per
/// <see cref="AmbientConnectionToken.Current"/> instead, same pattern as <c>ClientSettingsManager</c>.
/// </summary>
#pragma warning disable RS0030 // Do not use banned APIs
[Shared]
[CohostEndpoint(Methods.TextDocumentDocumentSymbolName)]
[Export(typeof(IDynamicRegistrationProvider))]
[ExportRazorStatelessLspService(typeof(CohostDocumentSymbolEndpoint))]
[method: ImportingConstructor]
#pragma warning restore RS0030 // Do not use banned APIs
internal sealed class CohostDocumentSymbolEndpoint(IIncompatibleProjectService incompatibleProjectService, IRemoteServiceInvoker remoteServiceInvoker)
    : AbstractCohostDocumentEndpoint<DocumentSymbolParams, SumType<DocumentSymbol[], SymbolInformation[]>?>(incompatibleProjectService), IDynamicRegistrationProvider
{
    private readonly IRemoteServiceInvoker _remoteServiceInvoker = remoteServiceInvoker;

    private readonly ConditionalWeakTable<object, StrongBox<bool>> _useHierarchicalSymbolsByConnection = new();
    private readonly StrongBox<bool> _useHierarchicalSymbolsWithNoAmbientConnection = new(false);

    protected override bool MutatesSolutionState => false;

    protected override bool RequiresLSPSolution => true;

    public ImmutableArray<Registration> GetRegistrations(VSInternalClientCapabilities clientCapabilities, RequestContext requestContext)
    {
        if (clientCapabilities.TextDocument?.DocumentSymbol?.DynamicRegistration == true)
        {
            GetUseHierarchicalSymbolsBox().Value = clientCapabilities.TextDocument.DocumentSymbol.HierarchicalDocumentSymbolSupport;

            return [new Registration
            {
                Method = Methods.TextDocumentDocumentSymbolName,
                RegisterOptions = new DocumentSymbolRegistrationOptions()
            }];
        }

        return [];
    }

    private StrongBox<bool> GetUseHierarchicalSymbolsBox()
        => AmbientConnectionToken.Current is { } token
            ? _useHierarchicalSymbolsByConnection.GetOrCreateValue(token)
            : _useHierarchicalSymbolsWithNoAmbientConnection;

    protected override TextDocumentIdentifier? GetRazorTextDocumentIdentifier(DocumentSymbolParams request)
        => request.TextDocument;

    protected override Task<SumType<DocumentSymbol[], SymbolInformation[]>?> HandleRequestAsync(DocumentSymbolParams request, TextDocument razorDocument, CancellationToken cancellationToken)
        => HandleRequestAsync(razorDocument, GetUseHierarchicalSymbolsBox().Value, cancellationToken);

    private async Task<SumType<DocumentSymbol[], SymbolInformation[]>?> HandleRequestAsync(TextDocument razorDocument, bool useHierarchicalSymbols, CancellationToken cancellationToken)
    {
        // Normally we could remove the await here, but in this case it neatly converts from ValueTask to Task for us,
        // and more importantly this method is essentially a public API entry point (via LSP) so having it appear in
        // call stacks is desirable
        return await _remoteServiceInvoker.TryInvokeAsync<IRemoteDocumentSymbolService, SumType<DocumentSymbol[], SymbolInformation[]>?>(
            razorDocument.Project.Solution,
            (service, solutionInfo, cancellationToken) => service.GetDocumentSymbolsAsync(solutionInfo, razorDocument.Id, useHierarchicalSymbols, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(CohostDocumentSymbolEndpoint instance)
    {
        public Task<SumType<DocumentSymbol[], SymbolInformation[]>?> HandleRequestAsync(TextDocument razorDocument, bool useHierarchicalSymbols, CancellationToken cancellationToken)
            => instance.HandleRequestAsync(razorDocument, useHierarchicalSymbols, cancellationToken);
    }
}
