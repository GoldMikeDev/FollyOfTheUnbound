// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.Host;

[ExportWorkspaceService(typeof(IWorkspaceConfigurationService), ServiceLayer.Host), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class WorkspaceConfigurationService(IGlobalOptionService globalOptions) : IWorkspaceConfigurationService
{
    private readonly IGlobalOptionService _globalOptions = globalOptions;

    // Deliberately not cached: SourceGeneratorExecution is connection-scoped in daemon mode (see
    // ConnectionScopedOptionOverrides / docs/ide/specs/daemon-per-connection-isolation.md), so the answer can
    // legitimately differ between calls made on different daemon connections' ambient contexts. This service
    // instance is a process-wide MEF singleton ([Shared], one ExportProvider for the whole daemon), so caching
    // the result of the first call here would fix every workspace to whichever connection happened to read it
    // first, silently ignoring every other connection's override.
    public WorkspaceConfigurationOptions Options => _globalOptions.GetWorkspaceConfigurationOptions();
}
