// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Microsoft.CodeAnalysis.LanguageServer.Client;

/// <summary>
/// Restricts a child process launch to inherit none of this process's inheritable handles. Used around both
/// stages of the daemon double launch (this thin client launching the bootstrap, and the bootstrap launching
/// the daemon).
/// <para>
/// On Windows a <see cref="Process"/> started with any redirected stream is created with
/// <c>CreateProcess(bInheritHandles: true)</c>, which by default leaks <em>all</em> of this process's
/// inheritable handles - in particular its own standard handles - to the child. In the daemon launch chain
/// (thin client → bootstrap → daemon) those standard handles are the editor's LSP stdio pipes; if the
/// long-lived daemon inherits copies of them it holds them open after this process exits (so the editor's
/// <c>WaitForExit</c>/output draining never sees EOF) and, in stdio mode, corrupts the editor's LSP channel.
/// Setting <see cref="ProcessStartInfo.InheritedHandles"/> to an empty list restricts the child to that
/// explicit (empty) set instead of the default "inherit everything", while the freshly created redirection
/// pipes (which the runtime sets up separately) are unaffected. A no-op off Windows, where redirected
/// children don't leak the parent's other handles.
/// </para>
/// </summary>
internal static class DaemonHandleInheritance
{
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;

    private static readonly IntPtr s_invalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    /// <summary>
    /// Restricts <paramref name="startInfo"/> so the launched child inherits none of this process's other
    /// inheritable handles. A no-op off Windows.
    /// </summary>
    public static void SuppressHandleInheritance(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsWindows())
            return;

        startInfo.InheritedHandles = new List<SafeHandle>();
    }

    /// <summary>
    /// Runs <paramref name="launch"/> (a raw <c>CreateProcess</c> call, e.g. <see cref="Interop.Win32BreakawayProcessLauncher.TryStart"/>,
    /// that has no <see cref="ProcessStartInfo"/> to apply <see cref="SuppressHandleInheritance"/> to) with this
    /// process's own standard handles temporarily marked non-inheritable, so it doesn't leak them the same way
    /// <see cref="SuppressHandleInheritance"/> prevents for the normal <see cref="Process.Start(ProcessStartInfo)"/>
    /// path. A no-op (aside from invoking <paramref name="launch"/>) off Windows.
    /// </summary>
    public static void WithStandardHandleInheritanceSuppressed(Action launch)
    {
        if (!OperatingSystem.IsWindows())
        {
            launch();
            return;
        }

        SetStandardHandlesInheritable(false);
        try
        {
            launch();
        }
        finally
        {
            SetStandardHandlesInheritable(true);
        }
    }

    private static void SetStandardHandlesInheritable(bool inheritable)
    {
        var flags = inheritable ? HANDLE_FLAG_INHERIT : 0u;
        SetInheritable(STD_INPUT_HANDLE, flags);
        SetInheritable(STD_OUTPUT_HANDLE, flags);
        SetInheritable(STD_ERROR_HANDLE, flags);

        static void SetInheritable(int stdHandle, uint flags)
        {
            var handle = GetStdHandle(stdHandle);
            if (handle != IntPtr.Zero && handle != s_invalidHandleValue)
                _ = SetHandleInformation(handle, HANDLE_FLAG_INHERIT, flags);
        }
    }
}
