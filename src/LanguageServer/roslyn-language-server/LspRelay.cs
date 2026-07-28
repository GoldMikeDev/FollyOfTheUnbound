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
    /// True when, shortly after the first side closed, the other side also closed on its own. A clean LSP
    /// shutdown closes both sides (the editor sends <c>exit</c> and closes; the server processes it and
    /// closes), whereas a crash leaves one side connected.
    /// </summary>
    public bool BothSidesClosed { get; } = bothSidesClosed;
}

internal static class LspRelay
{
    /// <summary>
    /// Grace period to wait for the second side to close after the first does, so a clean shutdown (which
    /// closes both) can be distinguished from a one-sided disconnect (a crash).
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

        // Give the other direction a brief window to finish on its own. A clean shutdown closes both sides, so
        // the other direction's copy will also complete -- and, because it observed the *other* endpoint's
        // stream closing, will report the opposite endpoint. If both directions instead report the same
        // endpoint (e.g. a crash that tears down both of that endpoint's pipes at once), this is not a clean,
        // two-sided shutdown.
        var otherTask = completedTask == editorToServer ? serverToEditor : editorToServer;
        var otherCompletedInTime = await Task.WhenAny(otherTask, Task.Delay(s_secondCloseGracePeriod)).ConfigureAwait(false) == otherTask;
        var bothSidesClosed = otherCompletedInTime && await otherTask.ConfigureAwait(false) != result;

        cancellationSource.Cancel();
        return new RelayResult(result, bothSidesClosed);
    }

    private static async Task<RelayEndpoint> CopyUntilClosedAsync(
        Stream input,
        Stream output,
        RelayEndpoint inputEndpoint,
        RelayEndpoint outputEndpoint,
        CancellationToken cancellationToken)
    {
        var result = await ProcessUtilities.CopyStreamAsync(input, output, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            StreamCopyCompletion.SourceClosed or StreamCopyCompletion.SourceException or StreamCopyCompletion.Cancelled => inputEndpoint,
            StreamCopyCompletion.DestinationException => outputEndpoint,
            _ => throw new InvalidOperationException($"Unexpected stream copy completion kind: {result}"),
        };
    }
}
