// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Composition;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Razor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Razor.Workspaces;

namespace Microsoft.VisualStudioCode.RazorExtension.Services;

/// <summary>
/// This is a <c>[Shared]</c> MEF part -- every daemon connection resolves the same instance (see
/// GoldMikeDev/roslyn#9). <see cref="SetWorkspace(Workspace)"/> used to mutate one shared <c>_workspace</c>
/// field, so a later connection's startup could silently repoint an earlier, unrelated connection's
/// <see cref="GetWorkspace"/> callers at the wrong <see cref="Workspace"/>. Keyed per
/// <see cref="AmbientConnectionToken.Current"/> instead, same pattern as <c>ClientSettingsManager</c>.
/// </summary>
[Shared]
[Export(typeof(IWorkspaceProvider))]
[Export(typeof(VSCodeWorkspaceProvider))]
internal sealed class VSCodeWorkspaceProvider : IWorkspaceProvider
{
    private readonly ConditionalWeakTable<object, StrongBox<Workspace?>> _workspaceByConnection = new();
    private readonly StrongBox<Workspace?> _workspaceWithNoAmbientConnection = new(null);

    public void SetWorkspace(Workspace workspace)
    {
        GetWorkspaceBox().Value = workspace;
    }

    public Workspace GetWorkspace()
    {
        return GetWorkspaceBox().Value ?? Assumed.Unreachable<Workspace>("Accessing the workspace before it has been provided");
    }

    private StrongBox<Workspace?> GetWorkspaceBox()
        => AmbientConnectionToken.Current is { } token
            ? _workspaceByConnection.GetOrCreateValue(token)
            : _workspaceWithNoAmbientConnection;
}
