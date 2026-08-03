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

    public static async Task<RelayCompletionKind> RelayAsync(
        Stream fromEditor,
        Stream toEditor,
        Stream fromServer,
        Stream toServer)
    {
        using var cancellationSource = new CancellationTokenSource();
        var editorToServer = CopyUntilClosedAsync(fromEditor, toServer, RelayEndpoint.Editor, RelayEndpoint.Server, detectCleanExitSentinel: false, cancellationSource.Token);
        var serverToEditor = CopyUntilClosedAsync(fromServer, toEditor, RelayEndpoint.Server, RelayEndpoint.Editor, detectCleanExitSentinel: true, cancellationSource.Token);
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
        var otherCompletedInTime = await Task.WhenAny(otherTask, Task.Delay(s_secondCloseGracePeriod)).ConfigureAwait(false) == otherTask;
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
        CancellationToken cancellationToken)
    {
        var (result, sentinelSeen) = detectCleanExitSentinel
            ? await CopyStreamDetectingTrailingSentinelAsync(input, output, cancellationToken).ConfigureAwait(false)
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
    /// Like <see cref="ProcessUtilities.CopyStreamAsync"/>, but recognizes <see cref="CleanExitSentinel"/> as a
    /// trailing, out-of-band marker rather than forwarding it: since data arrives in arbitrarily-sized chunks,
    /// whether a given byte is "the last one" is only knowable once <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>
    /// returns 0 (EOF) with no more bytes following it. This holds back exactly the single most-recently-read
    /// byte (forwarding everything else immediately, same as the non-detecting copy), and only decides whether
    /// it was real content or the sentinel once EOF confirms nothing else follows it. A sentinel-valued byte
    /// that isn't actually the last one (vanishingly unlikely in a real LSP byte stream, but not otherwise
    /// impossible to construct) is therefore still forwarded correctly, since more data arriving after it
    /// proves it wasn't the trailing marker.
    /// </summary>
    private static async Task<(StreamCopyCompletion Completion, bool SentinelSeen)> CopyStreamDetectingTrailingSentinelAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        const int BufferSize = 64 * 1024;
        var buffer = new byte[BufferSize];

        // The single byte withheld from the previous read, not yet known to be real content or the trailing
        // sentinel. Written to `destination` as soon as a subsequent read proves it wasn't the last byte.
        byte? pendingByte = null;

        try
        {
            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    return (StreamCopyCompletion.SourceException, SentinelSeen: false);
                }

                if (bytesRead == 0)
                {
                    // True EOF: no more bytes are ever coming, so any withheld byte's fate is now decided.
                    if (pendingByte is { } finalByte)
                    {
                        if (finalByte == CleanExitSentinel.Value)
                            return (StreamCopyCompletion.SourceClosed, SentinelSeen: true);

                        try
                        {
                            await destination.WriteAsync(new[] { finalByte }.AsMemory(), cancellationToken).ConfigureAwait(false);
                            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                        {
                            return (StreamCopyCompletion.DestinationException, SentinelSeen: false);
                        }
                    }

                    return (StreamCopyCompletion.SourceClosed, SentinelSeen: false);
                }

                try
                {
                    // Forward the previously-withheld byte first (proven not to be the last one, since more
                    // data just arrived), then this read's bytes minus its own last byte, which becomes the
                    // newly withheld one.
                    if (pendingByte is { } previousByte)
                        await destination.WriteAsync(new[] { previousByte }.AsMemory(), cancellationToken).ConfigureAwait(false);

                    if (bytesRead > 1)
                        await destination.WriteAsync(buffer.AsMemory(0, bytesRead - 1), cancellationToken).ConfigureAwait(false);

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    return (StreamCopyCompletion.DestinationException, SentinelSeen: false);
                }

                pendingByte = buffer[bytesRead - 1];
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (StreamCopyCompletion.Cancelled, SentinelSeen: false);
        }
    }
}
