// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.Daemon;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;

public sealed class DaemonPipeNameTests
{
    private const string ToolIdentifier = "/tools/v1/Microsoft.CodeAnalysis.LanguageServer.dll";

    [Fact]
    public void PipeName_IsDeterministic()
    {
        var first = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);
        var second = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);
        Assert.Equal(first, second);
    }

    [Fact]
    public void PipeName_DiffersByToolIdentifier()
    {
        var v1 = DaemonPipeName.GetPipeName("user", isAdmin: false, "/tools/v1/server.dll", serverArguments: []);
        var v2 = DaemonPipeName.GetPipeName("user", isAdmin: false, "/tools/v2/server.dll", serverArguments: []);
        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void PipeName_DiffersByUser()
    {
        var user1 = DaemonPipeName.GetPipeName("user1", isAdmin: false, ToolIdentifier, serverArguments: []);
        var user2 = DaemonPipeName.GetPipeName("user2", isAdmin: false, ToolIdentifier, serverArguments: []);
        Assert.NotEqual(user1, user2);
    }

    [Fact]
    public void PipeName_DiffersByElevation()
    {
        var standard = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);
        var elevated = DaemonPipeName.GetPipeName("user", isAdmin: true, ToolIdentifier, serverArguments: []);
        Assert.NotEqual(standard, elevated);
    }

    [Fact]
    public void PipeName_NormalizesToolIdentifierCasingOnWindows()
    {
        var mixedCase = DaemonPipeName.GetPipeName("user", isAdmin: false, "/Tools/V1/Server.dll", serverArguments: []);
        var lowerCase = DaemonPipeName.GetPipeName("user", isAdmin: false, "/tools/v1/server.dll", serverArguments: []);
        if (OperatingSystem.IsWindows())
            Assert.Equal(mixedCase, lowerCase);
        else
            Assert.NotEqual(mixedCase, lowerCase);
    }

    [Fact]
    public void PipeName_DiffersByServerArguments()
    {
        var noExtension = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);
        var withExtension = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: ["--extension", "foo.dll"]);
        Assert.NotEqual(noExtension, withExtension);
    }

    [Fact]
    public void PipeName_DoesNotCollideAcrossServerArgumentSplits()
    {
        var split = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: ["--extension", "a b"]);
        var joined = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: ["--extension", "a", "b"]);
        Assert.NotEqual(split, joined);
    }

    [Theory]
    [InlineData("--extensionLogDirectory")]
    [InlineData("--sourceGeneratorExecutionPreference")]
    public void PipeName_IgnoresPerConnectionRoutedOptions_TwoTokenForm(string option)
    {
        // Values now routed per-connection via ConnectionHandshake (see
        // docs/ide/specs/daemon-per-connection-isolation.md's phases 5/7) no longer need to split clients into
        // separate daemons: unlike the rest of serverArguments, a second client's value for one of these is
        // genuinely applied to that connection, not silently ignored.
        var first = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: [option, "/tmp/a"]);
        var second = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: [option, "/tmp/b"]);
        var none = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

        Assert.Equal(first, second);
        Assert.Equal(first, none);
    }

    [Theory]
    [InlineData("--extensionLogDirectory")]
    [InlineData("--sourceGeneratorExecutionPreference")]
    public void PipeName_IgnoresPerConnectionRoutedOptions_InlineForm(string option)
    {
        var first = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: [$"{option}=/tmp/a"]);
        var second = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: [$"{option}=/tmp/b"]);
        var none = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

        Assert.Equal(first, second);
        Assert.Equal(first, none);
    }

    [Fact]
    public void PipeName_StillDiffersByOtherArgumentsAroundPerConnectionRoutedOptions()
    {
        // Excluding a per-connection-routed option and its value must not accidentally swallow neighboring,
        // still-relevant arguments.
        var first = DaemonPipeName.GetPipeName(
            "user", isAdmin: false, ToolIdentifier,
            serverArguments: ["--extension", "a.dll", "--extensionLogDirectory", "/tmp/a", "--extension", "b.dll"]);
        var second = DaemonPipeName.GetPipeName(
            "user", isAdmin: false, ToolIdentifier,
            serverArguments: ["--extension", "a.dll", "--extensionLogDirectory", "/tmp/b", "--extension", "b.dll"]);
        var differentExtensions = DaemonPipeName.GetPipeName(
            "user", isAdmin: false, ToolIdentifier,
            serverArguments: ["--extension", "a.dll", "--extensionLogDirectory", "/tmp/a", "--extension", "c.dll"]);

        Assert.Equal(first, second);
        Assert.NotEqual(first, differentExtensions);
    }

    [Theory]
    [InlineData("DOTNET_HOST_PATH")]
    [InlineData("DOTNET_EXPERIMENTAL_HOST_PATH")]
    public void PipeName_DiffersByDotNetHostPathEnvironmentVariable(string variableName)
    {
        // RuntimeHostInfo.GetToolDotNetRoot prioritizes these two over scanning PATH, and ServerExecutable.Start
        // uses the result for the bundled server process's own DOTNET_ROOT -- a daemon-launch-time decision made
        // once, from whichever client happened to launch it. Two clients that agree on PATH but differ in
        // either of these must not share a daemon, or the second would silently run on the wrong .NET
        // installation.
        var original = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, "/fake/dotnet/one");
            var first = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

            Environment.SetEnvironmentVariable(variableName, "/fake/dotnet/two");
            var second = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

            Assert.NotEqual(first, second);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, original);
        }
    }

    [Fact]
    public void PipeName_DiffersByKeepAliveEnvironmentVariable()
    {
        // Keepalive genuinely can't be given per-connection semantics (it governs how long the one shared
        // daemon lingers after its *last* client disconnects), so two clients relying on different
        // ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE values -- without an explicit --daemonKeepAlive argument,
        // which would already be part of serverArguments -- must get separate daemons rather than one
        // silently ignoring the other's requested lifetime.
        var original = Environment.GetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, "60");
            var short60 = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, "3600");
            var long3600 = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, null);
            var unset = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

            Assert.NotEqual(short60, long3600);
            Assert.NotEqual(short60, unset);
            Assert.NotEqual(long3600, unset);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, original);
        }
    }

    [Fact]
    public void PipeName_NormalizesEquivalentKeepAliveEnvironmentValues()
    {
        // An unset variable, one explicitly equal to the default, and an invalid one (which also falls back
        // to the default in LanguageServerCommandLine) all resolve to the same effective keepalive -- clients
        // that happen to differ only in these should still share a daemon rather than being split unnecessarily.
        var original = Environment.GetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, null);
            var unset = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, DaemonPipeName.DefaultDaemonKeepAliveSeconds.ToString());
            var explicitDefault = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, "not-a-number");
            var invalid = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

            Assert.Equal(unset, explicitDefault);
            Assert.Equal(unset, invalid);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, original);
        }
    }

    [Fact]
    public void PipeName_OutOfRangeKeepAliveEnvironmentValue_DoesNotCollideWithDefault()
    {
        // Unlike a non-numeric value (which LanguageServerCommandLine's DefaultValueFactory also silently
        // falls back to the default for, no AddError), a value that parses but is out of range (< -1) is one
        // LanguageServerCommandLine rejects outright via AddError, refusing to launch a daemon over it.
        // Collapsing it to the same pipe key as a genuinely-valid default would let a client with this invalid
        // setting silently reuse an already-running default-keyed daemon instead of consistently hitting that
        // same launch failure -- so it must resolve to a different pipe name than the default, not the same one.
        var original = Environment.GetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, null);
            var unset = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, "-2");
            var outOfRange = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);

            Assert.NotEqual(unset, outOfRange);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, original);
        }
    }

    [Fact]
    public void PipeName_IgnoresKeepAliveEnvironmentVariableWhenArgumentIsExplicit()
    {
        // An explicit --daemonKeepAlive argument already dominates the environment variable in
        // LanguageServerCommandLine, and it's already part of serverArguments -- so two clients that agree on
        // an explicit value shouldn't be split into separate daemons just because they inherited different,
        // moot environment values.
        var original = Environment.GetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable);
        try
        {
            string[] explicitArguments = ["--daemonKeepAlive", "60"];

            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, "1");
            var withEnvironmentValueOne = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, explicitArguments);

            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, "3600");
            var withEnvironmentValueDifferent = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, explicitArguments);

            Assert.Equal(withEnvironmentValueOne, withEnvironmentValueDifferent);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, original);
        }
    }

    [Fact]
    public void PipeName_IgnoresKeepAliveEnvironmentVariableWhenArgumentIsExplicit_InlineForm()
    {
        // System.CommandLine also accepts "--daemonKeepAlive=60" (in addition to the two-token
        // "--daemonKeepAlive 60" form covered above); both make the argument explicit in
        // LanguageServerCommandLine, so both must be recognized as explicit here too.
        var original = Environment.GetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable);
        try
        {
            string[] inlineArguments = ["--daemonKeepAlive=60"];

            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, "1");
            var withEnvironmentValueOne = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, inlineArguments);

            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, "3600");
            var withEnvironmentValueDifferent = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, inlineArguments);

            Assert.Equal(withEnvironmentValueOne, withEnvironmentValueDifferent);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DaemonPipeName.DaemonKeepAliveEnvironmentVariable, original);
        }
    }

    [Theory]
    [InlineData("--extension")]
    [InlineData("--devKitDependencyPath")]
    [InlineData("--csharpDesignTimePath")]
    public void PipeName_DiffersForSameRelativePathArgumentFromDifferentWorkingDirectories(string option)
    {
        // Two clients launched from different working directories with the same *relative* path argument (e.g.
        // both pass "--extension foo.dll") must not collide onto one daemon: the daemon only ever gets one
        // client's working directory to resolve that relative path against (whichever client happened to launch
        // it), so the second client would silently have its path resolved against the wrong directory.
        var originalDirectory = Environment.CurrentDirectory;
        var firstDirectory = System.IO.Directory.CreateTempSubdirectory().FullName;
        var secondDirectory = System.IO.Directory.CreateTempSubdirectory().FullName;
        try
        {
            Environment.CurrentDirectory = firstDirectory;
            var first = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: [option, "foo.dll"]);

            Environment.CurrentDirectory = secondDirectory;
            var second = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: [option, "foo.dll"]);

            Assert.NotEqual(first, second);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            System.IO.Directory.Delete(firstDirectory, recursive: true);
            System.IO.Directory.Delete(secondDirectory, recursive: true);
        }
    }

    [Fact]
    public void PipeName_SameAbsolutePathArgumentStillCollides()
    {
        // Sanity check for the fix above: an already-absolute path argument (the common case -- these options'
        // help text says "full paths") must still produce the same key regardless of working directory, since
        // canonicalization is a no-op for it.
        var absolutePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foo.dll");
        var originalDirectory = Environment.CurrentDirectory;
        var firstDirectory = System.IO.Directory.CreateTempSubdirectory().FullName;
        var secondDirectory = System.IO.Directory.CreateTempSubdirectory().FullName;
        try
        {
            Environment.CurrentDirectory = firstDirectory;
            var first = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: ["--extension", absolutePath]);

            Environment.CurrentDirectory = secondDirectory;
            var second = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: ["--extension", absolutePath]);

            Assert.Equal(first, second);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            System.IO.Directory.Delete(firstDirectory, recursive: true);
            System.IO.Directory.Delete(secondDirectory, recursive: true);
        }
    }

    [Fact]
    public void PipeName_MultipleExtensionValuesAllCanonicalized()
    {
        // --extension has array arity (one-or-more following value tokens); all of them must be canonicalized,
        // not just the first, and a trailing unrelated option must still be recognized correctly afterward.
        var originalDirectory = Environment.CurrentDirectory;
        var firstDirectory = System.IO.Directory.CreateTempSubdirectory().FullName;
        var secondDirectory = System.IO.Directory.CreateTempSubdirectory().FullName;
        try
        {
            Environment.CurrentDirectory = firstDirectory;
            var first = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: ["--extension", "a.dll", "b.dll"]);

            Environment.CurrentDirectory = secondDirectory;
            var second = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: ["--extension", "a.dll", "b.dll"]);

            Assert.NotEqual(first, second);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            System.IO.Directory.Delete(firstDirectory, recursive: true);
            System.IO.Directory.Delete(secondDirectory, recursive: true);
        }
    }

    [Fact]
    public void PipeName_IsFileSystemAndUrlSafe()
    {
        var name = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('=', name);
    }

    [Fact]
    public void MutexNames_HaveExpectedShapeAndDiffer()
    {
        var pipeName = DaemonPipeName.GetPipeName("user", isAdmin: false, ToolIdentifier, serverArguments: []);
        var serverMutex = DaemonPipeName.GetServerMutexName(pipeName);
        var clientMutex = DaemonPipeName.GetClientMutexName(pipeName);

        Assert.StartsWith(@"Global\", serverMutex);
        Assert.StartsWith(@"Global\", clientMutex);
        Assert.EndsWith(".server", serverMutex);
        Assert.EndsWith(".client", clientMutex);
        Assert.NotEqual(serverMutex, clientMutex);
        Assert.Contains(pipeName, serverMutex);
        Assert.Contains(pipeName, clientMutex);
    }
}
