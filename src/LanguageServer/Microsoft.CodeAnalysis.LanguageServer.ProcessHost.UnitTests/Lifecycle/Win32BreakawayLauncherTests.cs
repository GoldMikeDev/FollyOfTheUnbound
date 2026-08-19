// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.LanguageServer.UnitTests;
using Roslyn.Test.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer.ProcessHost.UnitTests;

/// <summary>
/// End-to-end verification for GoldMikeDev/roslyn#11: a process the thin client launches via
/// <c>Win32BreakawayProcessLauncher</c> must survive a real Windows Job Object configured with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> being torn down -- the same thing happens when an editor like VS
/// Code, which runs its own process tree inside such a job, is closed.
/// <para>
/// This drives the thin client's hidden <c>--breakaway-self-test</c> hook (see
/// <c>Interop/BreakawaySelfTest.cs</c>) out of process rather than calling <c>Win32BreakawayProcessLauncher</c>
/// in-process, specifically so the job's <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> termination lands on that
/// helper process instead of on this test host -- terminating a job kills every member, and the test process
/// itself must never be one.
/// </para>
/// <para>
/// Written and compiled on a Linux sandbox with no Windows host available; per <c>[ConditionalFact(typeof(WindowsOnly))]</c>
/// this only ever executes on a Windows test agent. See the issue for why this needs a real pass there before
/// the underlying fix is trusted.
/// </para>
/// </summary>
public sealed class Win32BreakawayLauncherTests
{
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(30);

    [ConditionalFact(typeof(WindowsOnly))]
    public async Task DaemonLaunchBreaksAwayFromKillOnCloseJobObject()
    {
        var thinClientDllPath = TestPaths.GetThinClientPath();
        Assert.True(File.Exists(thinClientDllPath), $"Expected the thin client at '{thinClientDllPath}'.");

        // Launch through "dotnet <dll>" rather than the apphost directly, the same way the other process-host
        // tests' CreateThinClientStartInfo does: the apphost only resolves the .NET runtime via
        // DOTNET_ROOT[_<arch>], a registered install location, or the default install location -- never PATH --
        // so launching it directly would fail to start in an environment whose only .NET install is reachable
        // via PATH. "dotnet" itself has none of that trouble.
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet.exe",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_ROLL_FORWARD_TO_PRERELEASE"] = "1";
        startInfo.ArgumentList.Add(thinClientDllPath);
        startInfo.ArgumentList.Add("--breakaway-self-test");

        using var helper = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the breakaway self-test helper.");

        // From here on, the helper must always be torn down on the way out -- including if an assertion or a
        // Win32 call below throws before the job is even created -- since its own test hook otherwise waits on
        // stdin/blocks forever and would leak, along with anything it manages to launch.
        try
        {
            Assert.Equal("READY", await ReadLineWithTimeoutAsync(helper));

            // A job configured the way an editor like VS Code configures the job around its own process tree:
            // every member goes down the moment the job itself is torn down. JOB_OBJECT_LIMIT_BREAKAWAY_OK (not
            // the silent variant) is also required for CREATE_BREAKAWAY_FROM_JOB to succeed at all -- without it
            // the production launcher's CreateProcess call itself would fail and this test would only be
            // checking that failure path, never the survival behavior the issue is about. Assign the *helper*,
            // not this test process, to the job -- TerminateJobObject below must not take down the test host.
            var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, lpName: null);
            Assert.NotEqual(IntPtr.Zero, job);
            try
            {
                var limitInfo = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | NativeMethods.JOB_OBJECT_LIMIT_BREAKAWAY_OK,
                    },
                };

                Assert.True(NativeMethods.SetInformationJobObject(
                    job,
                    NativeMethods.JobObjectExtendedLimitInformation,
                    ref limitInfo,
                    (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()));

                Assert.True(NativeMethods.AssignProcessToJobObject(job, helper.SafeHandle.DangerousGetHandle()));

                // Only now (after the helper is a job member) tell it to proceed and attempt to break away.
                await helper.StandardInput.WriteLineAsync("GO");
                await helper.StandardInput.FlushAsync();

                Assert.Equal("INJOB:True", await ReadLineWithTimeoutAsync(helper));

                // The regression this guards against: bInheritHandles: true alone inherits *every* inheritable
                // handle open in the helper, not just the three stdio pipes. Confirms the escaped child can't
                // reach the helper's sentinel handle -- proving PROC_THREAD_ATTRIBUTE_HANDLE_LIST is actually
                // scoping inheritance, not just that the child process starts and runs.
                Assert.Equal("SENTINEL:Inaccessible", await ReadLineWithTimeoutAsync(helper));

                var startedLine = await ReadLineWithTimeoutAsync(helper);
                Assert.StartsWith("STARTED:True:", startedLine);
                var childProcessId = int.Parse(startedLine!.Split(':')[2]);

                using var child = Process.GetProcessById(childProcessId);
                try
                {
                    Assert.False(child.HasExited);

                    // Simulate the editor closing: tear down the whole job. If GoldMikeDev/roslyn#11 regresses
                    // (the launch path stops requesting breakaway), the escaped child would still be a member of
                    // this job and would be killed here along with the helper.
                    Assert.True(NativeMethods.TerminateJobObject(job, uExitCode: 1));

                    await helper.WaitForExitAsync().WaitAsync(s_stepTimeout);
                    Assert.True(helper.HasExited);

                    // The actual assertion this issue is about: the escaped child outlives the job it broke away from.
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    child.Refresh();
                    Assert.False(child.HasExited);
                }
                finally
                {
                    if (!child.HasExited)
                        child.Kill(entireProcessTree: true);

                    await child.WaitForExitAsync().WaitAsync(s_stepTimeout);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(job);
            }
        }
        finally
        {
            if (!helper.HasExited)
                helper.Kill(entireProcessTree: true);
        }
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(Process process)
    {
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(s_stepTimeout);
        return line;
    }

    /// <summary>
    /// Minimal, test-only P/Invoke surface for creating and controlling a Windows Job Object -- deliberately not
    /// shared with <c>Win32BreakawayProcessLauncher</c>'s CsWin32-generated bindings, since this test project
    /// only needs a handful of calls and doesn't otherwise take a CsWin32 dependency.
    /// </summary>
    private static class NativeMethods
    {
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        public const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
        public const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(
            IntPtr hJob, int jobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
