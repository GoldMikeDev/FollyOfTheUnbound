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

        var selfPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine this process's own executable path.");
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(static entry => new KeyValuePair<string, string?>((string)entry.Key, (string?)entry.Value));

        var started = Win32BreakawayProcessLauncher.TryStart(selfPath, [ChildArgument], environment, out var child);
        Console.WriteLine(started && child is not null ? $"STARTED:True:{child.Id}" : "STARTED:False:0");
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
