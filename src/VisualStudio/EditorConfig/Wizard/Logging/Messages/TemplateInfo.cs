// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.VisualStudio.TemplateWizard;

namespace Microsoft.VisualStudio.Templates.Editorconfig.Wizard.Logging.Messages;

internal class TemplateInfo : ILogMessage<MessageData>
{
    private readonly WizardRunKind runKind;
    private readonly Dictionary<string, string> replacementsDictionary;

    public TemplateInfo(WizardRunKind runKind, Dictionary<string, string> replacementsDictionary)
    {
        this.runKind = runKind;
        this.replacementsDictionary = replacementsDictionary;
    }

    // Only an explicit allowlist of non-user-identifying replacement keys is safe to log;
    // most other entries can carry user-controlled project/user names or local paths.
    private static readonly ImmutableArray<string> s_allowedReplacementKeys = ImmutableArray.Create("$type$");

    public ImmutableArray<MessageData> GetMessageData()
    {
        var builder = ImmutableArray.CreateBuilder<MessageData>();
        builder.Add(new MessageData("WizardRunKind", () => Enum.GetName(runKind.GetType(), runKind)));
        foreach (var key in s_allowedReplacementKeys)
        {
            if (replacementsDictionary.TryGetValue(key, out var value))
            {
                builder.Add(new MessageData("ReplacementsDictionaryValue", () => value));
            }
        }
        return builder.ToImmutable();
    }
}
