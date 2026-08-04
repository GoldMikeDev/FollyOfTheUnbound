// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.BrokeredServices;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio.Shell.ServiceBroker;

namespace Microsoft.CodeAnalysis.BrokeredServices;

/// <summary>
/// A single connection registers more than one <see cref="Workspace"/> (Host, MiscellaneousFiles), all of
/// which must resolve to the *same* <see cref="ServiceBrokerProvider"/> instance -- <c>serviceBroker/connect</c>
/// only ever calls <see cref="ServiceBrokerProvider.SetContainer"/> on one of them (<c>requestContext.Workspace</c>,
/// see <c>ServiceBrokerConnectHandler</c>), so code resolving <see cref="IServiceBrokerProvider"/> from any
/// other of that connection's workspaces (e.g. <c>PdbSourceDocumentMetadataAsSourceFileProvider</c> resolving
/// <c>ISourceLinkService</c> from the miscellaneous-files workspace for a loose-file navigation) needs to see
/// the same completed container, not hang forever on a separate, never-completed one. Keyed by
/// <see cref="AmbientConnectionToken.Current"/> instead of by workspace for that reason -- reusing one
/// provider across every workspace belonging to the same connection, while still giving different connections
/// (all built from the same process-wide daemon-mode <c>ExportProvider</c>, see GoldMikeDev/roslyn#9) their
/// own instance, so <see cref="ServiceBrokerProvider.SetContainer"/>'s guard against being called twice
/// doesn't throw for every connection after the first.
/// </summary>
[ExportWorkspaceServiceFactory(typeof(IServiceBrokerProvider), ServiceLayer.Host), Shared]
internal sealed class ServiceBrokerProviderFactory : IWorkspaceServiceFactory
{
    private readonly ConditionalWeakTable<object, ServiceBrokerProvider> _providersByConnection = new();
    private ServiceBrokerProvider? _providerWithNoAmbientConnection;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public ServiceBrokerProviderFactory()
    {
    }

    public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices)
        => AmbientConnectionToken.Current is { } token
            ? _providersByConnection.GetOrCreateValue(token)
            : _providerWithNoAmbientConnection ??= new ServiceBrokerProvider();
}

/// <summary>
/// Workspace service that can be used to fetch a service broker instance from a workspace.
/// </summary>
internal sealed class ServiceBrokerProvider() : IServiceBrokerProvider
{
    private readonly TaskCompletionSource<IBrokeredServiceContainer> _serviceBrokerContainerTask = new();

    /// <summary>
    /// Returns an instance of <see cref="IServiceBroker"/> that will wait for the service broker to be available before invoking the requested method.
    /// </summary>
    /// <remarks>
    /// Each call to this property returns a new instance of <see cref="IServiceBroker"/> from <see cref="IBrokeredServiceContainer.GetFullAccessServiceBroker"/>.
    /// This is observable to callers in a few ways, including that they only get the <see cref="IServiceBroker.AvailabilityChanged"/> events based on their own service queries.
    /// </remarks>
    public IServiceBroker ServiceBroker
    {
        get
        {
            return new WrappedServiceBroker(_serviceBrokerContainerTask.Task);
        }
    }

    public void SetContainer(IBrokeredServiceContainer container)
    {
        Contract.ThrowIfTrue(_serviceBrokerContainerTask.Task.IsCompleted);
        _serviceBrokerContainerTask.SetResult(container);
    }
}
