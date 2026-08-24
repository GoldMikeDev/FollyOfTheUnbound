// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.LanguageServer.Client.Interop;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Hidden hook for Win32BreakawayLauncherTests (see BreakawaySelfTest's remarks) to verify
        // GoldMikeDev/roslyn#11 against a real Windows Job Object; checked before normal argument parsing the
        // same way the daemon bootstrap marker below is.
        if (BreakawaySelfTest.IsParentRequested(args))
        {
            return BreakawaySelfTest.RunParent();
        }

        if (BreakawaySelfTest.IsChildRequested(args))
        {
            return BreakawaySelfTest.RunChild(args);
        }

        // When a client needs a shared daemon it launches a second copy of this thin client as a short-lived bootstrap
        // (see DaemonClient.LaunchDaemon / DaemonBootstrap). In that mode we just launch the real daemon detached and
        // exit - we are not an editor's client, so skip normal client argument parsing and process monitoring.
        if (DaemonBootstrap.IsBootstrapRequested(args))
        {
            return await DaemonBootstrap.RunAsync(args);
        }

        ThinClientArguments thinClientArguments;
        try
        {
            thinClientArguments = ThinClientArguments.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.BadArguments;
        }

        // Cancelled once this method has its own conclusive exit code (success path below, or an exception here),
        // so a normal post-shutdown editor process exit racing that result doesn't let the monitor's
        // Environment.Exit(EditorConnectionLost) below clobber it -- see the monitor's own remarks.
        using var clientMonitorCancellation = new CancellationTokenSource();
        _ = StartClientProcessMonitorAsync(thinClientArguments.ClientProcessId, clientMonitorCancellation.Token);

        try
        {
            var executable = ServerExecutable.ResolveLanguageServer();

            if (thinClientArguments.DaemonMode)
            {
                using var daemonResult = await DaemonClient.ConnectAsync(
                    executable,
                    thinClientArguments.ServerArguments);

                if (daemonResult.DaemonConnected)
                {
                    // The daemon hosts the server for every client, so the thin client must bridge the editor's
                    // transport to the daemon's pipe (it connects to both and relays between them).
                    using var editorConnection = await EditorConnection.CreateAsync(thinClientArguments);
                    var relayExitCode = await RelayDaemonAsync(
                        daemonResult.NamedPipeStream,
                        editorConnection);
                    clientMonitorCancellation.Cancel();
                    return relayExitCode;
                }

                Console.Error.WriteLine("Running language server in non-daemon fallback mode.");
            }

            // Single-server mode: launch a dedicated server on the editor's own transport. In pipe mode the server
            // connects to the editor's pipe directly, so the thin client stays out of the LSP message path.
            var childExitCode = await ChildServerHost.RunAsync(
                executable,
                thinClientArguments);
            clientMonitorCancellation.Cancel();
            return childExitCode;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidOperationException or TimeoutException)
        {
            clientMonitorCancellation.Cancel();
            Console.Error.WriteLine(ex);
            return ExitCodes.ServerLaunchOrConnectFailure;
        }
    }

    private static async Task<int> RelayDaemonAsync(
        Stream daemonStream,
        EditorConnection editorConnection)
    {
        // LspRelay's CleanShutdown classification is grounded in CleanExitSentinel (see LspRelay.RelayAsync's
        // remarks and GoldMikeDev/roslyn#10): it's only ever returned when the daemon actually wrote that
        // out-of-band byte immediately before tearing down this connection's JsonRpc, which only happens when
        // the client's own 'exit' notification was received and processed. That's a conclusive, per-connection
        // signal on its own -- unlike the previous "any graceful server-side closure" heuristic this replaced,
        // it does not need corroborating with whether the daemon process itself is still alive afterward
        // (checking DaemonServerMutex.IsRunning here used to disambiguate that heuristic from "the whole daemon
        // just died," but doing so now would wrongly downgrade a genuinely clean per-connection shutdown to
        // ServerConnectionLost whenever the daemon happens to exit immediately after, e.g. the last client with
        // --daemonKeepAlive 0 -- the daemon dying afterward doesn't retroactively make this connection's own
        // clean exit not have happened).
        var relayCompletionKind = await LspRelay.RelayAsync(
            editorConnection.Input,
            editorConnection.Output,
            daemonStream,
            daemonStream);

        switch (relayCompletionKind)
        {
            case RelayCompletionKind.CleanShutdown:
                Console.Error.WriteLine("Language server session ended cleanly.");
                return ExitCodes.Success;

            case RelayCompletionKind.EditorConnectionLost:
                Console.Error.WriteLine("Editor connection closed before the language server daemon connection.");
                return ExitCodes.EditorConnectionLost;

            case RelayCompletionKind.ServerConnectionLost:
                Console.Error.WriteLine("Language server daemon connection closed before the editor connection.");
                return ExitCodes.ServerConnectionLost;

            default:
                throw new InvalidOperationException($"Unexpected relay completion kind: {relayCompletionKind}");
        }
    }

    /// <summary>
    /// Extra slack added on top of <see cref="LspRelay.MaximumShutdownWait"/> in
    /// <see cref="s_editorExitForceExitGracePeriod"/>. The two deadlines are measured from different starting
    /// points: <see cref="LspRelay.MaximumShutdownWait"/> is the worst case measured from when the *first* relay
    /// direction completes (typically <c>editorToServer</c> observing EOF once the editor closes its transport),
    /// while this grace period is measured from <see cref="Process.WaitForExitAsync"/> observing the editor's OS
    /// process actually exit. Those aren't the same instant -- the editor's process can linger briefly after
    /// closing its transport (or vice versa) -- so an equal-duration deadline here doesn't actually guarantee this
    /// monitor waits at least as long as the relay's own legitimate wait; this slack covers that gap.
    /// </summary>
    private static readonly TimeSpan s_editorExitForceExitGraceSlack = TimeSpan.FromSeconds(2);

    /// <summary>Bound on how long a detected editor-process exit waits for <see cref="Main"/>'s own conclusive
    /// result before force-exiting over it; see <see cref="StartClientProcessMonitorAsync"/>'s remarks. Must be at
    /// least <see cref="LspRelay.MaximumShutdownWait"/> -- a shorter deadline here can force-exit a daemon-mode
    /// relay that was still within its own legitimate wait for a clean-exit sentinel, clobbering its exit code --
    /// plus <see cref="s_editorExitForceExitGraceSlack"/>, since the two deadlines start from different events.</summary>
    private static readonly TimeSpan s_editorExitForceExitGracePeriod = LspRelay.MaximumShutdownWait + s_editorExitForceExitGraceSlack;

    /// <summary>
    /// Force-exits if the monitored editor process disappears out from under us -- the normal signal for "the
    /// editor crashed or was killed outright, with no chance to go through its own LSP shutdown/exit sequence".
    /// </summary>
    /// <remarks>
    /// A well-behaved editor's own process can legitimately exit at roughly the same time its LSP session ends
    /// cleanly (send <c>exit</c>, then tear down its own process shortly after) -- racing
    /// <see cref="Main"/>'s own conclusive exit code from <see cref="RelayDaemonAsync"/>/<see cref="ChildServerHost.RunAsync"/>.
    /// <paramref name="cancellationToken"/> is cancelled by <see cref="Main"/> once it has that conclusive
    /// result, closing that window: a cancellation observed here means the session already ended on its own
    /// terms, so this must not force a different exit code over it.
    /// <para>
    /// That cancellation only happens once <see cref="RelayDaemonAsync"/>/<see cref="ChildServerHost.RunAsync"/>
    /// actually *returns*, though -- and the normal interleaving is the editor's own process exiting shortly
    /// after it sends <c>exit</c>, while the relay is still mid-flight processing the daemon's response to it
    /// (e.g. writing the clean-exit sentinel through to the editor). <see cref="Process.WaitForExitAsync"/>
    /// observes the real OS-level process exit independently of relay progress, so without a grace window this
    /// could <see cref="Environment.Exit"/> out from under a session that was seconds away from concluding
    /// cleanly on its own, both clobbering its exit code and hard-killing the relay mid-write. Instead of
    /// exiting immediately, give <see cref="Main"/>'s own conclusion a short, bounded window
    /// (<see cref="s_editorExitForceExitGracePeriod"/>) to land first.
    /// </para>
    /// </remarks>
    private static Task StartClientProcessMonitorAsync(int? processId, CancellationToken cancellationToken)
    {
        if (processId is null)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                using var process = Process.GetProcessById(processId.Value);
                await process.WaitForExitAsync(cancellationToken);
                Console.Error.WriteLine("Monitored editor process exited.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Main already has its own conclusive exit code; nothing to force here.
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error monitoring editor process: {ex}");
            }

            // The editor process just exited (or we failed to monitor it), but Main may already be seconds away
            // from its own conclusive result -- give it a bounded grace window to get there before overriding it.
            var deadline = DateTime.UtcNow + s_editorExitForceExitGracePeriod;
            while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // Main concluded on its own during the grace window; nothing to force.
                return;
            }

            Environment.Exit(ExitCodes.EditorConnectionLost);
        }, CancellationToken.None);
    }
}
