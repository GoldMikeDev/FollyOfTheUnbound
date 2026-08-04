// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.BrokeredServices;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio.Shell.ServiceBroker;

namespace Microsoft.CodeAnalysis.BrokeredServices;

[ExportWorkspaceServiceFactory(typeof(IServiceBrokerProvider), ServiceLayer.Host), Shared]
internal sealed class ServiceBrokerProviderFactory : IWorkspaceServiceFactory
{
    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public ServiceBrokerProviderFactory()
    {
    }

    public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices)
        => new ServiceBrokerProvider();
}

/// <summary>
/// Workspace service that can be used to fetch a service broker instance from a workspace.
/// </summary>
/// <remarks>
/// Exported as a per-workspace factory, not a <see cref="Shared"/> service: in daemon mode every
/// connection's <see cref="Workspace"/> is built from the same process-wide <c>ExportProvider</c>
/// (see GoldMikeDev/roslyn#9), so a directly-<see cref="Shared"/> export here would resolve to the same
/// singleton instance for every connection. <see cref="SetContainer"/>'s guard against being called twice
/// would then throw for every connection after the first, crashing that
/// connection's service-broker setup instead of merely misrouting brokered-service traffic like the other
/// per-connection state leaks tracked in that issue. <see cref="ServiceBrokerProviderFactory"/> gives each
/// workspace (i.e. each connection) its own instance instead.
/// </remarks>
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
