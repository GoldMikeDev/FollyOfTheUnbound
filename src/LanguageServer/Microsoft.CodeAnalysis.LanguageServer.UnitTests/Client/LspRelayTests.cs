// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO.Pipelines;
using Microsoft.CodeAnalysis.LanguageServer.Client;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class LspRelayTests
{
    /// <summary>
    /// Sets up the four streams <see cref="LspRelay.RelayAsync"/> takes, backed by two independently-controllable
    /// pipes for the read side of each direction (the side these tests need to close/fault to simulate the editor
    /// or server ending the session) and plain sinks for the write side (nothing forwarded in these tests ever
    /// produces bytes to write, since only completion/fault signals are exercised, not real payload data).
    /// </summary>
    private sealed class RelayHarness
    {
        private readonly Pipe _editorSource = new();
        private readonly Pipe _serverSource = new();

        public PipeWriter EditorWriter => _editorSource.Writer;
        public PipeWriter ServerWriter => _serverSource.Writer;

        /// <summary>Writes <see cref="CleanExitSentinel"/>, as a real daemon does immediately before a genuine client-requested exit.</summary>
        public async Task WriteServerCleanExitSentinelAsync() => await ServerWriter.WriteAsync(CleanExitSentinel.Bytes);

        public Task<RelayCompletionKind> RelayAsync()
            => LspRelay.RelayAsync(
                fromEditor: _editorSource.Reader.AsStream(),
                toEditor: new MemoryStream(),
                fromServer: _serverSource.Reader.AsStream(),
                toServer: new MemoryStream());
    }

    [Fact]
    public async Task ServerSendsCleanExitSentinelWithoutEditorClosing_IsCleanShutdown()
    {
        // Mirrors ServerExitsOnExitNotificationWithoutClosingTransport: the server processes `exit`, writes the
        // sentinel, and closes its send side, but the editor is not required to (and here, does not) close its
        // own transport.
        var harness = new RelayHarness();

        await harness.WriteServerCleanExitSentinelAsync();
        harness.ServerWriter.Complete();

        var result = await harness.RelayAsync();

        Assert.Equal(RelayCompletionKind.CleanShutdown, result);
    }

    [Fact]
    public async Task BothSidesCloseGracefullyWithSentinel_IsCleanShutdown()
    {
        var harness = new RelayHarness();

        harness.EditorWriter.Complete();
        await harness.WriteServerCleanExitSentinelAsync();
        harness.ServerWriter.Complete();

        var result = await harness.RelayAsync();

        Assert.Equal(RelayCompletionKind.CleanShutdown, result);
    }

    [Fact]
    public async Task ServerCrashesUngracefully_IsNotCleanShutdown()
    {
        // The server's connection breaks (an exception reading from it, not a clean EOF) and the editor never
        // follows -- this must not be mistaken for a clean shutdown. This is the regression guard for the
        // original bug this classification was added to fix: naively treating "the server side ended" as proof
        // of a clean shutdown regardless of whether it was graceful.
        var harness = new RelayHarness();

        harness.ServerWriter.Complete(new IOException("simulated daemon crash"));

        var result = await harness.RelayAsync();

        Assert.Equal(RelayCompletionKind.ServerConnectionLost, result);
    }

    [Fact]
    public async Task EditorClosesAloneWithoutServerFollowing_IsNotCleanShutdown()
    {
        var harness = new RelayHarness();

        harness.EditorWriter.Complete();

        var result = await harness.RelayAsync();

        Assert.Equal(RelayCompletionKind.EditorConnectionLost, result);
    }

    [Fact]
    public async Task ServerClosesGracefullyWithoutSentinel_IsNotCleanShutdown()
    {
        // Regression guard for GoldMikeDev/roslyn#10's third race window: the editor's transport reaches EOF
        // without ever sending `exit` (e.g. it crashed or was force-closed mid-session), the daemon's
        // JsonRpc_Disconnected handler reacts by tearing down that connection's logical server, and that
        // teardown closes the server's own transport gracefully -- indistinguishable, before this fix, from a
        // genuine exit-triggered clean shutdown. Simulated here directly: the server side closes gracefully
        // (clean EOF) but never writes CleanExitSentinel, exactly what a disconnect-triggered (not
        // exit-notification-triggered) teardown produces. Before the sentinel existed, RelayAsync treated any
        // graceful server-side closure as proof of a clean shutdown and this would have incorrectly returned
        // CleanShutdown.
        var harness = new RelayHarness();

        harness.EditorWriter.Complete();
        harness.ServerWriter.Complete();

        var result = await harness.RelayAsync();

        Assert.Equal(RelayCompletionKind.EditorConnectionLost, result);
    }

    [Fact]
    public async Task ServerClosesGracefullyWithoutSentinel_EditorNeverCloses_IsNotCleanShutdown()
    {
        // Same race as above, but from the angle of the original ServerClosesGracefullyWithoutEditorClosing_IsCleanShutdown
        // test this replaces: a graceful server-side closure alone, with no sentinel and no editor closure
        // either, must not be treated as a clean shutdown -- it has to actually carry the sentinel now.
        var harness = new RelayHarness();

        harness.ServerWriter.Complete();

        var result = await harness.RelayAsync();

        Assert.Equal(RelayCompletionKind.ServerConnectionLost, result);
    }
}
