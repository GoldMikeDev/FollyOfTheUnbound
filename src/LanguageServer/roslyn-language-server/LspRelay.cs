// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

internal enum RelayEndpoint
{
    Editor,
    Server,
}

internal readonly struct RelayResult(RelayEndpoint closedEndpoint, bool bothSidesClosed)
{
    /// <summary>The endpoint whose stream closed first, ending the relay.</summary>
    public RelayEndpoint ClosedEndpoint { get; } = closedEndpoint;

    /// <summary>
    /// True when the relay observed a clean LSP shutdown: the server processed <c>exit</c> and closed its send
    /// side gracefully (a clean EOF, not a broken connection), or the editor did the same and the server then
    /// followed. Editors are not required to close their own transport after sending <c>exit</c> (the server
    /// exits on its own once it processes the notification), so this does not require the editor side to also
    /// close -- only that the server's closure was graceful rather than an abrupt disconnect.
    /// </summary>
    public bool BothSidesClosed { get; } = bothSidesClosed;
}

internal readonly record struct RelayDirectionResult(RelayEndpoint ClosedEndpoint, bool Graceful);

internal static class LspRelay
{
    /// <summary>
    /// Grace period to wait for the second side to close after the first does, so a clean two-sided shutdown
    /// can be distinguished from a one-sided disconnect (a crash) when the server's own closure alone isn't
    /// already conclusive.
    /// </summary>
    private static readonly TimeSpan s_secondCloseGracePeriod = TimeSpan.FromSeconds(5);

    public static async Task<RelayResult> RelayAsync(
        Stream fromEditor,
        Stream toEditor,
        Stream fromServer,
        Stream toServer)
    {
        using var cancellationSource = new CancellationTokenSource();
        var editorToServer = CopyUntilClosedAsync(fromEditor, toServer, RelayEndpoint.Editor, RelayEndpoint.Server, cancellationSource.Token);
        var serverToEditor = CopyUntilClosedAsync(fromServer, toEditor, RelayEndpoint.Server, RelayEndpoint.Editor, cancellationSource.Token);
        var completedTask = await Task.WhenAny(editorToServer, serverToEditor).ConfigureAwait(false);
        var result = await completedTask.ConfigureAwait(false);

        // A graceful (clean EOF, not an error) closure attributed to the server is sufficient proof on its own
        // that the server finished processing `exit` and shut down normally -- the editor need not have closed
        // its own transport for this to be a clean shutdown, so we don't wait for it.
        if (result.ClosedEndpoint == RelayEndpoint.Server && result.Graceful)
        {
            cancellationSource.Cancel();
            return new RelayResult(result.ClosedEndpoint, bothSidesClosed: true);
        }

        // Otherwise, give the other direction a brief window to finish on its own and look for the same
        // graceful-server signal there. This covers both a traditional two-sided shutdown (editor closes first,
        // server follows) and the race where the server's write-side failure is observed before its read-side
        // EOF. If neither direction ever reports a graceful server closure, this was not a clean shutdown --
        // e.g. a crash that tears down the connection produces ungraceful closures on both directions,
        // regardless of which endpoint they're attributed to.
        var otherTask = completedTask == editorToServer ? serverToEditor : editorToServer;
        var otherCompletedInTime = await Task.WhenAny(otherTask, Task.Delay(s_secondCloseGracePeriod)).ConfigureAwait(false) == otherTask;
        var otherResult = otherCompletedInTime ? await otherTask.ConfigureAwait(false) : (RelayDirectionResult?)null;
        var bothSidesClosed = otherResult is { ClosedEndpoint: RelayEndpoint.Server, Graceful: true };

        cancellationSource.Cancel();
        return new RelayResult(result.ClosedEndpoint, bothSidesClosed);
    }

    private static async Task<RelayDirectionResult> CopyUntilClosedAsync(
        Stream input,
        Stream output,
        RelayEndpoint inputEndpoint,
        RelayEndpoint outputEndpoint,
        CancellationToken cancellationToken)
    {
        var result = await ProcessUtilities.CopyStreamAsync(input, output, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            StreamCopyCompletion.SourceClosed => new RelayDirectionResult(inputEndpoint, Graceful: true),
            StreamCopyCompletion.SourceException or StreamCopyCompletion.Cancelled => new RelayDirectionResult(inputEndpoint, Graceful: false),
            StreamCopyCompletion.DestinationException => new RelayDirectionResult(outputEndpoint, Graceful: false),
            _ => throw new InvalidOperationException($"Unexpected stream copy completion kind: {result}"),
        };
    }
}
