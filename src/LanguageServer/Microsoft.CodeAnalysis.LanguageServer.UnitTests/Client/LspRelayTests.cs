// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO.Pipelines;
using Microsoft.CodeAnalysis.LanguageServer.Client;
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

        public Task<RelayCompletionKind> RelayAsync()
            => LspRelay.RelayAsync(
                fromEditor: _editorSource.Reader.AsStream(),
                toEditor: new MemoryStream(),
                fromServer: _serverSource.Reader.AsStream(),
                toServer: new MemoryStream());
    }

    [Fact]
    public async Task ServerClosesGracefullyWithoutEditorClosing_IsCleanShutdown()
    {
        // Mirrors ServerExitsOnExitNotificationWithoutClosingTransport: the server processes `exit` and closes
        // its send side, but the editor is not required to (and here, does not) close its own transport.
        var harness = new RelayHarness();

        harness.ServerWriter.Complete();

        var result = await harness.RelayAsync();

        Assert.Equal(RelayCompletionKind.CleanShutdown, result);
    }

    [Fact]
    public async Task BothSidesCloseGracefully_IsCleanShutdown()
    {
        var harness = new RelayHarness();

        harness.EditorWriter.Complete();
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
}
