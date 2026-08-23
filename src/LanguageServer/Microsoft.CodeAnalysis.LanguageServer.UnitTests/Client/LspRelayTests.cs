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
        private readonly Pipe _editorSink = new();

        public PipeWriter EditorWriter => _editorSource.Writer;
        public PipeWriter ServerWriter => _serverSource.Writer;

        /// <summary>Writes <see cref="CleanExitSentinel"/>, as a real daemon does immediately before a genuine client-requested exit.</summary>
        public async Task WriteServerCleanExitSentinelAsync() => await ServerWriter.WriteAsync(CleanExitSentinel.Bytes);

        /// <summary>
        /// Writes <paramref name="payload"/> as if it were the server's side of the connection, and reads it back
        /// from what the relay forwarded to the editor -- with a short timeout, so a regression that withholds
        /// bytes (waiting to see if more data or EOF follows before forwarding, rather than forwarding
        /// immediately) shows up as this test hanging/timing out rather than silently passing. Does not close
        /// either transport, so this only proves forwarding happened promptly while the connection is still
        /// open -- exactly the case (an idle connection sitting between messages) the regression this guards
        /// against would otherwise deadlock.
        /// </summary>
        public async Task AssertServerPayloadForwardedPromptlyAsync(byte[] payload)
        {
            await ServerWriter.WriteAsync(payload);

            var buffer = new byte[payload.Length];
            var readTask = _editorSink.Reader.AsStream().ReadExactlyAsync(buffer).AsTask();
            var completedTask = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(completedTask == readTask, "Timed out waiting for the relay to forward the server's payload to the editor -- it may be withholding bytes instead of forwarding them immediately.");
            await readTask;

            Assert.Equal(payload, buffer);
        }

        public Task<RelayCompletionKind> RelayAsync(Stream? toEditorOverride = null)
            => LspRelay.RelayAsync(
                fromEditor: _editorSource.Reader.AsStream(),
                toEditor: toEditorOverride ?? _editorSink.Writer.AsStream(),
                fromServer: _serverSource.Reader.AsStream(),
                toServer: new MemoryStream());
    }

    /// <summary>
    /// A write-only stream that throws <see cref="IOException"/> on every <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>,
    /// simulating the editor's transport having already broken -- e.g. the editor disposed its side of the relay
    /// right after telling the server to exit, without waiting for any response.
    /// </summary>
    private sealed class AlwaysFaultingStream : Stream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("Simulated destination failure.");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new IOException("Simulated destination failure.");

        public override void Write(byte[] buffer, int offset, int count)
            => throw new IOException("Simulated destination failure.");

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
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
    public async Task ServerPayloadIsForwardedPromptlyWhileConnectionStaysOpen()
    {
        // Regression guard for the deadlock in an earlier version of the sentinel-detection copy loop: it
        // withheld the single most-recently-read byte from every read, forwarding it only once a subsequent
        // read proved it wasn't the stream's last byte -- so a response's own final byte sat unforwarded for
        // as long as the connection stayed open afterward (i.e. until the daemon's next message, or forever if
        // the session just goes idle after a reply). AssertServerPayloadForwardedPromptlyAsync times out rather
        // than hanging if that regression reappears.
        var harness = new RelayHarness();
        var relayTask = harness.RelayAsync();

        try
        {
            var payload = "Content-Length: 2\r\n\r\n{}"u8.ToArray();
            await harness.AssertServerPayloadForwardedPromptlyAsync(payload);

            // A second payload, to also prove forwarding remains prompt across multiple messages, not just the
            // first one.
            await harness.AssertServerPayloadForwardedPromptlyAsync("Content-Length: 4\r\n\r\n{\"a\":1}"u8.ToArray());
        }
        finally
        {
            // Let RelayAsync's own tasks unwind rather than leaving them running past this test.
            harness.EditorWriter.Complete();
            await harness.WriteServerCleanExitSentinelAsync();
            harness.ServerWriter.Complete();
            await relayTask;
        }
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

    [Fact]
    public async Task DestinationFailsThenServerSentinelArrives_IsStillCleanShutdown()
    {
        // The editor's transport can already be broken by the time the daemon writes its final response plus
        // sentinel -- e.g. the editor disposed its side of the relay right after telling the server to exit,
        // without waiting for any response (see CopyStreamDetectingSentinelAsync's remarks). A regression that
        // gives up on the first failed forward, instead of continuing to read for the sentinel, would misreport
        // this as EditorConnectionLost even though the daemon's own exit was perfectly clean.
        var harness = new RelayHarness();
        var relayTask = harness.RelayAsync(toEditorOverride: new AlwaysFaultingStream());

        await harness.ServerWriter.WriteAsync("Content-Length: 2\r\n\r\n{}"u8.ToArray());
        await harness.WriteServerCleanExitSentinelAsync();
        harness.ServerWriter.Complete();
        harness.EditorWriter.Complete();

        var result = await relayTask;

        Assert.Equal(RelayCompletionKind.CleanShutdown, result);
    }

    [Fact]
    public async Task DestinationFailsAndSentinelNeverArrives_TimesOutAsEditorConnectionLost()
    {
        // If the destination stays broken and the daemon never produces the sentinel, the bounded post-failure
        // drain must eventually give up rather than hang forever -- the regression guard for that bound actually
        // firing and producing a definite result instead of blocking RelayAsync indefinitely.
        var harness = new RelayHarness();
        var relayTask = harness.RelayAsync(toEditorOverride: new AlwaysFaultingStream());

        harness.EditorWriter.Complete();
        await harness.ServerWriter.WriteAsync("Content-Length: 2\r\n\r\n{}"u8.ToArray());
        // Never write the sentinel and never complete the server writer -- the drain has to time out on its own.

        var result = await relayTask;

        Assert.Equal(RelayCompletionKind.EditorConnectionLost, result);
    }
}
