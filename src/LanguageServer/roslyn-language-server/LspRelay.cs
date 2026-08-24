// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.Daemon;

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

internal enum RelayEndpoint
{
    Editor,
    Server,
}

internal enum RelayCompletionKind
{
    CleanShutdown,
    EditorConnectionLost,
    ServerConnectionLost,
}

internal readonly record struct RelayDirectionResult(RelayEndpoint ClosedEndpoint, bool Graceful, bool CleanExitSentinelSeen = false);

internal static class LspRelay
{
    /// <summary>
    /// Grace period to wait for the second side to close after the first does, so a clean two-sided shutdown
    /// can be distinguished from a one-sided disconnect (a crash) when the server's own closure alone isn't
    /// already conclusive. Only still needed as a fallback for a daemon that doesn't send
    /// <see cref="CleanExitSentinel"/> (e.g. a version skew during an in-place update) -- see the remarks below.
    /// </summary>
    private static readonly TimeSpan s_secondCloseGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bound on how long <see cref="CopyStreamDetectingSentinelAsync"/> keeps reading the source purely to look
    /// for <see cref="CleanExitSentinel"/> after a forward to the destination has already failed. Without this,
    /// a destination that dies asymmetrically from its paired read direction (e.g. stdio transport, where
    /// <c>EditorConnection.Input</c>/<c>Output</c> are independent streams and only the write side breaks) would
    /// leave this loop blocked on the next source read forever, since nothing else would ever cancel it -- the
    /// other relay direction can be blocked the same way on its own read, so <see cref="RelayAsync"/>'s
    /// <c>Task.WhenAny</c> would never observe either task complete. Same duration as
    /// <see cref="s_secondCloseGracePeriod"/> for consistency, not because the two bounds are otherwise related.
    /// </summary>
    private static readonly TimeSpan s_deadDestinationDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Upper bound on how long <see cref="RelayAsync"/> can legitimately still be waiting to conclude once one
    /// direction has already closed -- <see cref="s_secondCloseGracePeriod"/> plus
    /// <see cref="s_deadDestinationDrainTimeout"/>, the worst case when <c>serverToEditor</c>'s destination write
    /// fails right at the end of the base grace period (the latest point <see cref="RelayAsync"/> still honors a
    /// newly-started drain -- see its outer-wait handling) and its own drain then runs its full course. Anything
    /// that force-exits the process on a shorter deadline than this (e.g. <c>Program.StartClientProcessMonitorAsync</c>'s
    /// editor-exit grace window) can kill this relay out from under a shutdown that was only seconds away from
    /// concluding cleanly, clobbering its exit code with a spurious <see cref="RelayCompletionKind.EditorConnectionLost"/>.
    /// </summary>
    public static readonly TimeSpan MaximumShutdownWait = s_secondCloseGracePeriod + s_deadDestinationDrainTimeout;

