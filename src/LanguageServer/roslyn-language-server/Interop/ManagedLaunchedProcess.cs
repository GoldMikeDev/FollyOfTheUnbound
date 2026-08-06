// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace Microsoft.CodeAnalysis.LanguageServer.Client.Interop;

/// <summary>Adapts a normal <see cref="Process"/> (started via <see cref="ProcessStartInfo"/>) to <see cref="ILaunchedProcess"/>.</summary>
internal sealed class ManagedLaunchedProcess(Process process) : ILaunchedProcess
{
    public int Id => process.Id;

    public bool HasExited => process.HasExited;

    public int ExitCode => process.ExitCode;

    public Stream StandardInput => process.StandardInput.BaseStream;

    public Stream StandardOutput => process.StandardOutput.BaseStream;

    public Stream StandardError => process.StandardError.BaseStream;

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => process.WaitForExitAsync(cancellationToken);

    public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);

    public void Dispose() => process.Dispose();
}
