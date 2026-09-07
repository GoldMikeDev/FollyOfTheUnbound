// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Templates.Editorconfig.Wizard.Logging.Kinds;
using Microsoft.VisualStudio.Templates.Editorconfig.Wizard.Utilities;
using static Microsoft.VisualStudio.Templates.Editorconfig.Wizard.Logging.Logger;

namespace Microsoft.VisualStudio.Templates.Editorconfig.Wizard.Generator;

public static class EditorConfigFileGenerator
{
    public static (bool success, string? fileName) TryAddFileToSolution(bool? isDotnet = null)
    {
        var hasDotNetProjects = isDotnet ?? VSHelpers.IsDotnet();
        LogEvent(EventId.FoundDotnetProjects, hasDotNetProjects);
        var (isAtSolutionLevel, path, language, selectedItem) = VSHelpers.TryGetSelectedItemLanguageAndPath();
        if (language is not null)
        {
            LogEvent(EventId.FoundDotnetLanguage, language);
        }
        Assert(path is not null && selectedItem is not null, "Unable to get the selected item");
        if (path is null || selectedItem is null)
        {
            return (false, null);
        }

        using var _ = LogCreateOperation(hasDotNetProjects, isAtSolutionLevel, language);

        var hasBothLanguages = isAtSolutionLevel && VSHelpers.HasCSharpProjects() && VSHelpers.HasVisualBasicProjects();
        var (success1, fileName) = TryCreateFile(path, hasDotNetProjects, isAtSolutionLevel, language, hasBothLanguages);
        if (!success1 || fileName is null)
        {
            Assert(success1, "Unable to create editorconfig file");
            return (false, null);
        }

        var projectItem = selectedItem.TryAddFileToHierarchy(fileName);
        if (projectItem is null)
        {
            Assert(projectItem is not null, "Unable to add editorconfig file to hierarchy");

            // The file was already physically written by TryCreateFile; since it couldn't be attached to
            // the hierarchy, remove it instead of leaving an orphaned file that blocks every later attempt
            // via the existing-file check in TryCreateFile.
            TryDeleteFile(fileName);
            return (false, null);
        }

        return (true, fileName);
    }