    public static async Task<RelayCompletionKind> RelayAsync(
        Stream fromEditor,
        Stream toEditor,
        Stream fromServer,
        Stream toServer)
    {
        using var cancellationSource = new CancellationTokenSource();
        // Completed (TrySetResult) the moment serverToEditor's destination write first fails and its own
        // post-failure drain begins -- see the outer-wait handling below for why RelayAsync needs to observe
        // that moment rather than just racing a fixed timer against serverToEditor as a whole.
        var destinationFailedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var editorToServer = CopyUntilClosedAsync(fromEditor, toServer, RelayEndpoint.Editor, RelayEndpoint.Server, detectCleanExitSentinel: false, destinationFailedSignal: null, cancellationSource.Token);
        var serverToEditor = CopyUntilClosedAsync(fromServer, toEditor, RelayEndpoint.Server, RelayEndpoint.Editor, detectCleanExitSentinel: true, destinationFailedSignal, cancellationSource.Token);
        var completedTask = await Task.WhenAny(editorToServer, serverToEditor).ConfigureAwait(false);
        var result = await completedTask.ConfigureAwait(false);

        // The daemon writes CleanExitSentinel immediately before tearing down its JsonRpc connection, but only
        // when the client's own `exit` notification was actually received and processed -- not when the same
        // teardown is instead triggered by the daemon reacting to a lost/broken connection (see
        // AbstractLanguageServer.OnClientRequestedExitAsync's remarks, and GoldMikeDev/roslyn#10 for the three
        // race windows this closes). Its presence is therefore conclusive on its own, and its *absence* on an
        // otherwise-graceful server closure is equally informative: this thin client only ever talks to a
        // daemon built from the exact same version of this repo (DaemonPipeName folds identity/version into the
        // pipe name), so there's no cross-version daemon that might close gracefully without ever attempting to
        // write the sentinel. A graceful server closure that doesn't carry it is therefore not a clean shutdown
        // -- it's this daemon's own reaction to something else (a lost connection, a crash it's cleaning up
        // after) that merely *looks* graceful from here, exactly the ambiguity this signal exists to resolve.
        if (result.ClosedEndpoint == RelayEndpoint.Server && result.CleanExitSentinelSeen)
        {
            cancellationSource.Cancel();
            return RelayCompletionKind.CleanShutdown;
        }

        // Otherwise, give the other direction a brief window to finish on its own and look for the same
        // sentinel-carrying server-closure signal there. This covers both a traditional two-sided shutdown
        // (editor closes first, server follows) and the race where the server's write-side failure is observed
        // before its read-side EOF. If neither direction ever reports it, this was not a clean shutdown -- e.g.
        // a crash that tears down the connection produces ungraceful closures on both directions, regardless of
        // which endpoint they're attributed to.
        var otherTask = completedTask == editorToServer ? serverToEditor : editorToServer;

        // serverToEditor's destination write can fail at any point during this wait -- there's no way to bound
        // "when" in advance -- and once it does, CopyStreamDetectingSentinelAsync starts its own fresh
        // s_deadDestinationDrainTimeout-bounded drain from that moment. A fixed-duration outer race against
        // serverToEditor as a whole can't accommodate that: it either cuts off a drain that started late (an
        // earlier version of this code got this wrong twice -- see known-issues/ide.md), or, if simply removed,
        // leaves this wait genuinely unbounded whenever the daemon never attempts a write at all (e.g. it never
        // responds and never closes) -- which is exactly the case
        // EditorClosesAloneWithoutServerFollowing_IsNotCleanShutdown exercises, and would hang that test (and a
        // real thin client launched without --clientProcessId) forever. So the outer race here targets the
        // *failure*, not the whole task: wait up to s_secondCloseGracePeriod for either serverToEditor to finish
        // on its own or destinationFailedSignal to fire (meaning its post-failure drain has just started); once
        // that drain has started, its own internal timeout is unconditionally the one that governs from then on,
        // however close to the outer deadline it began.
        bool otherCompletedInTime;
        if (otherTask == serverToEditor)
        {
            var graceDelay = Task.Delay(s_secondCloseGracePeriod);
            var firstSignal = await Task.WhenAny(otherTask, destinationFailedSignal.Task, graceDelay).ConfigureAwait(false);
            if (firstSignal == graceDelay)
            {
                otherCompletedInTime = false;
            }
            else
            {
                // Either otherTask already finished, or its drain just started and will now run its own full
                // course -- either way, awaiting it directly is now unconditionally safe and bounded.
                await otherTask.ConfigureAwait(false);
                otherCompletedInTime = true;
            }
        }
        else
        {
            otherCompletedInTime = await Task.WhenAny(otherTask, Task.Delay(s_secondCloseGracePeriod)).ConfigureAwait(false) == otherTask;
        }

        var otherResult = otherCompletedInTime ? await otherTask.ConfigureAwait(false) : (RelayDirectionResult?)null;

        cancellationSource.Cancel();

        if (otherResult is { ClosedEndpoint: RelayEndpoint.Server, CleanExitSentinelSeen: true })
            return RelayCompletionKind.CleanShutdown;

        return result.ClosedEndpoint == RelayEndpoint.Editor
            ? RelayCompletionKind.EditorConnectionLost
            : RelayCompletionKind.ServerConnectionLost;
    }

    private static async Task<RelayDirectionResult> CopyUntilClosedAsync(
        Stream input,
        Stream output,
        RelayEndpoint inputEndpoint,
        RelayEndpoint outputEndpoint,
        bool detectCleanExitSentinel,
        TaskCompletionSource<bool>? destinationFailedSignal,
        CancellationToken cancellationToken)
    {
        var (result, sentinelSeen) = detectCleanExitSentinel
            ? await CopyStreamDetectingSentinelAsync(input, output, destinationFailedSignal, cancellationToken).ConfigureAwait(false)
            : (await ProcessUtilities.CopyStreamAsync(input, output, cancellationToken).ConfigureAwait(false), false);

        return result switch
        {
            StreamCopyCompletion.SourceClosed => new RelayDirectionResult(inputEndpoint, Graceful: true, CleanExitSentinelSeen: sentinelSeen),
            StreamCopyCompletion.SourceException or StreamCopyCompletion.Cancelled => new RelayDirectionResult(inputEndpoint, Graceful: false),
            StreamCopyCompletion.DestinationException => new RelayDirectionResult(outputEndpoint, Graceful: false),
            _ => throw new InvalidOperationException($"Unexpected stream copy completion kind: {result}"),
        };
    }

