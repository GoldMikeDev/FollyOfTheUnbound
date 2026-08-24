// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace.FileWatching;
using Microsoft.CodeAnalysis.ProjectSystem;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Workspaces.ProjectSystem;
using Roslyn.LanguageServer.Protocol;
using StreamJsonRpc;
using Xunit.Abstractions;
using FileSystemWatcher = Roslyn.LanguageServer.Protocol.FileSystemWatcher;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class LspFileChangeWatcherTests(ITestOutputHelper testOutputHelper)
    : AbstractLanguageServerHostTests(testOutputHelper)
{
    private readonly ClientCapabilities _clientCapabilitiesWithFileWatcherSupport = new()
    {
        Workspace = new WorkspaceClientCapabilities
        {
            DidChangeWatchedFiles = new DidChangeWatchedFilesClientCapabilities { DynamicRegistration = true }
        }
    };

    [Fact]
    public async Task LspFileWatcherNotSupportedWithoutClientSupport()
    {
        await using var testLspServer = await CreateLanguageServerAsync();

        AssertFileWatcherKind<DefaultFileChangeWatcher>(testLspServer);
    }

    [Fact]
    public async Task LspFileWatcherSupportedWithClientSupport()
    {
        await using var testLspServer = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);

        AssertFileWatcherKind<LspFileChangeWatcher>(testLspServer);
    }

    [Fact]
    public async Task CreatingDirectoryWatchRequestsDirectoryWatch()
    {
        AsynchronousOperationListenerProvider.Enable(enable: true);

        await using var testLspServer = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);
        var lspFileChangeWatcher = AssertFileWatcherKind<LspFileChangeWatcher>(testLspServer);

        var dynamicCapabilitiesRpcTarget = new DynamicCapabilitiesRpcTarget();
        testLspServer.AddClientLocalRpcTarget(dynamicCapabilitiesRpcTarget);

        var tempDirectory = TempRoot.CreateDirectory();

        // Try creating a context and ensure we created the registration
        var context = lspFileChangeWatcher.CreateContext([new ProjectSystem.WatchedDirectory(tempDirectory.Path, extensionFilters: [])]);
        await WaitForFileWatcherAsync(testLspServer);

        var watcher = GetSingleFileWatcher(dynamicCapabilitiesRpcTarget);

        Assert.Equal(ProtocolConversions.CreateAbsoluteDocumentUri(tempDirectory.Path), watcher.GlobPattern.Second.BaseUri.Second);
        Assert.Equal("**/*", watcher.GlobPattern.Second.Pattern);

        // Get rid of the registration and it should be gone again
        context.Dispose();
        await WaitForFileWatcherAsync(testLspServer);
        AssertNoFileWatcherRegistration(dynamicCapabilitiesRpcTarget);
    }

    [Fact]
    public async Task CreatingFileWatchRequestsFileWatch()
    {
        AsynchronousOperationListenerProvider.Enable(enable: true);

        await using var testLspServer = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);
        var lspFileChangeWatcher = AssertFileWatcherKind<LspFileChangeWatcher>(testLspServer);

        var dynamicCapabilitiesRpcTarget = new DynamicCapabilitiesRpcTarget();
        testLspServer.AddClientLocalRpcTarget(dynamicCapabilitiesRpcTarget);

        var tempDirectory = TempRoot.CreateDirectory();

        // Try creating a single file watch and ensure we created the registration
        var context = lspFileChangeWatcher.CreateContext([]);
        var filePath = Path.Combine(tempDirectory.Path, "SingleFile.txt");
        var watchedFile = context.EnqueueWatchingFile(filePath);
        await WaitForFileWatcherAsync(testLspServer);

        var watcher = GetSingleFileWatcher(dynamicCapabilitiesRpcTarget);

        Assert.Equal(ProtocolConversions.CreateAbsoluteDocumentUri(tempDirectory.Path), watcher.GlobPattern.Second.BaseUri.Second);
        Assert.Equal("SingleFile.txt", watcher.GlobPattern.Second.Pattern);

        // Get rid of the registration and it should be gone again
        watchedFile.Dispose();
        context.Dispose();
        await WaitForFileWatcherAsync(testLspServer);
        AssertNoFileWatcherRegistration(dynamicCapabilitiesRpcTarget);
    }

    /// <summary>
    /// Regression test for the <c>_removedFromWorkspace</c> guard in <see cref="ProjectSystemProject.RemoveFromWorkspace"/>:
    /// unlike <see cref="DefaultFileChangeWatcher"/>, <see cref="LspFileChangeWatcher"/>'s <c>FileChangeContext.Dispose</c>
    /// is not idempotent -- it sends a <c>client/unregisterCapability</c> request every time it runs. A regression that let
    /// a second <c>RemoveFromWorkspace</c> call reach that disposal again would send a second unregister request for an
    /// ID <see cref="DynamicCapabilitiesRpcTarget.UnregisterCapabilityAsync"/> has already removed, failing its own
    /// <c>Assert.True(TryRemove(...))</c> -- not just redundantly, but observably, which is what this test relies on.
    /// </summary>
    [Fact]
    public async Task RemovingProjectTwiceOnlyUnregistersFileWatchOnce()
    {
        AsynchronousOperationListenerProvider.Enable(enable: true);

        await using var testLspServer = await CreateLanguageServerAsync(_clientCapabilitiesWithFileWatcherSupport);
        AssertFileWatcherKind<LspFileChangeWatcher>(testLspServer);

        var dynamicCapabilitiesRpcTarget = new DynamicCapabilitiesRpcTarget();
        testLspServer.AddClientLocalRpcTarget(dynamicCapabilitiesRpcTarget);

        // A project only gets a *directory*-level watch registration (the one FileChangeContext.Dispose actually
        // unregisters) when it's created with a FilePath -- see ProjectSystemProject.GetWatchedDirectories. A
        // file watched via EnqueueWatchingFile that isn't covered by such a directory (the common case for a
        // project created without one) gets its own per-document registration instead, tracked and disposed
        // independently in _documentWatchedFiles -- unrelated to what RemoveFromWorkspace's _removedFromWorkspace
        // guard protects, so this test needs the directory-covered path to exercise the real invariant.
        var tempDirectory = TempRoot.CreateDirectory();
        var workspaceFactory = testLspServer.GetRequiredLspService<LanguageServerWorkspaceFactory>();
        var project = await workspaceFactory.HostProjectFactory.CreateAndAddToWorkspaceAsync(
            "TestProject",
            LanguageNames.CSharp,
            new ProjectSystemProjectCreationInfo { FilePath = Path.Combine(tempDirectory.Path, "TestProject.csproj"), AssemblyName = "TestProject" },
            workspaceFactory.ProjectSystemHostInfo);

        await WaitForFileWatcherAsync(testLspServer);
        Assert.NotEmpty(dynamicCapabilitiesRpcTarget.Registrations);

        project.RemoveFromWorkspace();
        await WaitForFileWatcherAsync(testLspServer);
        AssertNoFileWatcherRegistration(dynamicCapabilitiesRpcTarget);

        // The second call must throw before ever reaching disposal again -- if it instead reached
        // LspFileChangeWatcher.FileChangeContext.Dispose a second time, the resulting duplicate
        // client/unregisterCapability request would fail inside UnregisterCapabilityAsync above, not here.
        Assert.Throws<InvalidOperationException>(project.RemoveFromWorkspace);
        await WaitForFileWatcherAsync(testLspServer);
    }

    private static T AssertFileWatcherKind<T>(TestLspServer server) where T : IFileChangeWatcher
    {
        var lspFileWatcher = server.GetRequiredLspService<IFileChangeWatcher>();
        var delegatingWatcher = Assert.IsType<DelegatingFileChangeWatcher>(lspFileWatcher);
        return Assert.IsType<T>(delegatingWatcher.GetTestAccessor().UnderlyingFileWatcher);
    }

    private static Task WaitForFileWatcherAsync(TestLspServer testLspServer)
        => testLspServer.ExportProvider.GetExportedValue<AsynchronousOperationListenerProvider>().GetWaiter(FeatureAttribute.Workspace).ExpeditedWaitAsync();

    private static FileSystemWatcher GetSingleFileWatcher(DynamicCapabilitiesRpcTarget dynamicCapabilities)
    {
        var registrationJson = Assert.IsType<JsonElement>(
            Assert.Single(dynamicCapabilities.Registrations.Values, static registration => registration.Method == Methods.WorkspaceDidChangeWatchedFilesName).RegisterOptions);
        var registration = JsonSerializer.Deserialize<DidChangeWatchedFilesRegistrationOptions>(registrationJson, ProtocolConversions.LspJsonSerializerOptions)!;

        return Assert.Single(registration.Watchers);
    }

    private static void AssertNoFileWatcherRegistration(DynamicCapabilitiesRpcTarget dynamicCapabilities)
        => Assert.DoesNotContain(dynamicCapabilities.Registrations.Values, static registration => registration.Method == Methods.WorkspaceDidChangeWatchedFilesName);

    private sealed class DynamicCapabilitiesRpcTarget
    {
        public readonly ConcurrentDictionary<string, Registration> Registrations = new();

        [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
        public async Task RegisterCapabilityAsync(RegistrationParams registrationParams, CancellationToken _)
        {
            foreach (var registration in registrationParams.Registrations)
                Assert.True(Registrations.TryAdd(registration.Id, registration));
        }

        [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
        public async Task UnregisterCapabilityAsync(UnregistrationParams unregistrationParams, CancellationToken _)
        {
            foreach (var unregistration in unregistrationParams.Unregistrations)
                Assert.True(Registrations.TryRemove(unregistration.Id, out var _));
        }
    }
}
