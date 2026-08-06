// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Tracks accepted connections and cancels the current <see cref="TimeoutToken"/> when the initial-connection
/// timeout or keepalive elapses with no active connections. Timeout cancellation is tentative until
/// <see cref="TryCommitTimeout"/> is called: a concurrently accepted connection can still open and advance to a fresh
/// timeout generation.
/// </summary>
internal sealed class ConnectionIdleTimeout : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _keepAlive;
    private readonly ILogger _logger;
    private TimeoutGeneration? _currentGeneration;

    private int _activeConnections;
    private bool _stopped;

    public ConnectionIdleTimeout(TimeSpan initialConnectionTimeout, TimeSpan keepAlive, ILogger logger)
    {
        _keepAlive = keepAlive;
        _logger = logger;
        _currentGeneration = new TimeoutGeneration(initialConnectionTimeout, isInitialConnectionTimeout: true);
        StartTimeout_NoLock();
    }

    /// <summary>
    /// Cancelled when the current idle timeout elapses. A successfully accepted connection advances this to a fresh
    /// token, even if the previous token was cancelled concurrently.
    /// </summary>
    public CancellationToken TimeoutToken
    {
        get
        {
            lock (_gate)
            {
                Debug.Assert(_currentGeneration is not null);
                return _currentGeneration.Token;
            }
        }
    }

    /// <summary>
    /// Records an accepted connection, cancels the current idle delay, and advances to a fresh timeout generation.
    /// An accepted connection wins even if the previous generation's timeout elapsed concurrently.
    /// </summary>
    public void OpenConnection()
    {
        TimeoutGeneration previousGeneration;
        lock (_gate)
        {
            Debug.Assert(!_stopped);
            Debug.Assert(_currentGeneration is not null);

            previousGeneration = _currentGeneration;
            _currentGeneration = new TimeoutGeneration(_keepAlive, isInitialConnectionTimeout: false);
            _activeConnections++;
        }

        previousGeneration.Dispose();
    }

    /// <summary>
    /// Attempts to commit shutdown after a pipe wait observes timeout cancellation. With more than one listening
    /// pipe instance possibly outstanding at once (see <see cref="NamedPipeDaemonConnectionSource"/>'s
    /// "stay one accept ahead" background accept), the specific wait that observed cancellation might be a
    /// superseded/stale one -- its generation could have been cancelled (e.g. by <see cref="TestAccessor.TriggerTimeout"/>)
    /// while a *different*, concurrently-processing connection is still active, or a fresh connection could have
    /// opened in between. Committing in that case would violate the invariant that shutdown only happens with zero
    /// active connections. Returns <see langword="false"/> (does nothing) rather than committing when that's not
    /// actually true right now; the caller should treat that the same as a spurious cancellation and keep accepting.
    /// </summary>
    public bool TryCommitTimeout()
    {
        TimeoutGeneration generation;
        bool isInitialConnectionTimeout;
        lock (_gate)
        {
            if (_stopped)
                return false;

            Debug.Assert(_currentGeneration is not null);
            if (_activeConnections != 0 || !_currentGeneration.Token.IsCancellationRequested)
                return false;

            _stopped = true;
            generation = _currentGeneration;
            _currentGeneration = null;
            isInitialConnectionTimeout = generation.IsInitialConnectionTimeout;
        }

        generation.Dispose();
        _logger.LogInformation(
            isInitialConnectionTimeout
                ? "Initial connection timeout elapsed; shutting down."
                : "Keepalive elapsed with no active connections; shutting down.");
        return true;
    }

    /// <summary>
    /// Records that an accepted connection has finished. The keepalive starts when the last connection finishes.
    /// </summary>
    public void CloseConnection()
    {
        lock (_gate)
        {
            Debug.Assert(_activeConnections > 0);
            _activeConnections--;

            if (_activeConnections == 0 && !_stopped)
                StartTimeout_NoLock();
        }
    }

    public void Dispose()
    {
        TimeoutGeneration? generation;
        lock (_gate)
        {
            if (_stopped)
                return;

            _stopped = true;
            generation = _currentGeneration;
            _currentGeneration = null;
        }

        generation?.Dispose();
    }

    private void StartTimeout_NoLock()
    {
        Debug.Assert(_activeConnections == 0);
        Debug.Assert(!_stopped);
        var generation = _currentGeneration;
        Debug.Assert(generation is not null);

        generation.StartTimeout();
    }

    internal TestAccessor GetTestAccessor() => new(this);

    private sealed class TimeoutGeneration : IDisposable
    {
        // CancellationTokenSource.CancelAfter rejects delays above this (~49.7 days); a keepalive configured
        // beyond that (there's no upper bound on --daemonKeepAlive or its environment variable, only -1/>=0)
        // would otherwise throw ArgumentOutOfRangeException here -- inside CloseConnection's lock, uncaught --
        // instead of just running for a shorter time than requested.
        private static readonly TimeSpan s_maxCancelAfterDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        private readonly CancellationTokenSource _cancellationSource = new();
        private readonly TimeSpan _timeout;

        public TimeoutGeneration(TimeSpan timeout, bool isInitialConnectionTimeout)
        {
            _timeout = timeout;
            Token = _cancellationSource.Token;
            IsInitialConnectionTimeout = isInitialConnectionTimeout;
        }

        public CancellationToken Token { get; }
        public bool IsInitialConnectionTimeout { get; }

        public void StartTimeout()
            => _cancellationSource.CancelAfter(_timeout > s_maxCancelAfterDelay ? s_maxCancelAfterDelay : _timeout);

        public void Cancel()
            => _cancellationSource.Cancel();

        public void Dispose()
            => _cancellationSource.Dispose();
    }

    internal readonly struct TestAccessor
    {
        private readonly ConnectionIdleTimeout _instance;

        internal TestAccessor(ConnectionIdleTimeout instance)
            => _instance = instance;

        internal void TriggerTimeout()
        {
            TimeoutGeneration generation;
            lock (_instance._gate)
            {
                Debug.Assert(_instance._currentGeneration is not null);
                generation = _instance._currentGeneration;
            }

            generation.Cancel();
        }
    }
}
