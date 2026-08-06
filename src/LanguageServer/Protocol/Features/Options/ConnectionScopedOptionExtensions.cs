// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// Connection-scoped-aware replacements for <see cref="IGlobalOptionService.GetOption{T}(Option2{T})"/> and its
/// overloads. Prefer these over the plain <c>GetOption</c> methods anywhere reachable from LSP request/
/// notification handling in daemon mode (formatting, completion, and similar per-request option reads) -- see
/// docs/ide/specs/daemon-per-connection-isolation.md's phase 3/4 for the call sites this was audited against
/// and why plain <c>GetOption</c> there would read the wrong (globally shared, not per-client) value.
/// </summary>
internal static class ConnectionScopedOptionExtensions
{
    public static T GetConnectionScopedOption<T>(this IGlobalOptionService options, Option2<T> option)
        => ConnectionScopedOptionOverrides.TryGetOverride(new OptionKey2(option), out var value)
            ? (T)value!
            : options.GetOption(option);

    public static T GetConnectionScopedOption<T>(this IGlobalOptionService options, PerLanguageOption2<T> option, string language)
        => ConnectionScopedOptionOverrides.TryGetOverride(new OptionKey2(option, language), out var value)
            ? (T)value!
            : options.GetOption(option, language);

    public static T GetConnectionScopedOption<T>(this IGlobalOptionService options, OptionKey2 optionKey)
        => ConnectionScopedOptionOverrides.TryGetOverride(optionKey, out var value)
            ? (T)value!
            : options.GetOption<T>(optionKey);
}
