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
