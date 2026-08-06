// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// This is consumed as 'generated' code in a source package and therefore requires an explicit nullable enable
#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CommonLanguageServerProtocol.Framework;

/// <summary>
/// Queues requests to be executed in the proper order.
/// </summary>
/// <typeparam name="TRequestContext">The type of the RequestContext to be used by the handler.</typeparam>
internal interface IRequestExecutionQueue<TRequestContext> : IAsyncDisposable
{
    /// <summary>
    /// Queue a request.
    /// </summary>
    /// <returns>A task that completes when the handler execution is done.</returns>
    Task<object?> ExecuteAsync(object? serializedRequest, string methodName, ILspServices lspServices, CancellationToken cancellationToken);

    /// <summary>
    /// Start the queue accepting requests once any event handlers have been attached.
    /// </summary>
    void Start();

    /// <summary>
    /// Shuts down and disposes the queue, same as <see cref="IAsyncDisposable.DisposeAsync"/>, but reports
    /// whether every in-flight request -- including fire-and-forget non-mutating ones -- actually finished
    /// draining within the bounded wait, as opposed to that wait simply timing out with work still running in
    /// the background. Callers that need "quiesced" to mean something stronger than "we stopped waiting" --
    /// e.g. only emitting a raw out-of-band sentinel byte once it's actually safe from racing an in-flight
    /// response write -- should call this instead of the plain <see cref="IAsyncDisposable.DisposeAsync"/> and
    /// act on the result.
    /// </summary>
    /// <returns><see langword="true"/> if every tracked in-flight task completed before the wait's bound was
    /// reached; <see langword="false"/> if the bound was reached with some work still outstanding (that work
    /// still completes on its own schedule in the background either way).</returns>
    ValueTask<bool> DrainAndDisposeAsync();
}
