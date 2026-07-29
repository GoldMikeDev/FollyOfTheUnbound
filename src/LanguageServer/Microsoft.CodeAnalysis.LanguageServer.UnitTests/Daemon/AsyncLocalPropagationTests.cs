// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO.Pipelines;
using StreamJsonRpc;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

/// <summary>
/// Phase 1 of the daemon per-connection isolation work (see
/// docs/ide/specs/daemon-per-connection-isolation.md): verifies, before any real per-connection context
/// primitive is built, that an <see cref="AsyncLocal{T}"/> value actually survives the async transitions the
/// daemon's request-handling path goes through. If any of these dropped the value, an ambient-context design
/// built on <see cref="AsyncLocal{T}"/> would silently misattribute state between connections instead of
/// reliably isolating it -- worth knowing before, not after, something depends on it.
/// </summary>
public sealed class AsyncLocalPropagationTests
{
    private static readonly AsyncLocal<string?> s_ambientValue = new();

    [Fact]
    public async Task SurvivesDirectAwait()
    {
        s_ambientValue.Value = "connection-1";

        await Task.Yield();

        Assert.Equal("connection-1", s_ambientValue.Value);
    }

    [Fact]
    public async Task SurvivesConfigureAwaitFalse()
    {
        s_ambientValue.Value = "connection-1";

        await Task.Delay(1).ConfigureAwait(false);

        Assert.Equal("connection-1", s_ambientValue.Value);
    }

    [Fact]
    public async Task SurvivesTaskRun()
    {
        s_ambientValue.Value = "connection-1";

        // Task.Run captures ExecutionContext at the call site by default, so the ambient value set just above
        // should flow into the queued work -- this is the behavior an ambient-context primitive would rely on
        // for work fanned out via Task.Run (e.g. LanguageServerConnectionManager's per-daemon-connection
        // supervisor tasks).
        var observedInsideTaskRun = await Task.Run(() => s_ambientValue.Value);

        Assert.Equal("connection-1", observedInsideTaskRun);
    }

    [Fact]
    public async Task DoesNotLeakBetweenConcurrentTaskRunCalls()
    {
        // The critical isolation property: two "connections" running concurrently must not see each other's
        // ambient value, since AsyncLocal<T> is only isolated per logical call context, not automatically
        // per-connection -- this is exactly the property a real per-connection context needs to preserve.
        async Task<string> RunWithAmbientValue(string value)
        {
            s_ambientValue.Value = value;
            await Task.Delay(Random.Shared.Next(1, 20)).ConfigureAwait(false);
            return s_ambientValue.Value!;
        }

        var results = await Task.WhenAll(
            Task.Run(() => RunWithAmbientValue("connection-1")),
            Task.Run(() => RunWithAmbientValue("connection-2")),
            Task.Run(() => RunWithAmbientValue("connection-3")));

        Assert.Equal(["connection-1", "connection-2", "connection-3"], results);
    }

    [Fact]
    public async Task DoesNotFlowFromChildBackToParent()
    {
        // AsyncLocal<T> changes are only visible to the call context that made them and its descendants, never
        // back up to the caller -- setting it inside Task.Run must not affect the value observed after the
        // Task.Run call returns.
        s_ambientValue.Value = "outer";

        await Task.Run(() => s_ambientValue.Value = "inner-should-not-escape");

        Assert.Equal("outer", s_ambientValue.Value);
    }

    /// <summary>
    /// The daemon's actual request path: a client sends an LSP request, StreamJsonRpc dispatches it to a
    /// handler method on the server side. This verifies the ambient value set once per accepted connection
    /// (the design's intended usage) is still observable inside a method StreamJsonRpc invokes for that
    /// connection's JsonRpc instance, not just through plain Task/await primitives in isolation.
    /// </summary>
    [Fact]
    public async Task SurvivesStreamJsonRpcDispatch()
    {
        var serverPipe = new Pipe();
        var clientPipe = new Pipe();

        var serverTarget = new JsonRpcTarget();
        using var server = new JsonRpc(serverPipe.Writer.AsStream(), clientPipe.Reader.AsStream(), serverTarget);
        using var client = new JsonRpc(clientPipe.Writer.AsStream(), serverPipe.Reader.AsStream());

        // Simulate what NamedPipeDaemonConnectionSource.AcceptConnectionsAsync would do once per accepted
        // connection, before starting that connection's JsonRpc listen loop: establish the ambient value for
        // everything that happens on behalf of this connection from here on.
        s_ambientValue.Value = "connection-1";
        server.StartListening();
        client.StartListening();

        var observedInsideHandler = await client.InvokeAsync<string?>(nameof(JsonRpcTarget.GetAmbientValue));

        Assert.Equal("connection-1", observedInsideHandler);
    }

    /// <summary>
    /// Documents the exact ordering bug a Codex review found in an earlier version of
    /// <c>LanguageServerConnectionManager.RunAsync</c>: it called <c>DaemonConnectionContext.SetCurrent</c>
    /// only after constructing that connection's <c>LanguageServerHost</c>, but
    /// <c>LanguageServerHost</c>'s constructor synchronously starts <c>RequestExecutionQueue</c>'s
    /// background dispatch loop (via <c>AbstractLanguageServer.Initialize()</c>) -- so the loop's
    /// <see cref="ExecutionContext"/> was already captured, permanently, with no ambient value, before
    /// <c>SetCurrent</c> ever ran. Every request the loop later dispatched (and everything <em>that</em>
    /// scheduled, like <see cref="Task.Run(Action)"/> for each request) inherited that same empty context
    /// regardless of what the ambient value was elsewhere at dispatch time.
    /// </summary>
    [Fact]
    public async Task ValueSetAfterAsyncWorkAlreadyStarted_IsNotObservedByThatWork()
    {
        var startedLoopSawValue = new TaskCompletionSource<string?>();
        var releaseLoop = new TaskCompletionSource();

        // Starts "the loop" (standing in for RequestExecutionQueue.ProcessQueueAsync) before any ambient value
        // is set -- its ExecutionContext is captured right now, at this call, per normal Task.Run semantics.
        var loop = Task.Run(async () =>
        {
            await releaseLoop.Task;
            startedLoopSawValue.SetResult(s_ambientValue.Value);
        });

        // Only now does the "connection" become ambient -- too late for the loop already running above, which
        // is exactly the bug: this ordering looks correct locally (the value is set before doing anything
        // request-shaped) but the loop it needed to reach had already started beforehand.
        s_ambientValue.Value = "connection-1";
        releaseLoop.SetResult();

        var observedByLoop = await startedLoopSawValue.Task;
        await loop;

        Assert.Null(observedByLoop);
    }

    /// <summary>
    /// The fix's actual requirement: the ambient value must be set *before* the work that will read it later
    /// is started (constructed/scheduled), not merely before that work is explicitly kicked off
    /// (<c>Start()</c>/<c>StartListening()</c>) -- see <c>LanguageServerConnectionManager</c>'s
    /// <c>AmbientConnectionToken.SetCurrent</c> call, which now happens before constructing the
    /// <c>LanguageServerHost</c> rather than merely before <c>LanguageServerHost.Start</c>.
    /// </summary>
    [Fact]
    public async Task ValueSetBeforeAsyncWorkStarted_IsObservedByThatWork()
    {
        s_ambientValue.Value = "connection-1";

        var observedByLoop = await Task.Run(async () =>
        {
            await Task.Yield();
            return s_ambientValue.Value;
        });

        Assert.Equal("connection-1", observedByLoop);
    }

    private sealed class JsonRpcTarget
    {
        public string? GetAmbientValue() => s_ambientValue.Value;
    }
}
