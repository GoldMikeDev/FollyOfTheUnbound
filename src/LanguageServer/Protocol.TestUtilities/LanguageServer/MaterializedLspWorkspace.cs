// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.LanguageServer.Protocol;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using LSP = Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Test.Utilities;

internal sealed class MaterializedLspWorkspace
{
    public LspWorkspaceContent Content { get; }
    public string RootPath { get; }
    public Dictionary<string, IList<LSP.Location>> AnnotatedLocations { get; }

    private MaterializedLspWorkspace(
        LspWorkspaceContent content,
        string rootPath,
        Dictionary<string, IList<LSP.Location>> annotatedLocations)
    {
        Content = content;
        RootPath = rootPath;
        AnnotatedLocations = annotatedLocations;
    }

    public static MaterializedLspWorkspace Create(
        TempRoot tempRoot,
        LspWorkspaceContent content,
        CancellationToken cancellationToken)
    {
        var rootPath = tempRoot.CreateDirectory().Path;
        var annotatedLocations = new Dictionary<string, IList<LSP.Location>>();

        foreach (var (relativePath, file) in content.Files)
        {
            var filePath = GetFullPath(rootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(filePath, file.Content);

            if (Path.GetExtension(relativePath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var documentUri = ProtocolConversions.CreateAbsoluteDocumentUri(filePath);
                AddAnnotatedLocations(
                    annotatedLocations,
                    GetAnnotatedLocations(documentUri, SourceText.From(file.Content), file.MarkupSpans));
            }
        }

        if (content.ShouldRestore)
        {
            foreach (var projectPath in content.Files.Keys.Where(static path => PathUtilities.GetExtension(path) == ".csproj"))
                RunRestoreWithTimeout(GetFullPath(rootPath, projectPath));
        }

        return new MaterializedLspWorkspace(content, rootPath, annotatedLocations);
    }

    /// <summary>
    /// Runs `dotnet restore` for a project, bounded by <see cref="TestHelpers.HangMitigatingTimeout"/>.
    /// This intentionally does not use the shared <see cref="ProcessUtilities.Run(string, string, string, System.Collections.Generic.IEnumerable{System.Collections.Generic.KeyValuePair{string, string}}, string, bool)"/>
    /// helper, which blocks on <see cref="Process.WaitForExit()"/> with no timeout at all: a genuinely hung or
    /// slow `dotnet restore` (e.g. a flaky network) would otherwise block this call for the entire test-host
    /// Blame timeout (tens of minutes) rather than failing fast with a clear assertion.
    /// </summary>
    private static void RunRestoreWithTimeout(string projectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"restore --project \"{projectPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo)!;

        // Redirecting stdout/stderr without draining them risks deadlock: if `dotnet restore` writes enough
        // output to fill the OS pipe buffer (e.g. SDK/NuGet warnings), it blocks on the write and never exits,
        // so WaitForExit below would always hit the full timeout even for an otherwise-successful restore.
        // Drain both streams asynchronously, matching the pattern ProcessUtilities.Run uses.
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)TestHelpers.HangMitigatingTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'dotnet restore' for '{projectPath}' did not complete within {TestHelpers.HangMitigatingTimeout}.");
        }
    }

    public string GetFullPath(string relativePath)
        => GetFullPath(RootPath, relativePath);

    private static string GetFullPath(string workspaceRootPath, string relativePath)
        => PathUtilities.CombinePathsUnchecked(workspaceRootPath, relativePath);

    private static Dictionary<string, IList<LSP.Location>> GetAnnotatedLocations(
        DocumentUri codeUri,
        SourceText text,
        IReadOnlyDictionary<string, ImmutableArray<TextSpan>> spanMap)
    {
        var locations = new Dictionary<string, IList<LSP.Location>>();
        foreach (var (name, spans) in spanMap)
        {
            locations[name] =
            [
                .. spans.Select(span => new LSP.Location
                {
                    DocumentUri = codeUri,
                    Range = ProtocolConversions.TextSpanToRange(span, text),
                })
            ];
        }

        return locations;
    }

    private static void AddAnnotatedLocations(
        Dictionary<string, IList<LSP.Location>> locations,
        Dictionary<string, IList<LSP.Location>> locationsToAdd)
    {
        foreach (var (name, newLocations) in locationsToAdd)
        {
            var locationsForName = locations.GetValueOrDefault(name, []);
            locationsForName.AddRange(newLocations);
            locations[name] = [.. locationsForName.Distinct()];
        }
    }
}
