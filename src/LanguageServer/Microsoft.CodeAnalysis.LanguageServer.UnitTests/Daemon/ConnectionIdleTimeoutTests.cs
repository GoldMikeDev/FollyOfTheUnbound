// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

/// <summary>
/// Regression coverage for a Codex finding on PR #3: <see cref="ConnectionIdleTimeout"/> ultimately calls
/// <see cref="System.Threading.CancellationTokenSource.CancelAfter(TimeSpan)"/>, which throws
/// <see cref="System.ArgumentOutOfRangeException"/> for delays above ~49.7 days
/// (<see cref="uint.MaxValue"/> - 1 milliseconds). Since <c>--daemonKeepAlive</c> and its environment-variable
/// override accept any non-negative integer number of seconds with no upper bound, a large-enough configured
/// keepalive would previously throw uncaught from <see cref="ConnectionIdleTimeout.CloseConnection"/> (inside
/// its lock) the moment the last connection closed, instead of the daemon just exiting after a shorter delay
/// than requested.
/// </summary>
public sealed class ConnectionIdleTimeoutTests
{
    [Fact]
    public void CloseConnection_WithKeepAliveBeyondCancelAfterLimit_DoesNotThrow()
    {
        // ~68 years -- comfortably beyond CancelAfter's ~49.7-day ceiling, and a value a user could plausibly
        // configure via ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE without realizing the practical limit.
        var hugeKeepAlive = TimeSpan.FromSeconds(int.MaxValue);

        using var idleTimeout = new ConnectionIdleTimeout(
            initialConnectionTimeout: TimeSpan.FromMinutes(5),
            keepAlive: hugeKeepAlive,
            logger: NullLogger.Instance);

        idleTimeout.OpenConnection();

        // Starts a fresh keepalive-duration timeout generation via StartTimeout_NoLock -> TimeoutGeneration.StartTimeout
        // -> CancelAfter(hugeKeepAlive); this must not throw.
        var exception = Record.Exception(idleTimeout.CloseConnection);
        Assert.Null(exception);
    }

    [Fact]
    public void CloseConnection_WithNormalKeepAlive_StillTimesOut()
    {
        using var idleTimeout = new ConnectionIdleTimeout(
            initialConnectionTimeout: TimeSpan.FromMinutes(5),
            keepAlive: TimeSpan.FromMilliseconds(1),
            logger: NullLogger.Instance);

        idleTimeout.OpenConnection();
        idleTimeout.CloseConnection();

        var cancelled = SpinWait.SpinUntil(() => idleTimeout.TimeoutToken.IsCancellationRequested, TimeSpan.FromSeconds(10));
        Assert.True(cancelled);
    }

    [Fact]
    public void OpenConnection_WithInfiniteKeepAlive_NeverTimesOutOnClose()
    {
        using var idleTimeout = new ConnectionIdleTimeout(
            initialConnectionTimeout: TimeSpan.FromMinutes(5),
            keepAlive: Timeout.InfiniteTimeSpan,
            logger: NullLogger.Instance);

        idleTimeout.OpenConnection();
        idleTimeout.CloseConnection();

        var cancelled = SpinWait.SpinUntil(() => idleTimeout.TimeoutToken.IsCancellationRequested, TimeSpan.FromMilliseconds(200));
        Assert.False(cancelled);
    }
}