    private static void TryDeleteFile(string fileName)
    {
        try
        {
            File.Delete(fileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.Write(ex);
        }
    }

    private static IDisposable LogCreateOperation(bool hasDotNetProjects, bool isAtSolutionLevel, string? language)
    {
        var operation = GetOperationKind(hasDotNetProjects, isAtSolutionLevel, language);
        return LogOperation(operation);

        static OperationId GetOperationKind(bool isDotnet, bool isAtSolutionLevel, string? language)
        {
            return (isDotnet, isAtSolutionLevel, language) switch
            {
                (true, _, LanguageNames.CSharp) => OperationId.CreatingRoslynCSharpFileContent,
                (true, _, LanguageNames.VisualBasic) => OperationId.CreatingRoslynVisualBasicFileContent,
                (false, true, _) => OperationId.CreatingDefaultFileContentIsRoot,
                (false, false, _) => OperationId.CreatingDefaultFileContent,
                (true, true, _) => OperationId.CreatingDotNetFileContentIsRoot,
                (true, false, _) => OperationId.CreatingDotNetFileContent,
            };
        }
    }

    public static (bool success, string? fileName) TryAddFileToFolder(string directory)
    {
        var (isDotnet, language, hasBothLanguages) = VSHelpers.GetDotnetLanguageInfo(directory);
        LogEvent(EventId.FoundDotnetProjects, isDotnet);
        if (language is not null)
        {
            LogEvent(EventId.FoundDotnetLanguage, language);
        }

        using var _ = LogCreateOperation(isDotnet, true, language);
        var (success, fileName) = TryCreateFile(directory, isDotnet, true, language, hasBothLanguages);
        if (!success)
        {
            Assert(success, "Unable to create editorconfig file");
            return (false, fileName);
        }

        return (true, fileName);
    }

    private static (bool success, string? fileName) TryCreateFile(string projectPath, bool isDotnet, bool isAtSolutionLevel, string? language, bool hasBothLanguages)
    {
        var fileName = Path.Combine(projectPath, TemplateConstants.FileName);
        if (File.Exists(fileName))
        {
            LogEvent(EventId.CreationFailedFileExists);
            MessageBox.Show(WizardResource.AlreadyExists, ".editorconfig item template", MessageBoxButton.OK, MessageBoxImage.Information);
            return (false, null);
        }
        else
        {
            return (WriteFile(fileName, isDotnet, isAtSolutionLevel, language, hasBothLanguages), fileName);
        }
    }

    private static bool WriteFile(string fileName, bool isDotnet, bool isAtSolutionLevel, string? language, bool hasBothLanguages)
    {
        var editorconfigFileContents = GetEditorconfigFileContents(isDotnet, isAtSolutionLevel, language, hasBothLanguages);
        if (editorconfigFileContents is null)
        {
            Assert(editorconfigFileContents is not null, "Unable to generate editorconfig file content");
            return false;
        }

        File.WriteAllText(fileName, editorconfigFileContents);
        LogEvent(EventId.FileCreatedSuccessfully);
        return true;

        static string? GetEditorconfigFileContents(bool isDotnet, bool isAtSolutionLevel, string? language, bool hasBothLanguages)
        {
            if (!isDotnet)
            {
                return isAtSolutionLevel switch
                {
                    true => TemplateConstants.DefaultFileContentIsRoot,
                    false => TemplateConstants.DefaultFileContent,
                };
            }

            if (language is null)
            {
                return isAtSolutionLevel switch
                {
                    true => TemplateConstants.DotNetFileContentIsRoot,
                    false => TemplateConstants.DotNetFileContent,
                };
            }

            var generator = new RoslynEditorConfigFileGenerator();

            // A mixed-language target (a mixed-language solution, or an open folder containing both
            // C# and VB files) needs settings for both languages; a single language switch on just the
            // selected item's language would only ever emit one. If either language's generation fails
            // (e.g. the IEditorConfigGenerator MEF export is unavailable), fall through to the static
            // template below rather than silently emitting settings for only the other language.
            var generatedContent = hasBothLanguages
                ? CombineIfBothPresent(generator.Generate(LanguageNames.CSharp), generator.Generate(LanguageNames.VisualBasic))
                : language switch
                {
                    LanguageNames.CSharp => generator.Generate(LanguageNames.CSharp),
                    LanguageNames.VisualBasic => generator.Generate(LanguageNames.VisualBasic),
                    _ => null
                };

            if (generatedContent is null)
            {
                return isAtSolutionLevel switch
                {
                    true => TemplateConstants.DotNetFileContentIsRoot,
                    false => TemplateConstants.DotNetFileContent,
                };
            }

            // The underlying generator always prepends a 'root = true' preamble. That's only correct at
            // the solution level; a project/folder-local .editorconfig with 'root = true' would otherwise
            // unexpectedly block every solution- or repository-level .editorconfig above it, unlike the
            // non-root static templates used everywhere else in this non-solution-level path.
            return isAtSolutionLevel ? generatedContent : StripRootPreamble(generatedContent);

            static string? CombineIfBothPresent(string? csharpContent, string? visualBasicContent)
                => csharpContent is not null && visualBasicContent is not null
                    ? csharpContent + Environment.NewLine + visualBasicContent
                    : null;

            static string StripRootPreamble(string content)
            {
                const string RootMarker = "root = true";

                var lines = content.Replace("\r\n", "\n").Split('\n');
                var rootIndex = Array.FindIndex(lines, line => line.Trim() == RootMarker);
                if (rootIndex < 0)
                {
                    return content;
                }

                // Also drop an immediately preceding explanatory comment line, and the blank line
                // that follows the marker, so no empty gap is left at the top of the file.
                var start = rootIndex > 0 && lines[rootIndex - 1].TrimStart().StartsWith("#", StringComparison.Ordinal)
                    ? rootIndex - 1
                    : rootIndex;
                var end = rootIndex + 1;
                if (end < lines.Length && lines[end].Length == 0)
                {
                    end++;
                }

                return string.Join(Environment.NewLine, lines.Take(start).Concat(lines.Skip(end)));
            }
        }
    }
}
