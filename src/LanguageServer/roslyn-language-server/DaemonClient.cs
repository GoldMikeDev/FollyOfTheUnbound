// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

internal sealed class DaemonConnectResult : IDisposable
{
    private DaemonConnectResult(bool daemonConnected, Stream? namedPipeStream, string? pipeName)
    {
        DaemonConnected = daemonConnected;
        NamedPipeStream = namedPipeStream;
        PipeName = pipeName;
    }

    [MemberNotNullWhen(true, nameof(NamedPipeStream))]
    [MemberNotNullWhen(true, nameof(PipeName))]
    public bool DaemonConnected { get; }

    public Stream? NamedPipeStream { get; }

    /// <summary>
    /// The pipe this connection was made to, so callers can independently check
    /// <see cref="DaemonServerMutex.IsRunning(string)"/> later -- e.g. to tell a clean per-connection shutdown
    /// apart from the whole daemon process dying, which the pipe stream's own EOF can't distinguish (a killed
    /// process's pipe handle closes the same way a graceful one does).
    /// </summary>
    public string? PipeName { get; }

    public static DaemonConnectResult Connected(Stream stream, string pipeName)
        => new(daemonConnected: true, stream, pipeName);

    public static DaemonConnectResult FallbackToNonDaemon()
        => new(daemonConnected: false, namedPipeStream: null, pipeName: null);

    public void Dispose()
        => NamedPipeStream?.Dispose();
}

internal static class DaemonClient
{
    private static readonly TimeSpan s_existingDaemonConnectTimeout = TimeSpan.FromSeconds(5);

    // Must be at least the bootstrap's own readiness wait for these serverArguments (DaemonBootstrap.ReadyTimeout
    // normally, or DaemonBootstrap.DebugReadyTimeout when --debug is present): the bootstrap keeps trying to
    // start the daemon for that long, so a shorter connect timeout here would make us give up on (and report a
    // launch failure for) a daemon that is still legitimately starting -- e.g. still waiting for a debugger to
    // attach -- leaving a healthy but unused (and, without a relay, unusable) daemon running behind us.
    private static TimeSpan GetNewDaemonConnectTimeout(IReadOnlyList<string> serverArguments)
        => serverArguments.Contains(DaemonBootstrap.DebugArgument) ? DaemonBootstrap.DebugReadyTimeout : DaemonBootstrap.ReadyTimeout;

    // Must be at least GetNewDaemonConnectTimeout: the client that wins the race to launch the daemon holds
    // this mutex for its entire cold-start connect attempt (up to that timeout), not just the brief "check
    // server, launch if absent" decision -- ConnectAsync's `using (clientMutex)` spans the whole method. A
    // second client that gives up waiting for the mutex sooner than that would conclude the shared daemon is
    // unreachable while it's actually still starting successfully, and launch its own redundant fallback server
    // instead of sharing it -- defeating daemon sharing precisely in the case (slow cold start / heavy MEF
    // composition / a debugger not yet attached) it matters most. The extra 10s is scheduling margin, not part
    // of the legitimate startup window itself.
    private static TimeSpan GetDaemonMutexTimeout(IReadOnlyList<string> serverArguments)
        => GetNewDaemonConnectTimeout(serverArguments) + TimeSpan.FromSeconds(10);

