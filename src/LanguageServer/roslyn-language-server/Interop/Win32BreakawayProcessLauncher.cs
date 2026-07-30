// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Microsoft.CodeAnalysis.LanguageServer.Client.Interop;

/// <summary>
/// Launches a child process with <c>CREATE_BREAKAWAY_FROM_JOB</c> so it escapes any Windows Job Object the
/// current process is a member of (e.g. VS Code's kill-on-close job around its own process tree), while still
/// providing redirected stdin/stdout/stderr pipes the same way <see cref="System.Diagnostics.ProcessStartInfo"/>
/// would. Background: https://github.com/GoldMikeDev/roslyn/issues/11.
/// <para>
/// <see cref="System.Diagnostics.Process"/>/<see cref="System.Diagnostics.ProcessStartInfo"/> has no managed API
/// for <c>CREATE_BREAKAWAY_FROM_JOB</c> -- there is no supported way to inject creation flags into
/// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>'s internal
/// <c>CreateProcess</c> call. This class reimplements just enough of that call (redirected pipes, environment
/// block, command-line quoting) by hand to add the one flag that matters here.
/// </para>
/// <para>
/// This is only attempted when <see cref="IsCurrentProcessInJob"/> is true, and <see cref="TryStart"/> returns
/// <see langword="false"/> on <em>any</em> failure -- including a job that doesn't permit breakaway -- so
/// callers (see <see cref="Client.ServerExecutable"/>) always have a working fallback to the normal
/// <see cref="System.Diagnostics.ProcessStartInfo"/> path.
/// </para>
/// <para>
/// UNVERIFIED: written and compiled on a Linux sandbox with no Windows host available to exercise actual Job
/// Object behavior (CsWin32's source generator runs cross-platform, so this compiles cleanly here, but nothing
/// in this class has executed against a real Windows kill-on-close job). Needs a real pass on Windows before
/// it's trusted -- see the issue for why a verified pass, not a rushed unverified merge, is the plan.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32BreakawayProcessLauncher
{
    /// <summary>
    /// Whether the current process is a member of any Windows Job Object. Breakaway is only meaningful (and
    /// only worth <see cref="TryStart"/>'s extra complexity vs. the normal <see cref="System.Diagnostics.ProcessStartInfo"/>
    /// path) when this is true.
    /// </summary>
    public static unsafe bool IsCurrentProcessInJob()
    {
        // A NULL job handle means "is the process in *any* job" (not a specific one) per IsProcessInJob's own
        // documented behavior.
        BOOL result;
        if (!PInvoke.IsProcessInJob(PInvoke.GetCurrentProcess(), default, &result))
            return false; // Couldn't determine; conservatively fall back to the normal launch path.

        return result;
    }

    /// <summary>
    /// Attempts to start <paramref name="fileName"/> with <paramref name="arguments"/> and
    /// <paramref name="environment"/> (a full replacement environment block, not a delta -- callers should pass
    /// <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> after making their usual adjustments to
    /// it), breaking away from any job the current process belongs to. The new process is also assigned to a
    /// fresh job of its own, purely so <see cref="Win32BreakawayProcess.Kill"/> can still reliably terminate its
    /// full process tree despite having escaped the editor's job (breakaway is otherwise a one-way trip: there
    /// is no supported way to enumerate or kill an escaped process's descendants without it being in some job).
    /// Returns <see langword="false"/> on any failure -- including the current job simply not permitting
    /// breakaway -- so the caller can fall back to <see cref="System.Diagnostics.ProcessStartInfo"/>.
    /// </summary>
    public static unsafe bool TryStart(
        string fileName,
        IReadOnlyList<string> arguments,
        IEnumerable<KeyValuePair<string, string?>> environment,
        out Win32BreakawayProcess? process)
    {
        process = null;

        SafeFileHandle? childStdinRead = null;
        SafeFileHandle? childStdinWrite = null;
        SafeFileHandle? childStdoutRead = null;
        SafeFileHandle? childStdoutWrite = null;
        SafeFileHandle? childStderrRead = null;
        SafeFileHandle? childStderrWrite = null;
        SafeFileHandle? jobHandle = null;
        SafeFileHandle? processHandle = null;
        SafeFileHandle? threadHandle = null;

        try
        {
            var pipeSecurity = new SECURITY_ATTRIBUTES
            {
                nLength = (uint)sizeof(SECURITY_ATTRIBUTES),
                bInheritHandle = true,
            };

            if (!PInvoke.CreatePipe(out childStdinRead, out childStdinWrite, pipeSecurity, 0) ||
                !PInvoke.CreatePipe(out childStdoutRead, out childStdoutWrite, pipeSecurity, 0) ||
                !PInvoke.CreatePipe(out childStderrRead, out childStderrWrite, pipeSecurity, 0))
            {
                return false;
            }

            // Only the ends the child inherits (its stdin-read, stdout-write, stderr-write) should be
            // inheritable; our own ends must not be, the same way DaemonHandleInheritance guards this
            // process's own standard handles around the normal launch path -- otherwise the child (and
            // anything it later launches) would leak copies of our ends too.
            if (!PInvoke.SetHandleInformation(childStdinWrite, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, 0) ||
                !PInvoke.SetHandleInformation(childStdoutRead, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, 0) ||
                !PInvoke.SetHandleInformation(childStderrRead, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, 0))
            {
                return false;
            }

            var commandLineChars = (BuildCommandLine(fileName, arguments) + "\0").ToCharArray();
            var environmentBlock = BuildEnvironmentBlock(environment);

            PROCESS_INFORMATION processInfo;
            fixed (char* environmentBlockPtr = environmentBlock)
            {
                Span<char> commandLineSpan = commandLineChars;
                var startupInfo = new STARTUPINFOW
                {
                    cb = (uint)sizeof(STARTUPINFOW),
                    dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES,
                    hStdInput = new HANDLE((void*)childStdinRead.DangerousGetHandle()),
                    hStdOutput = new HANDLE((void*)childStdoutWrite.DangerousGetHandle()),
                    hStdError = new HANDLE((void*)childStderrWrite.DangerousGetHandle()),
                };

                var created = PInvoke.CreateProcess(
                    fileName,
                    ref commandLineSpan,
                    lpProcessAttributes: null,
                    lpThreadAttributes: null,
                    bInheritHandles: true,
                    // CREATE_SUSPENDED so we can assign the process to its own job (see jobHandle below)
                    // before it runs anything. CREATE_BREAKAWAY_FROM_JOB is the actual point of this class:
                    // escape any job the current process belongs to. CREATE_NO_WINDOW matches
                    // ProcessStartInfo's CreateNoWindow=true. CREATE_UNICODE_ENVIRONMENT because
                    // BuildEnvironmentBlock below produces a Unicode (not ANSI) block.
                    dwCreationFlags:
                        PROCESS_CREATION_FLAGS.CREATE_SUSPENDED
                        | PROCESS_CREATION_FLAGS.CREATE_BREAKAWAY_FROM_JOB
                        | PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW
                        | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT,
                    lpEnvironment: environmentBlockPtr,
                    lpCurrentDirectory: null!,
                    lpStartupInfo: in startupInfo,
                    lpProcessInformation: out processInfo);

                if (!created)
                    return false;
            }

            // PROCESS_INFORMATION's handles are raw HANDLEs, not SafeHandles (CsWin32 can't auto-wrap
            // out-struct fields) -- wrap them ourselves so they're reliably closed. Per CreateProcess's own
            // documented contract, both handles are valid here since `created` was true above.
            processHandle = new SafeFileHandle((nint)processInfo.hProcess.Value, ownsHandle: true);
            threadHandle = new SafeFileHandle((nint)processInfo.hThread.Value, ownsHandle: true);

            // Give the process its own fresh job purely so Win32BreakawayProcess.Kill can still reliably kill
            // its full subtree later -- breakaway from the editor's job doesn't, by itself, leave the process
            // in any job we can enumerate/terminate. If this fails, we still resume the process (breakaway from
            // the editor's job already succeeded above, which is the actual point of this class);
            // Win32BreakawayProcess.Kill falls back to a single-process TerminateProcess in that case.
            jobHandle = PInvoke.CreateJobObject(lpJobAttributes: null, lpName: null!);
            var assignedToOwnJob = jobHandle is { IsInvalid: false } && PInvoke.AssignProcessToJobObject(jobHandle, processHandle);

            PInvoke.ResumeThread(threadHandle);

            process = new Win32BreakawayProcess(
                processHandle,
                (int)processInfo.dwProcessId,
                new FileStream(childStdinWrite, FileAccess.Write),
                new FileStream(childStdoutRead, FileAccess.Read),
                new FileStream(childStderrRead, FileAccess.Read),
                assignedToOwnJob ? jobHandle : null);

            // Ownership of these transferred to `process` (or, for jobHandle, intentionally not disposed when
            // assignedToOwnJob) above; null them out so the finally block below doesn't dispose handles the
            // returned Win32BreakawayProcess (or its streams) now owns, or a job handle we're keeping.
            processHandle = null;
            childStdinWrite = null;
            childStdoutRead = null;
            childStderrRead = null;
            if (assignedToOwnJob)
                jobHandle = null;

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            // The child's ends of the pipes must always be closed in this process once CreateProcess has
            // duplicated them into the child (whether it succeeded or failed) -- otherwise our copy is never
            // released, and e.g. the child would never see EOF on stdin when we later close our write end.
            childStdinRead?.Dispose();
            childStdoutWrite?.Dispose();
            childStderrWrite?.Dispose();

            // Only still non-null here if TryStart didn't hand them off to the returned Win32BreakawayProcess
            // (i.e. we're on a failure path).
            childStdinWrite?.Dispose();
            childStdoutRead?.Dispose();
            childStderrRead?.Dispose();
            jobHandle?.Dispose();
            processHandle?.Dispose();
            threadHandle?.Dispose();
        }
    }

    /// <summary>
    /// Builds a single Win32-quoted command line the way <c>CreateProcess</c>'s <c>lpCommandLine</c> expects,
    /// since unlike <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> there is no array form here.
    /// Uses the same backslash/quote escaping rules .NET's own process-launch internals use (see
    /// "Parsing C++ Command-Line Arguments" on learn.microsoft.com): embedded quotes are backslash-escaped, and
    /// a run of backslashes immediately before a quote (embedded or closing) is doubled.
    /// </summary>
    private static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        AppendQuoted(builder, fileName);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendQuoted(builder, argument);
        }

        return builder.ToString();

        static void AppendQuoted(StringBuilder builder, string value)
        {
            if (value.Length > 0 && value.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
            {
                builder.Append(value);
                return;
            }

            builder.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var backslashCount = 0;
                while (i < value.Length && value[i] == '\\')
                {
                    backslashCount++;
                    i++;
                }

                if (i == value.Length)
                {
                    builder.Append('\\', backslashCount * 2);
                    break;
                }
                else if (value[i] == '"')
                {
                    builder.Append('\\', backslashCount * 2 + 1);
                    builder.Append('"');
                }
                else
                {
                    builder.Append('\\', backslashCount);
                    builder.Append(value[i]);
                }
            }

            builder.Append('"');
        }
    }

    /// <summary>
    /// Builds the double-null-terminated, ordinally-sorted Unicode environment block <c>CREATE_UNICODE_ENVIRONMENT</c>
    /// requires (the same format <c>Environment.GetEnvironmentVariables()</c>-based tooling produces). Entries
    /// with a <see langword="null"/> value are omitted (matching <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>'s
    /// own convention for "explicitly unset").
    /// </summary>
    private static char[] BuildEnvironmentBlock(IEnumerable<KeyValuePair<string, string?>> environment)
    {
        var builder = new StringBuilder();
        foreach (var pair in environment.Where(static kvp => kvp.Value is not null).OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        builder.Append('\0');
        return builder.ToString().ToCharArray();
    }
}
