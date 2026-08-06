// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.LanguageServer.Client.Interop;

/// <summary>
/// The subset of <see cref="System.Diagnostics.Process"/>'s surface <see cref="Client.ServerExecutable"/>'s
/// callers actually use, abstracted so <see cref="Client.ServerExecutable"/> can return either a normal
/// <see cref="ManagedLaunchedProcess"/> (wrapping <see cref="System.Diagnostics.Process"/>, used everywhere
/// except the Windows Job Object breakaway path) or a <see cref="Win32BreakawayProcess"/> (its raw-handle
/// replacement, see https://github.com/GoldMikeDev/roslyn/issues/11), interchangeably.
/// </summary>
internal interface ILaunchedProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    /// <summary>The child's standard input, from this process's point of view (writing here is writing to the child's stdin).</summary>
    Stream StandardInput { get; }

    /// <summary>The child's standard output, from this process's point of view (reading here reads the child's stdout).</summary>
    Stream StandardOutput { get; }

    /// <summary>The child's standard error, from this process's point of view (reading here reads the child's stderr).</summary>
    Stream StandardError { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken = default);

    void Kill(bool entireProcessTree);
}
