// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Shell;
using VSLangProj;
using Constants = EnvDTE.Constants;
using Project = EnvDTE.Project;

namespace Microsoft.VisualStudio.Templates.Editorconfig.Wizard.Utilities;

public static class VSHelpers
{
    private const string SolutionFolder = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

    public static DTE2 DTE { get; } = GetService<DTE, DTE2>();

    public static TReturnType GetService<TServiceType, TReturnType>()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return (TReturnType)ServiceProvider.GlobalProvider.GetService(typeof(TServiceType));
    }

    public static bool IsDotnet()
    {
        return EnumerateAllProjects().Any(p => p.IsKind(PrjKind.prjKindCSharpProject, PrjKind.prjKindVBProject));
    }

    /// <summary>
    /// Enumerates all projects in the solution, recursing through solution folders so that
    /// projects nested under them (rather than only the top-level solution-folder "projects"
    /// EnvDTE exposes directly) are included.
    /// </summary>
    public static System.Collections.Generic.IEnumerable<Project> EnumerateAllProjects()
    {
        foreach (Project project in DTE.Solution.Projects)
        {
            foreach (var flattened in EnumerateProject(project))
            {
                yield return flattened;
            }
        }

        static System.Collections.Generic.IEnumerable<Project> EnumerateProject(Project project)
        {
            if (project.Kind == SolutionFolder)
            {
                if (project.ProjectItems is not null)
                {
                    foreach (ProjectItem item in project.ProjectItems)
                    {
                        if (item.SubProject is Project subProject)
                        {
                            foreach (var flattened in EnumerateProject(subProject))
                            {
                                yield return flattened;
                            }
                        }
                    }
                }
            }
            else
            {
                yield return project;
            }
        }
    }

    /// <summary>
    /// Determines, in a single error-tolerant directory walk, whether <paramref name="directory"/>
    /// contains any C#/VB source files and which language dominates. A single traversal is used so
    /// that large or partially inaccessible folders aren't scanned multiple times (once per language
    /// per caller), and inaccessible subdirectories are skipped instead of aborting the whole scan.
    /// </summary>
    public static (bool isDotnet, string? language) GetDotnetLanguageInfo(string directory)
    {
        var hasCSharp = false;
        var hasVisualBasic = false;

        foreach (var file in EnumerateFilesTolerant(directory))
        {
            if (!hasCSharp && file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                hasCSharp = true;
            }
            else if (!hasVisualBasic && file.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            {
                hasVisualBasic = true;
            }

            if (hasCSharp && hasVisualBasic)
            {
                break;
            }
        }

        var language = hasCSharp ? LanguageNames.CSharp : hasVisualBasic ? LanguageNames.VisualBasic : null;
        return (hasCSharp || hasVisualBasic, language);
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateFilesTolerant(string root)
    {
        var pending = new System.Collections.Generic.Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                files = Array.Empty<string>();
            }

            foreach (var file in files)
            {
                yield return file;
            }

            string[] subDirectories;
            try
            {
                subDirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                subDirectories = Array.Empty<string>();
            }

            foreach (var subDirectory in subDirectories)
            {
                pending.Push(subDirectory);
            }
        }
    }

    public static bool HasCSharpProjects()
    {
        return EnumerateAllProjects().Any(p => p.IsKind(PrjKind.prjKindCSharpProject));
    }

    public static bool HasVisualBasicProjects()
    {
        return EnumerateAllProjects().Any(p => p.IsKind(PrjKind.prjKindVBProject));
    }

    public static (bool isSolutionLevel, string? path, string? language, object? selectedItem) TryGetSelectedItemLanguageAndPath()
    {
        var items = (Array)DTE.ToolWindows.SolutionExplorer.SelectedItems;
        object? selectedItem = null;

        foreach (UIHierarchyItem selItem in items)
        {
            selectedItem = selItem.Object;

            // Check if selected item is the solution
            if (selectedItem is EnvDTE.Solution)
            {
                return (true, Path.GetDirectoryName(DTE.Solution.FullName), HasVisualBasicProjects() ? LanguageNames.VisualBasic : LanguageNames.CSharp, selectedItem);
            }

            if (selectedItem is Project solutionFolder && solutionFolder.Kind == SolutionFolder)
            {
                // The selected item is a solution folder; GetRootFolder() places the .editorconfig
                // next to the solution itself, so this is a solution-level file and needs to go
                // through the mixed-language path in GetEditorconfigFileContents just like picking
                // the solution node directly does.
                var rootFolder = solutionFolder.GetRootFolder();
                return (true, rootFolder, HasVisualBasicProjects() ? LanguageNames.VisualBasic : LanguageNames.CSharp, selectedItem);
            }

            var containingProject = GetVSProject(selectedItem);
            if (containingProject is null)
            {
                return (false, null, null, selectedItem);
            }
            var language = containingProject.GetProjectLanguageName();

            if (selItem.Object is ProjectItem item && item.Properties != null)
            {
                if (item.Kind.Equals(Constants.vsProjectItemKindPhysicalFolder, StringComparison.OrdinalIgnoreCase))
                {
                    // The selected item is a folder; add the .editorconfig file to the folder.
                    var directoryPath = item.Properties.Item("FullPath").Value.ToString();
                    return (false, directoryPath, language, selectedItem);
                }
                else if (item.Kind.Equals(Constants.vsProjectItemKindPhysicalFile, StringComparison.OrdinalIgnoreCase))
                {
                    // The selected item is a file; add the .editorconfig file to the same folder.
                    var directoryPath = Path.GetDirectoryName(item.Properties.Item("FullPath").Value.ToString());
                    return (false, directoryPath, language, selectedItem);
                }
            }
            else if (selItem.Object is Project proj && proj.Kind != SolutionFolder) // solution folder
            {
                // The selected item is a project; add the .editorconfig to the project's root.
                var rootFolder = proj.GetRootFolder();
                return (false, rootFolder, language, selectedItem);
            }
        }

        // Default to solution level
        return (true, Path.GetDirectoryName(DTE.Solution.FullName), HasVisualBasicProjects() ? LanguageNames.VisualBasic : LanguageNames.CSharp, selectedItem);
    }

    public static ProjectItem? TryAddFileToHierarchy(this object item, string fileName)
    {
        if (item is Project proj)
        {
            // Added to project
            return proj.TryAddFileToProject(fileName, "None");
        }
        else if (item is ProjectItem projItem && projItem.ContainingProject != null)
        {
            // Added to folder in project
            return projItem.ContainingProject.TryAddFileToProject(fileName, "None");
        }
        else if (item is Solution2 solution)
        {
            // Added to solution
            return solution.TryAddFileToSolution(fileName);
        }

        return null;
    }

    public static ProjectItem? TryAddFileToProject(this Project project, string file, string? itemType = null)
    {
        if (project.IsKind(ProjectTypes.ASPNET_5, ProjectTypes.SSDT))
        {
            return DTE.Solution.FindProjectItem(file);
        }

        var rootFolder = project.GetRootFolder();
        if (rootFolder is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(rootFolder) || !file.StartsWith(rootFolder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var item = project.ProjectItems.AddFromFile(file);
        if (itemType is not null)
        {
            item.SetItemType(itemType);
        }
        return item;
    }

    public static ProjectItem? TryAddFileToSolution(this Solution2 solution, string fileName)
    {
        Project? currentProject = null;
        foreach (Project project in solution.Projects)
        {
            if (project.Kind == Constants.vsProjectKindSolutionItems && project.Name == "Solution Items")
            {
                currentProject = project;
                break;
            }
        }

        if (currentProject == null)
        {
            currentProject = solution.AddSolutionFolder("Solution Items");
        }

        return currentProject.TryAddFileToProject(fileName, "None");
    }

    public static void OpenFile(string fileName)
    {
        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, fileName);
        var command = DTE.Commands.Item("SolutionExplorer.SyncWithActiveDocument");
        if (command.IsAvailable)
        {
            DTE.Commands.Raise(command.Guid, command.ID, null, null);
        }
        DTE.ActiveDocument.Activate();
    }

    public static string? GetProjectLanguageName(this Project project)
    {
        return project?.Kind switch
        {
            PrjKind.prjKindCSharpProject => LanguageNames.CSharp,
            PrjKind.prjKindVBProject => LanguageNames.VisualBasic,
            _ => null,
        };
    }

    public static Project? GetVSProject(object item)
    {
        if (item is ProjectItem projectItem)
        {
            return projectItem.ContainingProject;
        }
        else if (item is Project proj && proj.Kind != SolutionFolder) // solution folder
        {
            return proj;
        }

        return null;
    }

    public static string? GetRootFolder(this Project project)
    {
        if (project == null)
        {
            return null;
        }

        if (project.IsKind(SolutionFolder)) // solution folder
        {
            return Path.GetDirectoryName(DTE.Solution.FullName);
        }

        if (string.IsNullOrEmpty(project.FullName))
        {
            return null;
        }

        string? fullPath;

        try
        {
            fullPath = project.Properties.Item("FullPath").Value as string;
        }
        catch (ArgumentException)
        {
            try
            {
                // MFC projects don't have FullPath, and there seems to be no way to query existence
                fullPath = project.Properties.Item("ProjectDirectory").Value as string;
            }
            catch (ArgumentException)
            {
                // Installer projects have a ProjectPath.
                fullPath = project.Properties.Item("ProjectPath").Value as string;
            }
        }

        if (string.IsNullOrEmpty(fullPath))
        {
            return File.Exists(project.FullName) ? Path.GetDirectoryName(project.FullName) : null;
        }

        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        if (File.Exists(fullPath))
        {
            return Path.GetDirectoryName(fullPath);
        }

        return null;
    }

    public static bool IsKind(this Project project, params string[] kindGuids)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var guid in kindGuids)
        {
            if (project.Kind.Equals(guid, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void SetItemType(this ProjectItem item, string itemType)
    {
        try
        {
            if (item == null || item.ContainingProject == null || item.Properties == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(itemType) || item.ContainingProject.IsKind(ProjectTypes.WEBSITE_PROJECT, ProjectTypes.UNIVERSAL_APP))
            {
                return;
            }

            item.Properties.Item("ItemType").Value = itemType;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.Write(ex);
        }
    }
}
