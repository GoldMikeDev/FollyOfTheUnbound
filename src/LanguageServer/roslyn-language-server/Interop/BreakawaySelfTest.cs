// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.Versioning;

namespace Microsoft.CodeAnalysis.LanguageServer.Client.Interop;

/// <summary>
/// Hidden CLI hook used only by <c>Win32BreakawayLauncherTests</c> (ProcessHost.UnitTests, Windows-only) to
/// verify GoldMikeDev/roslyn#11 end-to-end against a real Windows Job Object: the test assigns *this* process
/// (deliberately not the test host itself) to a job configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>,
/// signals it to proceed, then terminates the job to simulate an editor closing. A working fix means the
/// process this launches via <see cref="Win32BreakawayProcessLauncher"/> survives that; a regression means it
/// doesn't.
/// <para>
/// Not reachable from any normal thin-client argument parsing path -- <see cref="Program.Main"/> checks for
/// these two markers before <see cref="ThinClientArguments.Parse"/> even runs, the same way it already does for
/// <see cref="DaemonBootstrap.IsBootstrapRequested"/>.
/// </para>
/// </summary>
internal static class BreakawaySelfTest
{
    public const string ParentArgument = "--breakaway-self-test";
    public const string ChildArgument = "--breakaway-self-test-child";

    public static bool IsParentRequested(string[] args) => args.Length > 0 && args[0] == ParentArgument;
    public static bool IsChildRequested(string[] args) => args.Length > 0 && args[0] == ChildArgument;

    /// <summary>
    /// Prints <c>READY</c>, then waits for the harness's <c>GO</c> line on stdin (sent only after the harness has
    /// assigned this process to its test job), reports whether this process is now in a job (<c>INJOB:</c>) and
    /// whether breaking away from it succeeded (<c>STARTED:</c>, with the escaped child's process id), then
    /// blocks indefinitely so it stays a job member until the harness terminates the job.
    /// </summary>
    public static int RunParent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The breakaway self-test only applies on Windows.");
            return 1;
        }

        return RunParentOnWindows();
    }

    [SupportedOSPlatform("windows")]
    private static int RunParentOnWindows()
    {
        Console.WriteLine("READY");
        Console.Out.Flush();
        Console.In.ReadLine();

        var inJob = Win32BreakawayProcessLauncher.IsCurrentProcessInJob();
        Console.WriteLine($"INJOB:{inJob}");
        Console.Out.Flush();

        // Go through ServerExecutable.ResolveSelf().Start -- the same production entry point
        // DaemonBootstrap/Program use to launch the daemon/bootstrap -- rather than calling
        // Win32BreakawayProcessLauncher.TryStart directly, so this test also covers ServerExecutable's own
        // decision of *whether* to attempt breakaway, not just the launcher in isolation.
        try
        {
            var child = ServerExecutable.ResolveSelf().Start([ChildArgument]);

            // Wait for the child's own "I'm alive" line before reporting success: the harness terminates this
            // helper's job the moment it sees STARTED, which closes this process's (and, if breakaway failed,
            // the child's too) end of the child's redirected stdout pipe -- reporting success any earlier could
            // race the child's one write to CHILD-READY with that teardown and turn a working breakaway into a
            // spurious broken-pipe failure.
            using var childOutputReader = new StreamReader(child.StandardOutput);
            var childReady = childOutputReader.ReadLine();
            if (childReady != "CHILD-READY")
                throw new InvalidOperationException($"Expected 'CHILD-READY' from the escaped child, got '{childReady ?? "<eof>"}'.");

            Console.WriteLine($"STARTED:True:{child.Id}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            Console.WriteLine("STARTED:False:0");
            Console.Error.WriteLine(ex);
        }

        Console.Out.Flush();

        // Stay alive (and a job member) until the harness terminates the job; that's the whole point of this
        // helper, so there is no graceful way for this to return on the success path.
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    /// <summary>The breakaway target itself: just proves it's alive, then waits to be killed or cleaned up.</summary>
    public static int RunChild()
    {
        Console.WriteLine("CHILD-READY");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
