// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Composition;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Razor.Settings;
using Microsoft.CodeAnalysis.Razor.Workspaces.Settings;

namespace Microsoft.VisualStudioCode.RazorExtension.Configuration;

/// <summary>
/// This is a <c>[Shared]</c> MEF part -- every daemon connection resolves the same instance (see
/// GoldMikeDev/roslyn#9). <see cref="Update(ClientAdvancedSettings)"/> and its overloads used to mutate one
/// shared <c>_currentSettings</c> field, so a later connection's <c>workspace/didChangeConfiguration</c> could
/// silently change the settings <see cref="GetClientSettings"/> returns for an earlier, unrelated connection's
/// already-open documents. Keyed per <see cref="AmbientConnectionToken.Current"/> instead, the same
/// ambient-context primitive <c>ConnectionScopedOptionOverrides</c> uses on the Roslyn LSP side for the
/// equivalent problem -- including that same facade's fallback: a genuinely connection-less caller (no ambient
/// token at all, e.g. a test driving this type directly rather than through real request dispatch) reads and
/// writes a single shared instance, same as this type's behavior before this fix, rather than having its
/// writes silently dropped.
/// </summary>
/// <remarks>
/// <see cref="ClientSettingsChanged"/> is unchanged and still fires for every subscriber regardless of which
/// connection's settings actually changed -- a lower-severity remaining gap (wasted recomputation and a
/// spurious refresh in unrelated connections, not misrouted data), the same shape already tracked for
/// <c>FeatureProviderRefresher</c> in the linked issue.
/// </remarks>
[Shared]
[Export(typeof(IClientSettingsManager))]
internal class ClientSettingsManager : IClientSettingsManager
{
    private readonly ConditionalWeakTable<object, StrongBox<ClientSettings>> _settingsByConnection = new();
    private readonly StrongBox<ClientSettings> _settingsWithNoAmbientConnection = new(ClientSettings.Default);

    public event EventHandler<EventArgs>? ClientSettingsChanged;

    public ClientSettings GetClientSettings()
        => GetSettingsBox().Value ?? ClientSettings.Default;

    public void Update(ClientAdvancedSettings updateSettings)
        => Update(current => current with { AdvancedSettings = updateSettings });

    public void Update(ClientSpaceSettings updateSettings)
        => Update(current => current with { ClientSpaceSettings = updateSettings });

    public void Update(ClientCompletionSettings updateSettings)
        => Update(current => current with { ClientCompletionSettings = updateSettings });

    private void Update(Func<ClientSettings, ClientSettings> updater)
    {
        var box = GetSettingsBox();
        box.Value = updater(box.Value ?? ClientSettings.Default);

        ClientSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private StrongBox<ClientSettings> GetSettingsBox()
        => AmbientConnectionToken.Current is { } token
            ? _settingsByConnection.GetOrCreateValue(token)
            : _settingsWithNoAmbientConnection;
}
