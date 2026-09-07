// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
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
        if (path is null || selectedItem is null || language is null)
        {
            return (false, null);
        }

        using var _ = LogCreateOperation(hasDotNetProjects, isAtSolutionLevel, language);

        var (success1, fileName) = TryCreateFile(path, hasDotNetProjects, isAtSolutionLevel, language);
        if (!success1 || fileName is null)
        {
            Assert(success1, "Unable to create editorconfig file");
            return (false, null);
        }

        var projectItem = selectedItem.TryAddFileToHierarchy(fileName);
        if (projectItem is null)
        {
            Assert(projectItem is not null, "Unable to add editorconfig file to hierarchy");
            return (false, null);
        }

        return (true, fileName);
    }

    private static IDisposable LogCreateOperation(bool hasDotNetProjects, bool isAtSolutionLevel, string language)
    {
        var operation = GetOperationKind(hasDotNetProjects, isAtSolutionLevel, language);
        return LogOperation(operation);

        static OperationId GetOperationKind(bool isDotnet, bool isAtSolutionLevel, string language)
        {
            return (isDotnet, isAtSolutionLevel, language) switch
            {
                (_, _, LanguageNames.CSharp) => OperationId.CreatingRoslynCSharpFileContent,
                (_, _, LanguageNames.VisualBasic) => OperationId.CreatingRoslynVisualBasicFileContent,
                (false, true, _) => OperationId.CreatingDefaultFileContentIsRoot,
                (false, false, _) => OperationId.CreatingDefaultFileContent,
                (true, true, _) => OperationId.CreatingDotNetFileContentIsRoot,
                (true, false, _) => OperationId.CreatingDotNetFileContent,
            };
        }
    }

    public static (bool success, string? fileName) TryAddFileToFolder(string directory)
    {
        var isDotnet = VSHelpers.IsDotnet(directory);
        LogEvent(EventId.FoundDotnetProjects, isDotnet);
        var language = VSHelpers.GetLanguageFromDirectory(directory);
        if (language is not null)
        {
            LogEvent(EventId.FoundDotnetLanguage, language);
        }
        else
        {
            return (false, null);
        }

        using var _ = LogCreateOperation(isDotnet, true, language);
        var (success, fileName) = TryCreateFile(directory, isDotnet, true, language);
        if (!success)
        {
            Assert(success, "Unable to create editorconfig file");
            return (false, fileName);
        }

        return (true, fileName);
    }

    private static (bool success, string? fileName) TryCreateFile(string projectPath, bool isDotnet, bool isAtSolutionLevel, string language)
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
            return (WriteFile(fileName, isDotnet, isAtSolutionLevel, language), fileName);
        }
    }

    private static bool WriteFile(string fileName, bool isDotnet, bool isAtSolutionLevel, string language)
    {
        var editorconfigFileContents = GetEditorconfigFileContents(isDotnet, isAtSolutionLevel, language);
        if (editorconfigFileContents is null)
        {
            Assert(editorconfigFileContents is not null, "Unable to generate editorconfig file content");
            return false;
        }

        File.WriteAllText(fileName, editorconfigFileContents);
        LogEvent(EventId.FileCreatedSuccessfully);
        return true;

        static string? GetEditorconfigFileContents(bool isDotnet, bool isAtSolutionLevel, string language)
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

            // At the solution level, a mixed-language solution needs settings for both languages;
            // a single language switch on the selected item's language would only ever emit one.
            if (isAtSolutionLevel && VSHelpers.HasCSharpProjects() && VSHelpers.HasVisualBasicProjects())
            {
                var csharpContent = generator.Generate(LanguageNames.CSharp);
                var visualBasicContent = generator.Generate(LanguageNames.VisualBasic);
                if (csharpContent is not null && visualBasicContent is not null)
                {
                    return csharpContent + Environment.NewLine + visualBasicContent;
                }

                return csharpContent ?? visualBasicContent;
            }

            return language switch
            {
                LanguageNames.CSharp => generator.Generate(LanguageNames.CSharp),
                LanguageNames.VisualBasic => generator.Generate(LanguageNames.VisualBasic),
                _ => null
            } ?? isAtSolutionLevel switch
            {
                true => TemplateConstants.DotNetFileContentIsRoot,
                false => TemplateConstants.DotNetFileContent,
            };
        }
    }
}
