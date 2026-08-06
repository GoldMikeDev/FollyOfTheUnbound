// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.LanguageServer.Logging;

/// <summary>
/// Implements the global MEF <see cref="ILogger"/>. When the log call is attributable to a specific, still-live
/// connection (see <see cref="DaemonConnectionContext"/>), routes to just that connection's server; otherwise
/// broadcasts to every active LSP server, or to a fallback logger if none are active yet.
/// </summary>
internal sealed class GlobalLogMessageLogger(
    string categoryName,
    ILoggerFactory fallbackLoggerFactory,
    LanguageServerConnectionManager connectionManager,
    LogConfiguration fallbackLogConfiguration) : ILogger
{
    private readonly Lazy<ILogger> _fallbackLogger = new(() => fallbackLoggerFactory.CreateLogger(categoryName));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => _fallbackLogger.Value.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel)
    {
        var servers = GetTargetServers();
        if (servers.IsEmpty)
        {
            // If there are no started servers, then we should use the fallback logger's log level to determine if logging is enabled.
            return fallbackLogConfiguration.LogLevel <= logLevel;
        }

        foreach (var server in servers)
        {
            try
            {
                if (server.GlobalLogger.IsEnabled(logLevel))
                {
                    return true;
                }
            }
            // Servers can asynchronously start / stop, so by the time we get here a server (and its LSP services) could have been disposed.
            catch (ObjectDisposedException)
            {
            }
        }

        return false;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.None || !IsEnabled(logLevel))
        {
            return;
        }

        var servers = GetTargetServers();
        if (servers.IsEmpty)
        {
            // If there are no started servers, then we should log to the fallback logger.
            _fallbackLogger.Value.Log(logLevel, eventId, state, exception, formatter);
            return;
        }

        foreach (var server in servers)
        {
            try
            {
                server.GlobalLogger.Log(logLevel, eventId, state, exception, formatter);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException)
            {
                // Ignore failures due to a server shutting down while we're trying to log.
                // This is inherently racy, the best we can do is catch and move on.
            }
        }
    }

    /// <summary>
    /// The server(s) this log entry should be delivered to.
    /// <list type="bullet">
    /// <item>No ambient <see cref="AmbientConnectionToken"/> at all: genuine process-wide activity (e.g.
    /// before any client has connected, or single-server/non-daemon mode) -- broadcast to every started server
    /// (the pre-existing behavior), since there's no more specific target to prefer.</item>
    /// <item>Ambient token set and resolves (via <see cref="DaemonConnectionContext.Current"/>) to a live,
    /// still-started server: this call is attributable to that one connection alone -- e.g. a daemon client's
    /// own extension loading -- so route to just that server.</item>
    /// <item>Ambient token set but does not resolve to a live server -- either it hasn't been associated with
    /// one yet (<see cref="LanguageServerConnectionManager"/> internally mints the token and makes it
    /// ambient *before* constructing the <see cref="LanguageServerHost"/>, so logging or a thrown exception
    /// during that construction window, or during handshake/registration processing before
    /// <see cref="DaemonConnectionContext.Associate"/> runs, has an ambient token that doesn't resolve yet), or
    /// its connection has since ended (e.g. a fire-and-forget non-mutating request whose work outlives client
    /// disconnection -- <c>RequestExecutionQueue</c> cancels such work on shutdown without awaiting it): either
    /// way this call is still attributable to that one specific connection, not to process-wide activity, so it
    /// must not be broadcast to *other*, unrelated live clients. Falls through to the fallback logger instead
    /// (an empty target list), the same as the "no servers started yet" case.</item>
    /// </list>
    /// </summary>
    private ImmutableArray<LanguageServerHost> GetTargetServers()
    {
        var servers = connectionManager.GetStartedServers();

        if (AmbientConnectionToken.Current is null)
            return servers;

        return DaemonConnectionContext.Current is { } currentConnection && servers.Contains(currentConnection)
            ? [currentConnection]
            : [];
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(GlobalLogMessageLogger instance)
    {
        internal ImmutableArray<LanguageServerHost> GetTargetServers() => instance.GetTargetServers();
    }
}