    public static Task<DaemonConnectResult> ConnectAsync(
        ServerExecutable executable,
        IReadOnlyList<string> serverArguments)
    {
        var pipeName = GetDaemonPipeName(executable, serverArguments);
        var daemonMutexTimeout = GetDaemonMutexTimeout(serverArguments);

        if (!DaemonClientMutex.TryAcquire(pipeName, daemonMutexTimeout, out var clientMutex))
        {
            Console.Error.WriteLine($"Timed out waiting for the daemon startup mutex for pipe '{pipeName}'. Falling back to non-daemon mode.");
            return Task.FromResult(DaemonConnectResult.FallbackToNonDaemon());
        }

        using (clientMutex)
        {
            var launchedDaemon = false;
            Process? bootstrapProcess = null;
            if (!DaemonServerMutex.IsRunning(pipeName))
            {
                bootstrapProcess = LaunchDaemon(pipeName, serverArguments);
                launchedDaemon = true;
            }

            try
            {
                return Task.FromResult(DaemonConnectResult.Connected(ConnectPipe(pipeName, launchedDaemon, bootstrapProcess, serverArguments), pipeName));
            }
            catch (TimeoutException) when (!launchedDaemon)
            {
                // The daemon IsRunning observed above can have exited (crashed, or its idle-keepalive window
                // elapsed) in the gap between that check and this connect attempt -- nothing here holds it open,
                // so this is a genuine TOCTOU race, not just a slow daemon. Recheck under the same client mutex
                // (still held, so no other client can race us here) before concluding it's really gone: if it's
                // still running, the timeout is a real connection problem and should propagate as one; otherwise
                // launch a fresh daemon and connect to that instead of surfacing a spurious launch failure for a
                // daemon that simply isn't there anymore.
                if (DaemonServerMutex.IsRunning(pipeName))
                    throw;

                bootstrapProcess = LaunchDaemon(pipeName, serverArguments);
                return Task.FromResult(DaemonConnectResult.Connected(ConnectPipe(pipeName, launchedDaemon: true, bootstrapProcess, serverArguments), pipeName));
            }
        }
    }

    private static Stream ConnectPipe(string pipeName, bool launchedDaemon, Process? bootstrapProcess, IReadOnlyList<string> serverArguments)
    {
        var pipeClient = NamedPipeUtil.CreateClient(serverName: ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            var connectTimeout = launchedDaemon ? GetNewDaemonConnectTimeout(serverArguments) : s_existingDaemonConnectTimeout;
            if (bootstrapProcess is not null)
            {
                ConnectWithBootstrapFailureDetection(pipeClient, bootstrapProcess, connectTimeout);
            }
            else
            {
                pipeClient.Connect(connectTimeout);
            }

            // Tell the daemon which per-connection settings (e.g. our own --extensionLogDirectory) apply to
            // this connection specifically, before the stream becomes the raw LSP relay channel -- otherwise
            // the daemon only ever knows about whichever client's command line happened to launch it. See
            // ConnectionHandshake and docs/ide/specs/daemon-per-connection-isolation.md's phase 5.
            // Blocking (not awaited): this whole method runs on the single thread that holds the caller's
            // DaemonClientMutex, which must not be released from a different thread than it was acquired on
            // -- an `await` here could resume on a different thread pool thread and violate that.
            using var handshakeTimeoutSource = new CancellationTokenSource(s_existingDaemonConnectTimeout);
            ConnectionHandshake.FromServerArguments(serverArguments).WriteAsync(pipeClient, handshakeTimeoutSource.Token).GetAwaiter().GetResult();

            return pipeClient;
        }
        catch
        {
            pipeClient.Dispose();
            throw;
        }
    }

    private static string GetDaemonPipeName(ServerExecutable executable, IReadOnlyList<string> serverArguments)
    {
        // Honor an explicit override so independent instances (chiefly end-to-end tests) can run isolated
        // daemons. Normal clients leave it unset and derive the name from the bundled server path and
        // startup arguments so only compatible clients share a daemon.
        var pipeNameOverride = Environment.GetEnvironmentVariable(DaemonPipeName.PipeNameOverrideEnvironmentVariable);
        return string.IsNullOrEmpty(pipeNameOverride)
            ? DaemonPipeName.GetPipeName(executable.FileName, serverArguments)
            : pipeNameOverride;
    }

