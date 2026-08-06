// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Options;

/// <summary>
/// Keeps <see cref="Solution.FallbackAnalyzerOptions"/> up-to-date with global option values maintained by <see cref="IGlobalOptionService"/>.
/// </summary>
[Export]
[ExportEventListener(WellKnownEventListeners.Workspace,
    [WorkspaceKind.Host, WorkspaceKind.Interactive, WorkspaceKind.SemanticSearch, WorkspaceKind.MetadataAsSource, WorkspaceKind.MiscellaneousFiles, WorkspaceKind.Debugger, WorkspaceKind.Preview]), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class SolutionAnalyzerConfigOptionsUpdater(IGlobalOptionService globalOptions) : IEventListener
{
    public void StartListening(Workspace workspace)
        => globalOptions.AddOptionChangedHandler(workspace, GlobalOptionsChanged);

    public void StopListening(Workspace workspace)
        => globalOptions.RemoveOptionChangedHandler(workspace, GlobalOptionsChanged);

    private void GlobalOptionsChanged(object sender, object target, OptionChangedEventArgs args)
    {
        Debug.Assert(target is Workspace);

        try
        {
            ApplyChangedOptionsIfRelevant((Workspace)target, args.ChangedOptions.SelectAsArray(static o => KeyValuePair.Create(o.key, o.newValue)));
        }
        catch (Exception e) when (FatalError.ReportAndPropagate(e, ErrorSeverity.Diagnostic))
        {
            throw ExceptionUtilities.Unreachable();
        }
    }

    /// <summary>
    /// Applies <paramref name="changedOptions"/> to <paramref name="workspace"/>'s <see cref="Solution.FallbackAnalyzerOptions"/>,
    /// the same transform <see cref="GlobalOptionsChanged"/> applies in response to a real
    /// <see cref="IGlobalOptionService"/> change event. Exposed so callers that change options through a path
    /// other than <see cref="IGlobalOptionService"/> (e.g. daemon-mode connection-scoped overrides -- see
    /// docs/ide/specs/daemon-per-connection-isolation.md -- which intentionally never touch the shared
    /// <see cref="IGlobalOptionService"/>, and so never raise the event this type normally listens for) can
    /// still keep a workspace's fallback options in sync without duplicating this logic.
    /// </summary>
    public static void ApplyChangedOptionsIfRelevant(Workspace workspace, IReadOnlyList<KeyValuePair<OptionKey2, object?>> changedOptions)
    {
        // only editorconfig options are stored in Solution.FallbackAnalyzerOptions:
        if (!changedOptions.Any(static o => o.Key.Option.Definition.IsEditorConfigOption))
        {
            return;
        }

        _ = workspace.SetCurrentSolution(UpdateOptions, changeKind: WorkspaceChangeKind.SolutionChanged);

        Solution UpdateOptions(Solution oldSolution)
        {
            var oldFallbackOptions = oldSolution.FallbackAnalyzerOptions;
            var newFallbackOptions = oldFallbackOptions;

            foreach (var (language, languageOptions) in oldFallbackOptions)
            {
                ImmutableDictionary<string, string>.Builder? lazyBuilder = null;

                foreach (var (key, value) in changedOptions)
                {
                    if (!key.Option.Definition.IsEditorConfigOption)
                    {
                        continue;
                    }

                    if (key.Language != null && key.Language != language)
                    {
                        continue;
                    }

                    if (lazyBuilder == null)
                    {
                        lazyBuilder = ImmutableDictionary.CreateBuilder<string, string>(AnalyzerConfigOptions.KeyComparer);

                        // copy existing option values:
                        foreach (var oldKey in languageOptions.Keys)
                        {
                            if (languageOptions.TryGetValue(oldKey, out var oldValue))
                            {
                                lazyBuilder.Add(oldKey, oldValue);
                            }
                        }
                    }

                    // update changed value:
                    EditorConfigValueSerializer.Serialize(lazyBuilder, key.Option, language, value);
                }

                if (lazyBuilder != null)
                {
                    newFallbackOptions = newFallbackOptions.SetItem(
                        language,
                        StructuredAnalyzerConfigOptions.Create(new DictionaryAnalyzerConfigOptions(lazyBuilder.ToImmutable())));
                }
            }

            return oldSolution.WithFallbackAnalyzerOptions(newFallbackOptions);
        }
    }
}
