// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

extern alias InteractiveHost;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Scripting.Hosting;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.UnitTests.Interactive
{
    using InteractiveHost::Microsoft.CodeAnalysis.Interactive;
    using Xunit.Abstractions;

    public abstract class AbstractInteractiveHostTests : CSharpTestBase, IAsyncLifetime
    {
        private SynchronizedStringWriter _synchronizedOutput = null!;
        private SynchronizedStringWriter _synchronizedErrorOutput = null!;
        private int[] _outputReadPosition = [0, 0];

        internal readonly InteractiveHost Host;
        internal readonly ITestOutputHelper TestOutputHelper;

        // DOTNET_ROOT must be set in order to run host process on .NET Core on machines (like CI)
        // that do not have the required version of the runtime installed globally.
        // 
        // If it was not set the process would fail with exit code -2147450749:
        // "A fatal error occurred. The required library hostfxr.dll could not be found."
        //
        // See https://github.com/dotnet/runtime/issues/38462.
        static AbstractInteractiveHostTests()
        {
            if (Environment.GetEnvironmentVariable("DOTNET_ROOT") == null)
            {
                // DOTNET_HOST_PATH / DOTNET_EXPERIMENTAL_HOST_PATH are the SDK/test-host-provided
                // pointers to the exact dotnet muxer that launched the current test run (`dotnet
                // test` sets these for child processes precisely so they resolve the same runtime,
                // not an unrelated one that also happens to be on PATH) -- prefer them over guessing.
                // See RuntimeHostInfo.GetDotNetPathOrDefault in src/Compilers/Shared for the same
                // priority used elsewhere in this repo for tool resolution.
                var dir =
                    Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath ? Path.GetDirectoryName(hostPath) :
                    Environment.GetEnvironmentVariable("DOTNET_EXPERIMENTAL_HOST_PATH") is { Length: > 0 } experimentalHostPath ? Path.GetDirectoryName(experimentalHostPath) :
                    null;

                if (dir == null || !HasHostFxr(dir))
                {
                    // This test project targets net472, so this static constructor always runs inside
                    // a .NET Framework test-runner process (e.g. testhost.exe), never one launched
                    // through the `dotnet` muxer itself -- so the current process's own executable
                    // tells us nothing about where the .NET (Core) SDK/runtime used to run the
                    // InteractiveHost child process lives. Search PATH instead: that's how `dotnet`
                    // would actually be resolved from a shell on this machine, and doesn't depend on
                    // any assumption about install layout relative to this process's runtime
                    // directory. A directory merely containing a file named dotnet.exe isn't
                    // sufficient -- package-manager shims and forwarding executables (e.g. Windows App
                    // Execution Aliases) can have that name without being a real SDK/runtime root, so
                    // also require the host\fxr\<version>\ layout that actually holds hostfxr.dll.
                    dir = (Environment.GetEnvironmentVariable("PATH") ?? "")
                        .Split(Path.PathSeparator)
                        .Select(p => p.Trim())
                        .Where(p => p.Length > 0 && File.Exists(Path.Combine(p, "dotnet.exe")))
                        .FirstOrDefault(HasHostFxr);
                }

                if (dir == null)
                {
                    // Fallback: walk up from the shared-framework directory looking for a sibling
                    // dotnet.exe with a real host\fxr\ layout. This assumes the standard SDK install
                    // layout (dotnet\shared\Microsoft.NETCore.App\<version>\), which doesn't hold for
                    // every runtime layout (e.g. a private testhost copy of the runtime) -- hence
                    // trying PATH first.
                    dir = RuntimeEnvironment.GetRuntimeDirectory();
                    while (dir != null && !(File.Exists(Path.Combine(dir, "dotnet.exe")) && HasHostFxr(dir)))
                    {
                        dir = Path.GetDirectoryName(dir);
                    }
                }

                // dotnet.exe not found
                Assert.NotNull(dir);

                Environment.SetEnvironmentVariable("DOTNET_ROOT", dir);
            }

            static bool HasHostFxr(string dotnetRoot)
            {
                var fxrDir = Path.Combine(dotnetRoot, "host", "fxr");
                return Directory.Exists(fxrDir) &&
                    Directory.EnumerateDirectories(fxrDir).Any(v => File.Exists(Path.Combine(v, "hostfxr.dll")));
            }
        }

        protected AbstractInteractiveHostTests(ITestOutputHelper testOutputHelper)
        {
            TestOutputHelper = testOutputHelper;
            Host = new InteractiveHost(typeof(CSharpReplServiceProvider), ".", millisecondsTimeout: -1, joinOutputWritingThreadsOnDisposal: true);

            Host.InteractiveHostProcessCreationFailed += (exception, exitCode) =>
                testOutputHelper.WriteLine((exception?.Message ?? "Host process terminated unexpectedly.") + $" Exit code: {exitCode?.ToString() ?? "<unknown>"}");

            RedirectOutput();
        }

        internal abstract InteractiveHostPlatform DefaultPlatform { get; }
        internal abstract bool UseDefaultInitializationFile { get; }

        public async Task InitializeAsync()
        {
            var initializationFileName = UseDefaultInitializationFile ? "CSharpInteractive.rsp" : null;

            await Host.ResetAsync(InteractiveHostOptions.CreateFromDirectory(TestUtils.HostRootPath, initializationFileName, CultureInfo.InvariantCulture, CultureInfo.InvariantCulture, DefaultPlatform));

            // assert and remove logo:
            var output = SplitLines(await ReadOutputToEnd());
            var errorOutput = await ReadErrorOutputToEnd();

            AssertEx.AssertEqualToleratingWhitespaceDifferences("", errorOutput);

            var expectedOutput = new List<string>
            {
                string.Format(CSharpScriptingResources.LogoLine1, CommonCompiler.GetProductVersion(typeof(CSharpReplServiceProvider)))
            };

            if (UseDefaultInitializationFile)
            {
                expectedOutput.Add(string.Format(InteractiveHostResources.Loading_context_from_0, initializationFileName));
            }

            expectedOutput.Add(InteractiveHostResources.Type_Sharphelp_for_more_information);

            AssertEx.Equal(expectedOutput, output);

            // remove logo:
            ClearOutput();
        }

        public async Task DisposeAsync()
        {
            var service = await Host.TryGetServiceAsync();
            Assert.NotNull(service);

            var process = service!.Process;

            Host.Dispose();

            // the process should be terminated
            if (process != null && !process.HasExited)
            {
                process.WaitForExit();
            }
        }

        public void RedirectOutput()
        {
            _synchronizedOutput = new SynchronizedStringWriter();
            _synchronizedErrorOutput = new SynchronizedStringWriter();
            ClearOutput();
            Host.SetOutputs(_synchronizedOutput, _synchronizedErrorOutput);
        }

        public static ImmutableArray<string> SplitLines(string text)
        {
            return ImmutableArray.Create(text.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries));
        }

        public async Task<bool> LoadReference(string reference)
        {
            return await Execute($"#r \"{reference}\"");
        }

        public async Task<bool> Execute(string code)
        {
            var task = await Host.ExecuteAsync(code);
            return task.Success;
        }

        public Task<string> ReadErrorOutputToEnd()
        {
            return ReadOutputToEnd(isError: true);
        }

        public void ClearOutput()
        {
            _outputReadPosition = [0, 0];
            _synchronizedOutput.Clear();
            _synchronizedErrorOutput.Clear();
        }

        public async Task RestartHost()
        {
            ClearOutput();

            await Host.ResetAsync(InteractiveHostOptions.CreateFromDirectory(TestUtils.HostRootPath, initializationFileName: null, CultureInfo.InvariantCulture, CultureInfo.InvariantCulture, InteractiveHostPlatform.Desktop64));
        }

        public async Task<string> ReadOutputToEnd(bool isError = false)
        {
            // writes mark to the STDOUT/STDERR pipe in the remote process:
            var remoteService = await Host.TryGetServiceAsync().ConfigureAwait(false);

            if (remoteService == null)
            {
                Assert.Fail(@$"
Remote service unavailable
STDERR: {_synchronizedErrorOutput}
STDOUT: {_synchronizedOutput}
");
            }

            var writer = isError ? _synchronizedErrorOutput : _synchronizedOutput;
            var markPrefix = '\uFFFF';
            var mark = markPrefix + Guid.NewGuid().ToString();

            await remoteService!.JsonRpc.InvokeAsync(nameof(InteractiveHost.Service.RemoteConsoleWriteAsync), InteractiveHost.OutputEncoding.GetBytes(mark), isError).ConfigureAwait(false);
            while (true)
            {
                var data = writer.Prefix(mark, ref _outputReadPosition[isError ? 0 : 1]);
                if (data != null)
                {
                    return data;
                }

                await Task.Delay(10);
            }
        }

        public static (string Path, ImmutableArray<byte> Image) CompileLibrary(
            TempDirectory dir, string fileName, string assemblyName, string source, params MetadataReference[] references)
        {
            var file = dir.CreateFile(fileName);
            var compilation = CreateEmptyCompilation(
                new[] { source },
                assemblyName: assemblyName,
                references: TargetFrameworkUtil.GetReferences(TargetFramework.NetStandard20, references),
                options: fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? TestOptions.ReleaseExe : TestOptions.ReleaseDll);

            var image = compilation.EmitToArray();
            file.WriteAllBytes(image);

            return (file.Path, image);
        }

        public static string PrintSearchPaths(params string[] paths)
            => paths.Length == 0 ? "SearchPaths { }" : $"SearchPaths {{ {string.Join(", ", paths.Select(p => "\"" + p.Replace("\\", "\\\\") + "\""))} }}";

        public async Task<string> GetHostRuntimeDirectoryAsync()
        {
            var remoteService = await Host.TryGetServiceAsync().ConfigureAwait(false);
            Assert.NotNull(remoteService);
            return await remoteService!.JsonRpc.InvokeAsync<string>(nameof(InteractiveHost.Service.GetRuntimeDirectoryAsync)).ConfigureAwait(false);
        }
    }
}
