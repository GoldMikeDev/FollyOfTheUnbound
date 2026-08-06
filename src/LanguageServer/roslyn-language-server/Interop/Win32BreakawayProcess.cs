// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace Microsoft.CodeAnalysis.LanguageServer.Client.Interop;

/// <summary>
/// A process launched by <see cref="Win32BreakawayProcessLauncher.TryStart"/>, exposing just the subset of
/// <see cref="System.Diagnostics.Process"/>'s surface <see cref="Client.ServerExecutable"/>'s callers actually
/// use (see <see cref="ILaunchedProcess"/>), backed by the raw process/pipe handles that launch created instead
/// of by a <see cref="System.Diagnostics.Process"/> instance -- there is no supported way to construct one of
/// those around an existing native handle with its <c>StandardInput</c>/<c>StandardOutput</c>/<c>StandardError</c>
/// populated (those are only set by <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>'s
/// own internal pipe creation).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32BreakawayProcess : ILaunchedProcess
{
    private readonly SafeFileHandle _processHandle;

    /// <summary>
    /// This process's own dedicated job (see the remarks on <see cref="Win32BreakawayProcessLauncher.TryStart"/>
    /// for why breakaway needs one), or <see langword="null"/> if creating/assigning one failed -- in which case
    /// <see cref="Kill"/> falls back to terminating just this one process.
    /// </summary>
    private readonly SafeFileHandle? _jobHandle;

    private bool _disposed;

    internal Win32BreakawayProcess(
        SafeFileHandle processHandle,
        int id,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError,
        SafeFileHandle? jobHandle)
    {
        _processHandle = processHandle;
        _jobHandle = jobHandle;
        Id = id;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int Id { get; }

    public Stream StandardInput { get; }

    public Stream StandardOutput { get; }

    public Stream StandardError { get; }

    public unsafe bool HasExited
    {
        get
        {
            if (!PInvoke.GetExitCodeProcess(_processHandle, out var exitCode))
                return true; // The handle is no longer valid to query; treat that as "exited" rather than throw.

            // STILL_ACTIVE (259) is also a real exit code a process can legitimately return, but that ambiguity
            // is inherent to this API (System.Diagnostics.Process has the exact same caveat) -- WaitForExitAsync
            // below doesn't have this problem, since it waits on the handle itself rather than polling this.
            const uint STILL_ACTIVE = 259;
            return exitCode != STILL_ACTIVE;
        }
    }

    public unsafe int ExitCode
    {
        get
        {
            if (!PInvoke.GetExitCodeProcess(_processHandle, out var exitCode))
                throw new InvalidOperationException("Failed to query the process's exit code.");

            return (int)exitCode;
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        // Wrap the process handle in a WaitHandle so it can be awaited via the thread pool's registered-wait
        // machinery instead of polling -- the same trick .NET itself used for Process.WaitForExit before
        // WaitForExitAsync existed: an event's SafeWaitHandle can be swapped out for any other syncable kernel
        // object, and a process handle becomes signaled exactly when the process exits.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processWaitHandle = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_processHandle.DangerousGetHandle(), ownsHandle: false) };

        RegisteredWaitHandle? registeredWait = null;
        registeredWait = ThreadPool.RegisterWaitForSingleObject(
            processWaitHandle,
            static (state, timedOut) =>
            {
                var (tcs, processWaitHandle) = ((TaskCompletionSource, ManualResetEvent))state!;
                tcs.TrySetResult();
                processWaitHandle.Dispose();
            },
            (tcs, processWaitHandle),
            Timeout.Infinite,
            executeOnlyOnce: true);

        cancellationToken.Register(() =>
        {
            registeredWait?.Unregister(null);
            tcs.TrySetCanceled(cancellationToken);
        });

        return tcs.Task;
    }

    public unsafe void Kill(bool entireProcessTree)
    {
        if (entireProcessTree && _jobHandle is { IsInvalid: false } jobHandle)
        {
            // Terminating the job takes every process assigned to it (this one, plus anything it launched that
            // didn't itself request breakaway) down at once -- the entireProcessTree behavior this method needs
            // to match, without walking a process snapshot by hand.
            PInvoke.TerminateJobObject(jobHandle, uExitCode: 1);
            return;
        }

        PInvoke.TerminateProcess(_processHandle, uExitCode: 1);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _jobHandle?.Dispose();
        _processHandle.Dispose();
    }
}
