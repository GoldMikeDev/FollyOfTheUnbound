// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

extern alias MSBuildWorkspaces;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using Microsoft.CodeAnalysis.LanguageServer.Daemon;
using Microsoft.Extensions.Logging;
using RoslynLog = Microsoft.CodeAnalysis.Internal.Log;

// Reuse the compiler server's named-pipe helper (Asynchronous | WriteThrough | CurrentUserOnly,
// MaxAllowedServerInstances, and Unix /tmp socket-path handling). It is source-linked into
// Microsoft.CodeAnalysis.Workspaces.MSBuild, which this project already references under the
// MSBuildWorkspaces alias, so we use that already-compiled copy rather than source-linking another
// copy into this assembly (which would collide with the MSBuild build host's copy of the same type).
using NamedPipeUtil = MSBuildWorkspaces::Microsoft.CodeAnalysis.NamedPipeUtil;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// A connection source for daemon mode: owns the server mutex (which signals "a daemon is running" for
/// this pipe) and accepts client connections on a named pipe, handing each a dedicated, independent
/// <see cref="System.IO.Pipes.NamedPipeServerStream"/>.
/// </summary>
internal sealed class NamedPipeDaemonConnectionSource : ILanguageServerConnectionSource, IDisposable
{
    private static readonly TimeSpan s_initialConnectionTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_handshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly Mutex _serverMutex;
    private readonly ConnectionIdleTimeout _idleTimeout;

    /// <summary>
    /// Completes once <see cref="RunAcceptLoopAsync"/> has created its first listening pipe instance and is about
    /// to wait on it -- i.e. once a client connecting to <see cref="_pipeName"/> can actually expect its connect
    /// call to succeed. Exposed for tests only (see <see cref="TestAccessor.WaitForAcceptLoopStartedAsync"/>):
    /// production callers race a real client's connect-with-retry/timeout against this loop starting up on a
    /// background <see cref="Task"/>, but a test harness that connects a client immediately after constructing
    /// this source (no such retry) can otherwise connect before the OS pipe is actually listening.
    /// </summary>
    private readonly TaskCompletionSource _acceptLoopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Action? _onConnectionAccepted;

    private NamedPipeDaemonConnectionSource(
        string pipeName,
        Mutex serverMutex,
        TimeSpan initialConnectionTimeout,
        TimeSpan keepAlive,
        ILogger logger)
    {
        _pipeName = pipeName;
        _serverMutex = serverMutex;
        _idleTimeout = new ConnectionIdleTimeout(initialConnectionTimeout, keepAlive, logger);
        _logger = logger;
    }

    public bool ShouldIsolateConnectionFaults => true;

    /// <summary>
    /// Attempts to become the daemon for <paramref name="pipeName"/> by acquiring the server mutex.
    /// Returns <see langword="false"/> (without creating a source) if another daemon already owns it.
    /// </summary>
    public static bool TryCreate(
        string pipeName,
        TimeSpan keepAlive,
        ILogger logger,
        [NotNullWhen(true)] out NamedPipeDaemonConnectionSource? source,
        TimeSpan? initialConnectionTimeout = null)
    {
        if (!DaemonServerMutex.TryAcquire(pipeName, out var serverMutex))
        {
            logger.LogWarning(
                "A language server daemon already owns pipe '{pipeName}'; this instance will exit so clients use the existing daemon.",
                pipeName);
            source = null;
            return false;
        }

        source = new NamedPipeDaemonConnectionSource(
            pipeName, serverMutex, initialConnectionTimeout ?? s_initialConnectionTimeout, keepAlive, logger);
        RoslynLog.Logger.Log(RoslynLog.FunctionId.VSCode_LanguageServer_Daemon_Started, logLevel: RoslynLog.LogLevel.Information);
        return true;
    }

    /// <summary>
    /// Accepts client connections. The raw accept loop (<see cref="RunAcceptLoopAsync"/>) only ever does
    /// <see cref="NamedPipeUtil.CreateServer"/> + <c>WaitForConnectionAsync</c> in a tight loop and never blocks on
    /// a single client -- each accepted connection's elevation check and (up to <see cref="s_handshakeTimeout"/>)
    /// handshake read run independently in <see cref="ProcessAcceptedConnectionAsync"/>, handed off via a channel.
    /// This is necessary, not just an optimization: an <see cref="IAsyncEnumerable{T}"/>'s <c>yield return</c> is
    /// consumed one <c>MoveNextAsync</c> at a time, so if accept-and-validate were a single sequential loop body
    /// (even one that pre-creates the *next* listening pipe instance before validating the current one), a client
    /// that connects but withholds its handshake would still keep that MoveNextAsync call from ever completing --
    /// blocking every other already-accepted client from being yielded until it times out. Routing validated
    /// connections through a channel lets many validations run concurrently while the enumerator just drains
    /// whichever ones finish, in whatever order they finish.
    /// </summary>
    public async IAsyncEnumerable<LanguageServerConnection> AcceptConnectionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<LanguageServerConnection>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // Fire-and-forget: this task's own life cycle is expressed entirely through the channel it writes to
        // (completed normally, or with the loop's terminating exception, from every one of its exit paths).
        _ = RunAcceptLoopAsync(channel.Writer, cancellationToken);

