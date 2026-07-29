// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Microsoft.CodeAnalysis.LanguageServer.Daemon;

/// <summary>
/// Computes the named-pipe and mutex names used to discover and connect to a shared language
/// server daemon. Only identity/version-compatible clients connect to a given daemon.
/// <para>
/// This file is source-shared (linked) into both the thin client and the language server, so
/// they must remain dependency-light and AOT/trim-safe (no reflection).
/// </para>
/// </summary>
internal static class DaemonPipeName
{
    private const string GlobalMutexPrefix = "Global\\";

    /// <summary>
    /// Restricts daemon mutex access to the current user while keeping the mutex visible across sessions.
    /// </summary>
    public static NamedWaitHandleOptions MutexOptions => new()
    {
        CurrentUserOnly = true,
        CurrentSessionOnly = false,
    };

    /// <summary>
    /// Optional environment variable that, when set, is used verbatim as the daemon pipe name instead of the
    /// value derived from the tool identifier. This lets independent instances run isolated daemons that don't
    /// share state (primarily so end-to-end tests can scope a daemon to a single test, but also usable for
    /// advanced scenarios that deliberately want a separate daemon). Normal clients leave it unset so that only
    /// version-compatible clients share a daemon.
    /// </summary>
    public const string PipeNameOverrideEnvironmentVariable = "ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME";

    /// <summary>
    /// Environment variable that overrides the daemon's keepalive (in seconds) when <c>--daemonKeepAlive</c>
    /// isn't explicitly passed. Defined here (rather than solely in <c>LanguageServerCommandLine</c>, which
    /// resolves it into the effective value) because <see cref="GetPipeName(string, bool, string, IReadOnlyList{string})"/>
    /// needs the same name to fold this setting into the pipe key -- see the remarks there for why.
    /// </summary>
    public const string DaemonKeepAliveEnvironmentVariable = "ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE";

    /// <summary>
    /// The daemon keepalive (in seconds) used when neither <c>--daemonKeepAlive</c> nor
    /// <see cref="DaemonKeepAliveEnvironmentVariable"/> resolve to a valid value. Defined here (rather than
    /// solely in <c>LanguageServerCommandLine</c>) so the pipe-key computation can normalize an unset/invalid
    /// environment value to the same effective setting <c>LanguageServerCommandLine</c> would resolve to.
    /// </summary>
    public const int DefaultDaemonKeepAliveSeconds = 15 * 60;

    /// <summary>
    /// Computes the pipe name for the current user, scoped by <paramref name="toolIdentifier"/> and
    /// <paramref name="serverArguments"/>.
    /// </summary>
    public static string GetPipeName(string toolIdentifier, IReadOnlyList<string> serverArguments)
    {
        // Prefix with identity and elevation so different users / elevation levels don't share a daemon.
        var isAdmin = false;
        var userName = Environment.UserName;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

            // Environment.UserName is only the short account name, which a domain account and a local
            // account can share on the same machine; the SID uniquely identifies the actual account, so
            // prefer it when available.
            userName = identity.User?.Value ?? userName;
        }

        return GetPipeName(userName, isAdmin, toolIdentifier, serverArguments);
    }

    /// <summary>
    /// Computes the pipe name from the user identity, a tool identifier, and the daemon-global startup
    /// arguments. The <paramref name="toolIdentifier"/> ensures only compatible clients connect to a
    /// compatible server; we use the full path to the server executable (in a versioned location).
    /// <paramref name="serverArguments"/> (forwarded verbatim to the daemon on first launch -- e.g.
    /// <c>--extension</c>, <c>--devKitDependencyPath</c>) ensures clients requesting incompatible startup
    /// configuration don't silently share a daemon composed for a different one; since MEF composition
    /// only happens once per daemon, a second client's requested extensions/configuration would otherwise
    /// be silently ignored.
    /// <para>
    /// Also folds in the *effective* <see cref="DaemonKeepAliveEnvironmentVariable"/> setting, even though
    /// it's an environment variable rather than one of <paramref name="serverArguments"/>: unlike most
    /// per-session settings, keepalive genuinely can't be given per-connection semantics (it governs how long
    /// the one shared daemon process lingers after its *last* client disconnects, not any single client's
    /// session), so two clients wanting different keepalives can only be reconciled by giving them separate
    /// daemons, the same trade-off already accepted for incompatible <paramref name="serverArguments"/>. An
    /// explicit <c>--daemonKeepAlive</c> argument already flows through <paramref name="serverArguments"/>
    /// and takes precedence over the environment variable in <c>LanguageServerCommandLine</c>, so when one is
    /// present here the environment variable is ignored entirely for the key too -- clients that already agree
    /// on an explicit keepalive shouldn't be split into separate daemons just because they inherited different,
    /// moot environment values. When there's no explicit argument, the raw environment value is normalized to
    /// the effective seconds it resolves to (matching <c>LanguageServerCommandLine</c>'s own fallback), so an
    /// unset variable, one equal to the default, and an invalid one that also falls back to the default don't
    /// get split into unnecessary separate daemons either.
    /// </para>
    /// </summary>
    public static string GetPipeName(string userName, bool isAdmin, string toolIdentifier, IReadOnlyList<string> serverArguments)
    {
        // Windows paths are case-insensitive. Preserve casing on other platforms, where paths may be
        // case-sensitive and distinct executables must not share a daemon.
        if (OperatingSystem.IsWindows())
            toolIdentifier = toolIdentifier.ToLowerInvariant();

        var effectiveKeepAlive = GetEffectiveKeepAliveForPipeKey(serverArguments);

        // U+0001 can't appear in a parsed command-line argument, so joining with it can't collide
        // across different splits of the same concatenated arguments (e.g. ["--extension", "a b"] vs
        // ["--extension", "a", "b"]).
        var pipeNameInput = $"{userName}.{isAdmin}.{toolIdentifier}.{string.Join('', serverArguments)}.{effectiveKeepAlive}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(pipeNameInput));
        return Convert.ToBase64String(bytes)
            .Replace("/", "_")
            .Replace("=", string.Empty);
    }

    private static string GetEffectiveKeepAliveForPipeKey(IReadOnlyList<string> serverArguments)
    {
        foreach (var argument in serverArguments)
        {
            // System.CommandLine also accepts the inline "--daemonKeepAlive=60" spelling in addition to the
            // two-token "--daemonKeepAlive 60" form; both make the argument explicit and dominate the
            // environment variable in LanguageServerCommandLine, so both must be recognized here too.
            if (argument == "--daemonKeepAlive" || argument.StartsWith("--daemonKeepAlive=", StringComparison.Ordinal))
                return string.Empty;
        }

        var rawValue = Environment.GetEnvironmentVariable(DaemonKeepAliveEnvironmentVariable);
        var effectiveSeconds = int.TryParse(rawValue, out var value) && value >= -1
            ? value
            : DefaultDaemonKeepAliveSeconds;

        return effectiveSeconds.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Name of the mutex held by the daemon for its entire lifetime; its existence means a daemon
    /// is running for this pipe.
    /// </summary>
    public static string GetServerMutexName(string pipeName)
        => $"{GlobalMutexPrefix}{pipeName}.server";

    /// <summary>
    /// Name of the mutex briefly acquired by a connecting client to serialize the
    /// check-server-then-launch sequence so two clients can't race to start two daemons.
    /// </summary>
    public static string GetClientMutexName(string pipeName)
        => $"{GlobalMutexPrefix}{pipeName}.client";
}
