// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.LanguageServer;

namespace Microsoft.CodeAnalysis.Razor.Protocol;

/// <summary>
/// Subclasses of this type are resolved from a <c>[Shared]</c>-composition MEF part shared across every daemon
/// connection (see GoldMikeDev/roslyn#9). <see cref="SetCapabilities(VSInternalClientCapabilities)"/> used to
/// mutate one shared <c>_clientCapabilities</c> field, so a later connection's LSP <c>initialize</c> could
/// silently overwrite the capabilities an earlier, unrelated connection's <see cref="ClientCapabilities"/>
/// callers observe. Keyed per <see cref="AmbientConnectionToken.Current"/> instead, same pattern as
/// <c>ClientSettingsManager</c>.
/// </summary>
internal abstract class AbstractClientCapabilitiesService : IClientCapabilitiesService
{
    private readonly ConditionalWeakTable<object, StrongBox<VSInternalClientCapabilities?>> _capabilitiesByConnection = new();
    private readonly StrongBox<VSInternalClientCapabilities?> _capabilitiesWithNoAmbientConnection = new(null);

    public bool CanGetClientCapabilities => GetCapabilitiesBox().Value is not null;

    public VSInternalClientCapabilities ClientCapabilities => GetCapabilitiesBox().Value ?? throw new InvalidOperationException("Client capabilities requested before initialized.");

    public void SetCapabilities(VSInternalClientCapabilities clientCapabilities)
    {
        GetCapabilitiesBox().Value = clientCapabilities;
    }

    private StrongBox<VSInternalClientCapabilities?> GetCapabilitiesBox()
        => AmbientConnectionToken.Current is { } token
            ? _capabilitiesByConnection.GetOrCreateValue(token)
            : _capabilitiesWithNoAmbientConnection;
}
