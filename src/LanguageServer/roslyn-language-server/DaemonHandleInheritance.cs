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
}
