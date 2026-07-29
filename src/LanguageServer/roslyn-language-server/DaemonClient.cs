// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

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
    private static readonly TimeSpan s_daemonMutexTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan s_existingDaemonConnectTimeout = TimeSpan.FromSeconds(5);

    // Must be at least DaemonBootstrap.ReadyTimeout: the bootstrap keeps trying to start the daemon for that
    // long, so a shorter connect timeout here would make us give up on (and report a launch failure for) a
    // daemon that is still legitimately starting, leaving a healthy but unused daemon running behind us.
    private static readonly TimeSpan s_newDaemonConnectTimeout = DaemonBootstrap.ReadyTimeout;

    public static Task<DaemonConnectResult> ConnectAsync(
        ServerExecutable executable,
        IReadOnlyList<string> serverArguments)
    {
        var pipeName = GetDaemonPipeName(executable, serverArguments);

        if (!DaemonClientMutex.TryAcquire(pipeName, s_daemonMutexTimeout, out var clientMutex))
        {
            Console.Error.WriteLine($"Timed out waiting for the daemon startup mutex for pipe '{pipeName}'. Falling back to non-daemon mode.");
            return Task.FromResult(DaemonConnectResult.FallbackToNonDaemon());
        }

        using (clientMutex)
        {
            var launchedDaemon = false;
            if (!DaemonServerMutex.IsRunning(pipeName))
            {
                LaunchDaemon(pipeName, serverArguments);
                launchedDaemon = true;
            }

            try
            {
                return Task.FromResult(DaemonConnectResult.Connected(ConnectPipe(pipeName, launchedDaemon, serverArguments), pipeName));
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

                LaunchDaemon(pipeName, serverArguments);
                return Task.FromResult(DaemonConnectResult.Connected(ConnectPipe(pipeName, launchedDaemon: true, serverArguments), pipeName));
            }
        }
    }

    private static Stream ConnectPipe(string pipeName, bool launchedDaemon, IReadOnlyList<string> serverArguments)
    {
        var pipeClient = NamedPipeUtil.CreateClient(serverName: ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            var connectTimeout = launchedDaemon ? s_newDaemonConnectTimeout : s_existingDaemonConnectTimeout;
            pipeClient.Connect(connectTimeout);

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

    private static void LaunchDaemon(
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
