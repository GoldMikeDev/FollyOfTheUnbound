// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

extern alias MSBuildWorkspaces;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
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

    public async IAsyncEnumerable<LanguageServerConnection> AcceptConnectionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var timeoutToken = _idleTimeout.TimeoutToken;
            var pipeStream = NamedPipeUtil.CreateServer(_pipeName);

            // Wait for a client (outside any 'yield return', which C# disallows inside a try/catch). On success
            // the stream's ownership passes to the yielded connection; on failure we dispose it here.
            try
            {
                using var acceptCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken);
                await pipeStream.WaitForConnectionAsync(acceptCancellationSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
            {
                // The idle timeout and a real client connecting can race: the OS-level connection can complete
                // just before the token's cancellation callback aborts the pending I/O, in which case
                // WaitForConnectionAsync can still surface OperationCanceledException even though the pipe is
                // actually connected now. Don't discard an accepted client on that race -- fall through to treat
                // this the same as a successful connection instead of committing to shutdown.
                if (!pipeStream.IsConnected)
                {
                    await pipeStream.DisposeAsync().ConfigureAwait(false);
                    _idleTimeout.CommitTimeout();
                    yield break;
                }
            }
            catch (Exception ex)
            {
                // Failing to accept one connection shouldn't take down the daemon; log and try again.
                _logger.LogError(ex, "Daemon encountered an error while waiting for a client connection.");
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            // CurrentUserOnly (used by both client and server) already guarantees matching identity, but not
            // matching elevation -- without this check an unelevated process running as the same user could
            // derive an elevated daemon's pipe name and submit LSP requests with the daemon's privileges.
            // A client that disconnects immediately after WaitForConnectionAsync succeeds (e.g. it gave up
            // waiting) can make this check itself throw -- treat that the same as any other single-connection
            // failure (log and move on to the next client) rather than let it escape the loop and take the
            // whole daemon down with it.
            bool elevationMatches;
            try
            {
                elevationMatches = NamedPipeUtil.CheckClientElevationMatches(pipeStream);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _logger.LogWarning(ex, "Daemon failed to verify a client connection's elevation; treating it as rejected.");
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            if (!elevationMatches)
            {
                _logger.LogWarning("Daemon rejected a client connection whose elevation did not match the daemon's.");
                await pipeStream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            // Read the connecting client's own per-connection configuration before this stream becomes the
            // raw LSP JSON-RPC channel -- see ConnectionHandshake and docs/ide/specs/daemon-per-connection-isolation.md's
            // phase 5. Bounded so a client that connects but never completes the handshake (or an
            // incompatible/garbled one) can't hang this accept loop; treated the same as any other
            // single-connection failure (log and move on), not a reason to take the whole daemon down.
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
                continue;
            }

            _onConnectionAccepted?.Invoke();
            _idleTimeout.OpenConnection();
            _logger.LogInformation("Daemon accepted a new client connection.");
            RoslynLog.Logger.Log(RoslynLog.FunctionId.VSCode_LanguageServer_Daemon_Client_Connected, logLevel: RoslynLog.LogLevel.Information);

            // The accepted stream is both input and output, and is disposed when its language server exits.
            yield return new LanguageServerConnection(pipeStream, pipeStream, new ConnectionResource(pipeStream, this), handshake);
        }
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor
    {
        private readonly NamedPipeDaemonConnectionSource _instance;

        internal TestAccessor(NamedPipeDaemonConnectionSource instance) => _instance = instance;

        internal bool HasTimedOut => _instance._idleTimeout.TimeoutToken.IsCancellationRequested;

        internal void TriggerTimeout() => _instance._idleTimeout.GetTestAccessor().TriggerTimeout();

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