        await foreach (var connection in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return connection;
        }
    }

    /// <summary>
    /// Never blocks on a single client: creates a listening pipe instance, waits for a connection, and as soon as
    /// one arrives hands it off to <see cref="ProcessAcceptedConnectionAsync"/> (not awaited) before immediately
    /// looping back to listen again. Terminates by completing <paramref name="writer"/> -- normally on
    /// cancellation or a committed idle timeout, or with an exception if something unexpected escapes the loop
    /// (surfaced to <see cref="AcceptConnectionsAsync"/>'s <c>await foreach</c> the same way an exception thrown
    /// directly from the old single-loop-body implementation used to propagate).
    /// </summary>
    private async Task RunAcceptLoopAsync(ChannelWriter<LanguageServerConnection> writer, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var timeoutToken = _idleTimeout.TimeoutToken;
                var pipeStream = NamedPipeUtil.CreateServer(_pipeName);

                // Idempotent (TrySetResult only ever takes effect on the first, i.e. this loop's very first,
                // iteration): a client connecting to _pipeName can now expect to actually succeed.
                _acceptLoopStarted.TrySetResult();

                try
                {
                    using var acceptCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken);
                    await pipeStream.WaitForConnectionAsync(acceptCancellationSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await pipeStream.DisposeAsync().ConfigureAwait(false);

                    // Let ReadAllAsync's own cancellationToken observe this the same way the old implementation's
                    // 'throw;' propagated OperationCanceledException out of the enumerator.
                    writer.TryComplete();
                    return;
                }
                catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
                {
                    // The idle timeout and a real client connecting can race: the OS-level connection can complete
                    // just before the token's cancellation callback aborts the pending I/O, in which case
                    // WaitForConnectionAsync can still surface OperationCanceledException even though the pipe is
                    // actually connected now. Don't discard an accepted client on that race -- fall through to
                    // treat this the same as a successful connection instead of committing to shutdown.
                    if (!pipeStream.IsConnected)
                    {
                        await pipeStream.DisposeAsync().ConfigureAwait(false);

                        // This wait's generation might be stale (superseded by a connection accepted since this
                        // wait started, and concurrently being validated in ProcessAcceptedConnectionAsync) rather
                        // than a genuine "idle with zero connections" shutdown -- see TryCommitTimeout. Only
                        // actually shut down when it confirms that; otherwise this was a spurious cancellation and
                        // accepting should just continue.
                        if (_idleTimeout.TryCommitTimeout())
                        {
                            writer.TryComplete();
                            return;
                        }

                        continue;
                    }
                }
                catch (Exception ex)
                {
                    // Failing to accept one connection shouldn't take down the daemon; log and try again.
                    _logger.LogError(ex, "Daemon encountered an error while waiting for a client connection.");
                    await pipeStream.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                // 'pipeStream' has now genuinely accepted a client. Record it with the idle timeout immediately,
                // *before* handing it off for independent validation below and looping back to listen again:
                // OpenConnection advances to a fresh timeout generation, so this loop's next iteration -- which
                // snapshots the (then-current) generation's token for its own timeout-vs-cancellation race
                // handling above -- only ever observes a generation that already accounts for this connection.
                // Doing this after handing off (or not at all until validation succeeds) would let the idle
                // timeout elapse and race the next accept while this connection is still uncounted, violating
                // ConnectionIdleTimeout's invariant that a real commit only happens with zero active connections.
                //
                // This does mean a connection that fails its elevation check or handshake still counts as "opened"
                // for idle-timeout purposes for that brief window; ProcessAcceptedConnectionAsync's rejection paths
                // call CloseConnection to balance it out, the same as a real connection's ConnectionResource.Dispose
                // would.
                _idleTimeout.OpenConnection();

                _ = ProcessAcceptedConnectionAsync(pipeStream, writer, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Surface anything unexpected that escaped the loop's own per-connection handling the same way it
            // would have propagated out of the old single-loop-body implementation's 'await foreach'.
            writer.TryComplete(ex);
        }
    }

    /// <summary>
    /// Validates one already-accepted connection (elevation check, then handshake read) independently of the
    /// accept loop and every other in-flight connection's validation, and -- on success -- writes it to
    /// <paramref name="writer"/> so <see cref="AcceptConnectionsAsync"/> can yield it. A connection that fails
    /// validation is logged, disposed, and its <see cref="ConnectionIdleTimeout.OpenConnection"/> count balanced
    /// via <see cref="ConnectionIdleTimeout.CloseConnection"/> -- never allowed to take down the daemon or block
    /// any other connection (accepted before, concurrently, or after it).
    /// </summary>
    private async Task ProcessAcceptedConnectionAsync(
        System.IO.Pipes.NamedPipeServerStream pipeStream,
        ChannelWriter<LanguageServerConnection> writer,
        CancellationToken cancellationToken)
    {
        // CurrentUserOnly (used by both client and server) already guarantees matching identity, but not matching
        // elevation -- without this check an unelevated process running as the same user could derive an elevated
        // daemon's pipe name and submit LSP requests with the daemon's privileges. A client that disconnects
        // immediately after WaitForConnectionAsync succeeds (e.g. it gave up waiting) can make this check itself
        // throw -- treat that the same as any other single-connection failure (log and move on) rather than let
        // it escape and take the whole daemon down with it.
        bool elevationMatches;
        try
        {
            elevationMatches = NamedPipeUtil.CheckClientElevationMatches(pipeStream);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Daemon failed to verify a client connection's elevation; treating it as rejected.");
            await pipeStream.DisposeAsync().ConfigureAwait(false);
            _idleTimeout.CloseConnection();
            return;
        }

        if (!elevationMatches)
        {
            _logger.LogWarning("Daemon rejected a client connection whose elevation did not match the daemon's.");
            await pipeStream.DisposeAsync().ConfigureAwait(false);
            _idleTimeout.CloseConnection();
            return;
        }

        // Read the connecting client's own per-connection configuration before this stream becomes the raw LSP
        // JSON-RPC channel -- see ConnectionHandshake and docs/ide/specs/daemon-per-connection-isolation.md's
        // phase 5. Bounded so a client that connects but never completes the handshake (or an
        // incompatible/garbled one) can't hang this validation forever; treated the same as any other
        // single-connection failure (log and move on), not a reason to take the whole daemon down -- and, unlike
        // the old sequential implementation, this bound no longer needs to also protect *other* clients from
        // being blocked, since this validation runs independently of theirs.
        ConnectionHandshake handshake;
        try
        {
            using var handshakeTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeoutSource.CancelAfter(s_handshakeTimeout);
            handshake = await ConnectionHandshake.ReadAsync(pipeStream, handshakeTimeoutSource.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Daemon failed to read a client connection's handshake; treating it as rejected.");
            await pipeStream.DisposeAsync().ConfigureAwait(false);
            _idleTimeout.CloseConnection();
            return;
        }

        _onConnectionAccepted?.Invoke();
        _logger.LogInformation("Daemon accepted a new client connection.");
        RoslynLog.Logger.Log(RoslynLog.FunctionId.VSCode_LanguageServer_Daemon_Client_Connected, logLevel: RoslynLog.LogLevel.Information);

        // The accepted stream is both input and output, and is disposed when its language server exits.
        var connection = new LanguageServerConnection(pipeStream, pipeStream, new ConnectionResource(pipeStream, this), handshake);

        // Unbounded channel: TryWrite always succeeds (never actually awaits), matching the unbounded capacity.
        writer.TryWrite(connection);
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor
    {
        private readonly NamedPipeDaemonConnectionSource _instance;

        internal TestAccessor(NamedPipeDaemonConnectionSource instance) => _instance = instance;

        internal bool HasTimedOut => _instance._idleTimeout.TimeoutToken.IsCancellationRequested;

        internal void TriggerTimeout() => _instance._idleTimeout.GetTestAccessor().TriggerTimeout();

        /// <summary>
        /// Completes once the accept loop has created its first listening pipe instance, so a test can await this
        /// before connecting a client instead of racing an immediate connect against the loop's background
        /// <see cref="Task"/> not having started yet.
        /// </summary>
        internal Task WaitForAcceptLoopStartedAsync() => _instance._acceptLoopStarted.Task;

        internal Action? OnConnectionAccepted
        {
            set => _instance._onConnectionAccepted = value;
        }
    }

    private sealed class ConnectionResource(IDisposable resource, NamedPipeDaemonConnectionSource source) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                resource.Dispose();
            }
            finally
            {
                source._idleTimeout.CloseConnection();
                RoslynLog.Logger.Log(RoslynLog.FunctionId.VSCode_LanguageServer_Daemon_Client_Disconnected, logLevel: RoslynLog.LogLevel.Information);
            }
        }
    }

    public void Dispose()
    {
        _idleTimeout.Dispose();
        _serverMutex.Dispose();
    }
}