    private static Process LaunchDaemon(
        string pipeName,
        IReadOnlyList<string> serverArguments)
    {
        // The daemon is shared across clients and must outlive whichever client launches it. Launching it directly
        // would leave it in this thin client's process tree, vulnerable to an editor tearing that tree down. Instead,
        // launch a short-lived bootstrap (a second copy of this thin client), which launches the real daemon and exits
        // to orphan it out of the editor's process tree. See DaemonBootstrap.
        var bootstrapExecutable = ServerExecutable.ResolveSelf();

        var bootstrapArguments = new List<string>(serverArguments.Count + 3)
        {
            DaemonBootstrap.BootstrapArgument,
            "--pipe",
            pipeName,
        };
        bootstrapArguments.AddRange(serverArguments);

        // Forward the bootstrap's standard error to ours so daemon startup diagnostics still reach the host, and
        // drain its standard output and input so it cannot corrupt the editor's LSP channel in stdio mode.
        // On Windows, mark our standard handles non-inheritable across the launch. A redirected Process.Start uses
        // CreateProcess(bInheritHandles: true), which would otherwise leak our own standard handles - the editor's LSP
        // stdio pipes - to the bootstrap and, transitively, to the long-lived daemon it spawns. A daemon holding those
        // pipes keeps them from ever reaching EOF after we exit (so the editor's WaitForExit/output draining hangs) and
        // in stdio mode corrupts the editor's LSP channel. The bootstrap applies the same protection when it launches
        // the daemon. A no-op off Windows.
        var process = bootstrapExecutable.StartWithStandardHandleInheritanceSuppressed(bootstrapArguments);

        // The bootstrap reads nothing from its stdin; close our write end so it sees EOF if it ever does.
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The short-lived bootstrap may already have exited or closed its redirected standard-input stream.
        }

        // Drain the bootstrap's stdout so it never blocks on a full pipe, and forward its stderr onto ours (never our
        // stdout, which carries LSP in stdio mode). Both end on their own once the bootstrap exits, shortly after it has
        // launched the daemon. The daemon then logs to its own files.
        _ = ProcessUtilities.ForwardStreamAsync(process.StandardOutput.BaseStream, Stream.Null, CancellationToken.None);
        _ = ForwardAndDisposeStandardErrorAsync(process.StandardError.BaseStream);
        Console.Error.WriteLine($"Started language server daemon bootstrap (pid {process.Id})");

        return process;
    }

    /// <summary>
    /// Connects to the daemon's pipe the same way <see cref="NamedPipeClientStream.Connect(int)"/> would, but
    /// polls in short slices instead of blocking for the whole timeout so it can notice <paramref
    /// name="bootstrapProcess"/> exiting early. Without this, a daemon that fails fast (bad command-line
    /// arguments, MEF composition failure, another process already owning the pipe) still leaves this client
    /// blocked for the full <paramref name="connectTimeout"/> (up to <see cref="GetNewDaemonConnectTimeout"/>,
    /// normally 60s) even though <see cref="DaemonBootstrap.RunAsync"/> already knew the failure within a couple of
    /// seconds and reported it on this same bootstrap process's exit code -- the editor would appear hung for
    /// up to a minute for a failure that was already known.
    /// </summary>
    private static void ConnectWithBootstrapFailureDetection(NamedPipeClientStream pipeClient, Process bootstrapProcess, TimeSpan connectTimeout)
    {
        var pollInterval = TimeSpan.FromMilliseconds(100);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var remaining = connectTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Timed out after {connectTimeout} connecting to the daemon pipe.");

            var thisAttemptTimeout = remaining < pollInterval ? remaining : pollInterval;
            try
            {
                pipeClient.Connect((int)thisAttemptTimeout.TotalMilliseconds);
                return;
            }
            catch (TimeoutException)
            {
                // The bootstrap exits with Success as soon as it observes the daemon's readiness mutex
                // (DaemonBootstrap.WaitForReadyOrExitAsync), which can happen slightly before the daemon
                // finishes standing up its pipe listener in AcceptConnectionsAsync -- so a successful bootstrap
                // exit racing a poll slice here is expected, not a failure. Only a nonzero exit code means the
                // daemon itself failed to start; keep polling for the pipe in every other case (still exited
                // with Success, or not exited yet).
                if (bootstrapProcess.HasExited && bootstrapProcess.ExitCode != ExitCodes.Success)
                {
                    throw new InvalidOperationException(
                        $"The language server daemon bootstrap exited with code {bootstrapProcess.ExitCode} before this client could connect. See its forwarded standard error output above for the failure reason.");
                }
            }
        }
    }

    private static Task ForwardAndDisposeStandardErrorAsync(Stream daemonStandardError)
    {
        return Task.Run(async () =>
        {
            try
            {
                await ProcessUtilities.CopyStreamAsync(daemonStandardError, Console.OpenStandardError(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await daemonStandardError.DisposeAsync().ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }
}
