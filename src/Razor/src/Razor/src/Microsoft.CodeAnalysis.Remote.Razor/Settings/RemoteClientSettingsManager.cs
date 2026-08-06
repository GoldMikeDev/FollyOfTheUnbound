// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Composition;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Razor.Settings;
using Microsoft.CodeAnalysis.Razor.Workspaces.Settings;

namespace Microsoft.CodeAnalysis.Remote.Razor;

/// <summary>
/// This is a <c>[Shared]</c> MEF part -- every daemon connection resolves the same instance (see
/// GoldMikeDev/roslyn#9), including in VS Code's "remote" services, which run in-process
/// (<c>InProcServiceFactory</c>) rather than truly out-of-process there. <see cref="Update(ClientSettings)"/>
/// used to mutate one shared <c>_settings</c> field, so a later connection's settings push (relayed here from
/// <c>VSCodeRemoteServicesInitializer</c>'s local <c>ClientSettingsManager.ClientSettingsChanged</c> handler,
/// itself already fixed to be per-connection) could still silently overwrite what remote consumers like
/// <c>CSharpCodeActionProvider</c> observe for an unrelated, earlier connection. Keyed per
/// <see cref="AmbientConnectionToken.Current"/> instead, the same pattern already used for the local-side
/// <c>ClientSettingsManager</c> and <c>ConnectionScopedOptionOverrides</c> on the Roslyn LSP side, including
/// the same fallback to a single shared slot when there's genuinely no ambient connection.
/// </summary>
[Shared]
[Export(typeof(IClientSettingsManager))]
[Export(typeof(RemoteClientSettingsManager))]
internal sealed class RemoteClientSettingsManager : IClientSettingsManager
{
    private readonly ConditionalWeakTable<object, StrongBox<ClientSettings>> _settingsByConnection = new();
    private readonly StrongBox<ClientSettings> _settingsWithNoAmbientConnection = new(ClientSettings.Default);

    public event EventHandler<EventArgs>? ClientSettingsChanged;

    public ClientSettings GetClientSettings() => GetSettingsBox().Value ?? ClientSettings.Default;

    public void Update(ClientSpaceSettings updatedSettings)
    {
        var current = GetClientSettings();
        UpdateSettings(current with { ClientSpaceSettings = updatedSettings });
    }

    public void Update(ClientCompletionSettings updatedSettings)
    {
        var current = GetClientSettings();
        UpdateSettings(current with { ClientCompletionSettings = updatedSettings });
    }

    public void Update(ClientAdvancedSettings updatedSettings)
    {
        var current = GetClientSettings();
        UpdateSettings(current with { AdvancedSettings = updatedSettings });
    }

    internal void Update(ClientSettings settings)
    {
        UpdateSettings(settings);
    }

    private void UpdateSettings(ClientSettings settings)
    {
        var box = GetSettingsBox();
        if (!(box.Value ?? ClientSettings.Default).Equals(settings))
        {
            box.Value = settings;
            ClientSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private StrongBox<ClientSettings> GetSettingsBox()
        => AmbientConnectionToken.Current is { } token
            ? _settingsByConnection.GetOrCreateValue(token)
            : _settingsWithNoAmbientConnection;
}
