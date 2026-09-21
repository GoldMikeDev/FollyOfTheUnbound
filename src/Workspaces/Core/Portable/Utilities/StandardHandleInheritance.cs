// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Microsoft.CodeAnalysis.Shared.Utilities;

/// <summary>
/// Controls whether this process's standard input, output, and error handles are inheritable by child processes.
/// <para>
/// On Windows a <see cref="Process"/> started with any redirected stream is created with
/// <c>CreateProcess(bInheritHandles: true)</c>, which by default leaks <em>all</em> of this process's
/// inheritable handles - in particular its own standard handles - to the child. Marking the standard handles
/// non-inheritable (or, on net11.0+, restricting <see cref="ProcessStartInfo.InheritedHandles"/> to an empty
/// list) prevents the child from receiving them, while the freshly created redirection pipes (which the
/// runtime sets up separately) are unaffected. A no-op off Windows, where redirected children don't leak the
/// parent's standard handles.
/// </para>
/// <para>
/// This is shared across TFMs as low as net8.0 (the MSBuildWorkspace BuildHost, which must stay on the lowest
/// supported SDK), so <see cref="ProcessStartInfo.InheritedHandles"/> - only available net11.0+ - is exposed
/// via <see cref="SuppressHandleInheritance(ProcessStartInfo)"/> guarded behind <c>NET11_0_OR_GREATER</c>.
/// <see cref="WithStandardHandleInheritanceSuppressed(Action)"/> and <see cref="SetStandardHandlesInheritable(bool)"/>
/// use the cross-TFM <c>kernel32.dll</c> P/Invoke fallback unconditionally, for callers (e.g. a raw
/// <c>CreateProcess</c> launch with no <see cref="ProcessStartInfo"/> to attach the net11.0+ API to, or any
/// caller below net11.0) that can't use the newer API.
/// </para>
/// </summary>
internal static class StandardHandleInheritance
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

#if NET11_0_OR_GREATER
    /// <summary>
    /// Restricts <paramref name="startInfo"/> so the launched child inherits none of this process's other
    /// inheritable handles. A no-op off Windows. Only available where this project targets net11.0+.
    /// </summary>
    public static void SuppressHandleInheritance(ProcessStartInfo startInfo)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        startInfo.InheritedHandles = new List<SafeHandle>();
    }
#endif

    /// <summary>
    /// Runs <paramref name="launch"/> (e.g. a raw <c>CreateProcess</c> call that has no
    /// <see cref="ProcessStartInfo"/> to apply <see cref="SuppressHandleInheritance(ProcessStartInfo)"/> to)
    /// with this process's own standard handles temporarily marked non-inheritable. A no-op (aside from
    /// invoking <paramref name="launch"/>) off Windows.
    /// </summary>
    public static void WithStandardHandleInheritanceSuppressed(Action launch)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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

    /// <summary>
    /// On Windows, sets whether this process's standard input, output, and error handles are inheritable by
    /// child processes. Must be paired (set <see langword="false"/> before launching, restore
    /// <see langword="true"/> after). A no-op off Windows.
    /// </summary>
    public static void SetStandardHandlesInheritable(bool inheritable)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

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
