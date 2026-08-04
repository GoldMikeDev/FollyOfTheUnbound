// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// Options whose value is now routed per-connection via <c>ConnectionHandshake</c> (see
    /// docs/ide/specs/daemon-per-connection-isolation.md's phases 5 and 7) rather than baked into the daemon's
    /// one-time MEF composition or global option state. Excluded from <c>GetPipeName</c>'s hash input:
    /// clients that differ only in one of these no longer have a reason to be split into separate daemons --
    /// unlike the rest of <c>serverArguments</c>, a second client's value for one of these is no longer
    /// silently ignored, it's genuinely applied to that connection specifically.
    /// </summary>
    private static readonly string[] s_perConnectionRoutedOptions = ["--extensionLogDirectory", "--sourceGeneratorExecutionPreference"];

    /// <summary>
    /// Options excluded from <c>GetPipeName</c>'s hash input for a different reason than
    /// <see cref="s_perConnectionRoutedOptions"/>: these aren't routed per-connection at all, they're simply
    /// irrelevant to whether two clients can share a daemon. <c>--sessionId</c> only initializes the daemon
    /// process's telemetry singleton once at startup; the per-connection-isolation design already accepts
    /// attributing telemetry to whichever client happened to launch the shared daemon (see
    /// docs/ide/specs/daemon-per-connection-isolation.md's phase 6 notes) rather than isolating it. Hashing
    /// this normally session-specific value would silently defeat daemon sharing for every client -- each
    /// session gets its own daemon -- without buying any actual isolation in return.
    /// </summary>
    private static readonly string[] s_pipeKeyIrrelevantOptions = ["--sessionId"];

    /// <summary>
    /// Path-valued <c>serverArguments</c> options whose values are resolved relative to the launching client's
    /// working directory downstream (<c>--extension</c> by <c>ExtensionAssemblyManager</c>,
    /// <c>--devKitDependencyPath</c>/<c>--csharpDesignTimePath</c> similarly) but only ever get *one* client's
    /// working directory to resolve against -- the daemon process's own, inherited from whichever client
    /// happened to launch it. Two clients launched from different working directories with the same relative
    /// argument (e.g. both pass <c>--extension foo.dll</c>) would otherwise hash to the identical pipe key and
    /// share one daemon, silently resolving the second client's path using the first client's directory instead
    /// of its own. Canonicalized to an absolute path (relative to *this* process's own working directory, i.e.
    /// the client currently computing the key) before folding into the hash, so two such clients get distinct
    /// keys -- and therefore separate daemons -- whenever their relative arguments actually resolve to different
    /// files.
    /// </summary>
    private static readonly string[] s_pathValuedOptions = ["--extension", "--devKitDependencyPath", "--csharpDesignTimePath"];

    /// <summary>
    /// Environment variables folded into <c>GetPipeName</c>'s hash input alongside <c>PATH</c> because
    /// <c>DotnetCliHelper.Run</c> inherits the daemon process's own environment into every dotnet CLI
    /// invocation it makes on behalf of any connection, regardless of which client is currently connected --
    /// see the remarks on <see cref="GetPipeName(string, bool, string, IReadOnlyList{string})"/>.
    /// <c>DOTNET_HOST_PATH</c>/<c>DOTNET_EXPERIMENTAL_HOST_PATH</c> are folded in for a related but distinct
    /// reason: <c>RuntimeHostInfo.GetToolDotNetRoot</c> (via <c>GetDotNetPathOrDefault</c>) prioritizes those two
    /// over scanning <c>PATH</c>, and <c>ServerExecutable.Start</c> uses the result to set the bundled server
    /// process's own <c>DOTNET_ROOT</c> -- a daemon-launch-time decision made once, from whichever client
    /// happened to launch it. Two clients that agree on <c>PATH</c> but differ in either of these would
    /// otherwise silently share a daemon running on the wrong client's selected .NET installation, which can
    /// change runtime roll-forward and dependency resolution behavior.
    /// </summary>
    private static readonly string[] s_dotnetEnvironmentVariablesForPipeKey =
        ["PATH", "NUGET_PACKAGES", "DOTNET_CLI_HOME", "MSBuildSDKsPath", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_HOST_PATH", "DOTNET_EXPERIMENTAL_HOST_PATH"];

    /// <summary>
    /// Computes the pipe name from the user identity, a tool identifier, and the daemon-global startup
    /// arguments. The <paramref name="toolIdentifier"/> ensures only compatible clients connect to a
    /// compatible server; we use the full path to the server executable (in a versioned location).
    /// <paramref name="serverArguments"/> (forwarded verbatim to the daemon on first launch -- e.g.
    /// <c>--extension</c>, <c>--devKitDependencyPath</c>) ensures clients requesting incompatible startup
    /// configuration don't silently share a daemon composed for a different one; since MEF composition
    /// only happens once per daemon, a second client's requested extensions/configuration would otherwise
    /// be silently ignored. <see cref="s_perConnectionRoutedOptions"/> are excluded from this, since that
    /// reasoning no longer applies to them.
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
    /// unset variable and one equal to the default don't get split into unnecessary separate daemons either --
    /// but an out-of-range value (which <c>LanguageServerCommandLine</c> rejects outright, refusing to launch a
    /// daemon over it) is deliberately kept distinct rather than also collapsed to the default, so a client
    /// with an invalid setting can't silently reuse an already-running default-keyed daemon and skip that
    /// validation depending on what else happens to be running.
    /// </para>
    /// </summary>
    public static string GetPipeName(string userName, bool isAdmin, string toolIdentifier, IReadOnlyList<string> serverArguments)
    {
        // Windows paths are case-insensitive. Preserve casing on other platforms, where paths may be
        // case-sensitive and distinct executables must not share a daemon.
        if (OperatingSystem.IsWindows())
            toolIdentifier = toolIdentifier.ToLowerInvariant();

        var effectiveKeepAlive = GetEffectiveKeepAliveForPipeKey(serverArguments);
        var keyRelevantArguments = GetServerArgumentsForPipeKey(serverArguments);

        // Like keepalive, this can't be given per-connection semantics: DotnetCliHelper.Run inherits the
        // daemon process's own environment into every dotnet CLI invocation (restore/build/test) it makes,
        // regardless of which connection asked for it -- so two clients with different values for
        // PATH (which dotnet/SDK gets resolved) or the other variables below (where NuGet reads/writes
        // packages, and the CLI's own home/telemetry/profile directory) would otherwise silently share a
        // daemon that only ever uses whichever client happened to launch it. Folding them into the key gives
        // such clients separate daemons instead, the same trade-off already accepted for incompatible
        // serverArguments and keepalive.
        // This list is best-effort, not exhaustive -- covers PATH plus the NuGet/CLI-home variables most
        // likely to actually change dotnet CLI behavior across restores/builds/tests, not every environment
        // variable that could conceivably affect it (e.g. proxy settings). Extend it if another one is found
        // to matter in practice.
        var effectiveDotnetEnvironment = string.Join('', s_dotnetEnvironmentVariablesForPipeKey
            .Select(static name => $"{name}={Environment.GetEnvironmentVariable(name)}"));

        // U+0001 can't appear in a parsed command-line argument, so joining with it can't collide
        // across different splits of the same concatenated arguments (e.g. ["--extension", "a b"] vs
        // ["--extension", "a", "b"]).
        var pipeNameInput = $"{userName}.{isAdmin}.{toolIdentifier}.{string.Join('', keyRelevantArguments)}.{effectiveKeepAlive}.{effectiveDotnetEnvironment}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(pipeNameInput));
        return Convert.ToBase64String(bytes)
            .Replace("/", "_")
            .Replace("=", string.Empty);
    }

    /// <summary>
    /// Filters <paramref name="serverArguments"/> down to the subset that should still distinguish which
    /// daemon a client connects to, dropping any <see cref="s_perConnectionRoutedOptions"/> or
    /// <see cref="s_pipeKeyIrrelevantOptions"/> occurrence (and its value, whether given as a separate token or
    /// inline <c>--option=value</c>/<c>--option:value</c>) since neither category needs to split clients into
    /// separate daemons.
    /// </summary>
    private static IEnumerable<string> GetServerArgumentsForPipeKey(IReadOnlyList<string> serverArguments)
    {
        for (var i = 0; i < serverArguments.Count; i++)
        {
            var argument = serverArguments[i];
            var matchedOption =
                Array.Find(s_perConnectionRoutedOptions, option => IsOptionOrInlineValue(argument, option)) ??
                Array.Find(s_pipeKeyIrrelevantOptions, option => IsOptionOrInlineValue(argument, option));

            if (matchedOption is not null)
            {
                // Only the two-token form ("--option value") has a separate value token to also skip; the inline
                // form ("--option=value") is entirely contained in the one token already excluded above.
                if (argument == matchedOption && i + 1 < serverArguments.Count)
                    i++;

                continue;
            }

            var pathOption = Array.Find(s_pathValuedOptions, option => IsOptionOrInlineValue(argument, option));
            if (pathOption is not null)
            {
                if (argument != pathOption)
                {
                    // Inline "--option=value" form: canonicalize just the value portion.
                    yield return $"{pathOption}={CanonicalizePathValue(argument[(pathOption.Length + 1)..])}";
                    continue;
                }

                yield return argument;

                // Two-token form. --extension takes one-or-more following values (its arity); the single-value
                // path options take exactly one. Canonicalize every value token that follows, up to the next
                // option-looking ("--"-prefixed) token or the end of the arguments.
                while (i + 1 < serverArguments.Count && !serverArguments[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    i++;
                    yield return CanonicalizePathValue(serverArguments[i]);
                }

                continue;
            }

            yield return argument;
        }

        static bool IsOptionOrInlineValue(string argument, string optionName)
            => argument == optionName ||
               argument.StartsWith(optionName + "=", StringComparison.Ordinal) ||
               argument.StartsWith(optionName + ":", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves <paramref name="value"/> to an absolute path relative to this process's own working directory,
    /// falling back to the raw value if it isn't a valid path (e.g. empty, or contains characters invalid for
    /// the current platform) -- the pipe key just needs a value that's stable and distinguishes genuinely
    /// different paths, not a guarantee the path exists or is well-formed.
    /// </summary>
    private static string CanonicalizePathValue(string value)
    {
        try
        {
            return System.IO.Path.GetFullPath(value);
        }
        catch (ArgumentException)
        {
            return value;
        }
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
        if (!int.TryParse(rawValue, out var value))
            return DefaultDaemonKeepAliveSeconds.ToString(CultureInfo.InvariantCulture);

        if (value >= -1)
            return value.ToString(CultureInfo.InvariantCulture);

        // An out-of-range value (< -1) is invalid -- LanguageServerCommandLine.AddError rejects it and refuses
        // to launch a daemon over it, falling back to DefaultDaemonKeepAliveSeconds only as the *reported*
        // value for that failed parse, not as something that should actually take effect. Collapsing it to
        // the same pipe key as a genuinely-valid default here would let a client with this invalid setting
        // silently reuse an already-running default-keyed daemon (skipping validation entirely) instead of
        // consistently hitting the same launch failure a fresh daemon start would -- whether it fails or
        // "succeeds" would depend on pure happenstance of what else is running. Keep it distinct instead (its
        // own literal, invalid value, which no valid setting can ever collide with) so this client is always
        // routed to the same never-created daemon and always hits that same launch failure.
        return $"invalid:{rawValue}";
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