    /// <summary>
    /// Like <see cref="ProcessUtilities.CopyStreamAsync"/>, but recognizes <see cref="CleanExitSentinel"/> as an
    /// out-of-band marker rather than forwarding it. Unlike a positional "last byte" scheme, this needs no
    /// lookahead or EOF confirmation: the sentinel's byte value can never legitimately appear in real LSP
    /// content (which is `Content-Length`-framed, printable-header-plus-UTF8-JSON traffic), so each read chunk
    /// is scanned for that value and it is stripped in place, immediately, as soon as it's seen. Everything else
    /// is forwarded without delay -- there is no byte withheld pending a later read, so an idle connection after
    /// a response never blocks a byte from reaching the editor.
    /// </summary>
    private static async Task<(StreamCopyCompletion Completion, bool SentinelSeen)> CopyStreamDetectingSentinelAsync(
        Stream source,
        Stream destination,
        TaskCompletionSource<bool>? destinationFailedSignal,
        CancellationToken cancellationToken)
    {
        const int BufferSize = 64 * 1024;
        var buffer = new byte[BufferSize];

        // Once a forward to the destination fails, stop trying to write to it but keep reading from the source:
        // the daemon may still be about to write the sentinel in a later chunk (e.g. the editor disposed its
        // side of the relay right after telling the server to exit, without waiting for any response -- the
        // failed write can easily land on an earlier, unrelated chunk than the one that would have carried the
        // sentinel). Whether the daemon's exit was clean is a fact about what it wrote to the source; it doesn't
        // depend on whether anyone was still listening for the courtesy forward. That drain is bounded by
        // drainCancellation below so a source that never itself closes can't block this forever.
        var destinationAlive = true;
        CancellationTokenSource? drainCancellation = null;

        try
        {
            while (true)
            {
                var readToken = drainCancellation?.Token ?? cancellationToken;
                int bytesRead;
                try
                {
                    bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), readToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (drainCancellation is not null && !cancellationToken.IsCancellationRequested)
                {
                    // The bounded post-failure drain timed out without ever seeing the sentinel -- report the
                    // destination failure that started it, the same outcome as if we'd given up immediately.
                    return (StreamCopyCompletion.DestinationException, SentinelSeen: false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // If the destination already failed, that's the more specific fact: it's what actually
                    // caused this drain, and this source failure could just as easily be a consequence of the
                    // same underlying disconnect racing to be observed on both ends -- reporting SourceException
                    // here would blame the wrong endpoint.
                    return (destinationAlive ? StreamCopyCompletion.SourceException : StreamCopyCompletion.DestinationException, SentinelSeen: false);
                }

                if (bytesRead == 0)
                {
                    // Same reasoning as the source-exception case above: a dead destination is the more specific,
                    // earlier fact, so a graceful source close discovered only while draining for the sentinel
                    // must not overwrite it.
                    return (destinationAlive ? StreamCopyCompletion.SourceClosed : StreamCopyCompletion.DestinationException, SentinelSeen: false);
                }

                var sentinelIndex = Array.IndexOf(buffer, CleanExitSentinel.Value, 0, bytesRead);
                var sawSentinel = sentinelIndex >= 0;
                var forwardLength = sawSentinel ? sentinelIndex : bytesRead;

                // Forward everything up to (but not including) the sentinel, if any -- the daemon writes it
                // immediately before tearing down its JsonRpc connection, so nothing meaningful follows it in the
                // same stream.
                if (destinationAlive && forwardLength > 0)
                {
                    try
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, forwardLength), cancellationToken).ConfigureAwait(false);
                        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        destinationAlive = false;
                        drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        drainCancellation.CancelAfter(s_deadDestinationDrainTimeout);
                        destinationFailedSignal?.TrySetResult(true);
                    }
                }

                if (sawSentinel)
                    return (StreamCopyCompletion.SourceClosed, SentinelSeen: true);

                // A dead destination alone isn't evidence of an unclean exit -- loop back and keep reading from
                // the source (without further forwarding) until it either produces the sentinel or ends on its
                // own, at which point the switch in CopyUntilClosedAsync reports that outcome normally.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (StreamCopyCompletion.Cancelled, SentinelSeen: false);
        }
        finally
        {
            drainCancellation?.Dispose();
        }
    }
}
