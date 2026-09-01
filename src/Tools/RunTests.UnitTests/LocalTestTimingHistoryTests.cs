// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using Xunit;

namespace RunTests.UnitTests
{
    public sealed class LocalTestTimingHistoryTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _artifactsDirectory;

        public LocalTestTimingHistoryTests()
        {
            _tempRoot = Directory.CreateTempSubdirectory(nameof(LocalTestTimingHistoryTests)).FullName;
            // GetFilePath derives the history file's location from the parent of a directory literally
            // named "artifacts" -- matching how Options.ArtifactsDirectory is always resolved for real.
            _artifactsDirectory = Path.Combine(_tempRoot, "artifacts");
            Directory.CreateDirectory(_artifactsDirectory);
        }

        public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

        [Fact]
        public void GetKey_NullTfmTag_OmitsIt()
        {
            var withoutTfm = LocalTestTimingHistory.GetKey("Some.Assembly", null, "Debug", "x64");
            Assert.DoesNotContain("net", withoutTfm, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Some.Assembly", withoutTfm);
            Assert.Contains("Debug", withoutTfm);
            Assert.Contains("x64", withoutTfm);
        }

        [Fact]
        public void GetKey_WithTfmTag_DisambiguatesByTfm()
        {
            var net10 = LocalTestTimingHistory.GetKey("Some.Assembly", "net10.0", "Debug", "x64");
            var net472 = LocalTestTimingHistory.GetKey("Some.Assembly", "net472", "Debug", "x64");

            Assert.NotEqual(net10, net472);
        }

        [Fact]
        public void GetKey_DisambiguatesByConfiguration()
        {
            var debug = LocalTestTimingHistory.GetKey("Some.Assembly", "net10.0", "Debug", "x64");
            var release = LocalTestTimingHistory.GetKey("Some.Assembly", "net10.0", "Release", "x64");

            Assert.NotEqual(debug, release);
        }

        [Fact]
        public void GetKey_DisambiguatesByArchitecture()
        {
            var x64 = LocalTestTimingHistory.GetKey("Some.Assembly", "net10.0", "Debug", "x64");
            var arm64 = LocalTestTimingHistory.GetKey("Some.Assembly", "net10.0", "Debug", "arm64");

            Assert.NotEqual(x64, arm64);
        }

        [Fact]
        public void FreshHistory_NoFileYet_ReturnsNullForAnyKey()
        {
            var history = LocalTestTimingHistory.Load(_artifactsDirectory, _artifactsDirectory);
            Assert.Null(history.TryGetPreviousDuration("Some.Assembly"));
        }

        [Fact]
        public void NullArtifactsDirectory_NeverThrows_AndRecordIsANoOp()
        {
            var history = LocalTestTimingHistory.Load(artifactsDirectory: null);
            history.RecordPassed("Some.Assembly", TimeSpan.FromSeconds(5));

            Assert.Null(history.TryGetPreviousDuration("Some.Assembly"));
        }

        [Fact]
        public void RecordPassed_ThenLoadAgain_RoundTripsTheDuration()
        {
            var duration = TimeSpan.FromSeconds(93.5);

            var history = LocalTestTimingHistory.Load(_artifactsDirectory, _artifactsDirectory);
            history.RecordPassed("Some.Assembly", duration);

            var reloaded = LocalTestTimingHistory.Load(_artifactsDirectory, _artifactsDirectory);
            var previous = reloaded.TryGetPreviousDuration("Some.Assembly");

            Assert.NotNull(previous);
            Assert.Equal(duration, previous.Value);
        }

        [Fact]
        public void RecordPassed_Twice_LastWriteWinsForThatKey_OthersUnaffected()
        {
            var history = LocalTestTimingHistory.Load(_artifactsDirectory, _artifactsDirectory);
            history.RecordPassed("A", TimeSpan.FromSeconds(1));
            history.RecordPassed("B", TimeSpan.FromSeconds(2));
            history.RecordPassed("A", TimeSpan.FromSeconds(9));

            var reloaded = LocalTestTimingHistory.Load(_artifactsDirectory, _artifactsDirectory);
            Assert.Equal(TimeSpan.FromSeconds(9), reloaded.TryGetPreviousDuration("A"));
            Assert.Equal(TimeSpan.FromSeconds(2), reloaded.TryGetPreviousDuration("B"));
        }

        [Fact]
        public void HistoryFile_IsWrittenOutsideArtifactsDirectory()
        {
            var history = LocalTestTimingHistory.Load(_artifactsDirectory, _artifactsDirectory);
            history.RecordPassed("Some.Assembly", TimeSpan.FromSeconds(1));

            var expectedPath = Path.Combine(_tempRoot, ".test-timings.json");
            Assert.True(File.Exists(expectedPath));
            Assert.False(File.Exists(Path.Combine(_artifactsDirectory, ".test-timings.json")));
        }

        [Fact]
        public void RepoRootIsResolvedFromBinaryLocation_NotFromAnOverriddenArtifactsPathsParent()
        {
            // Simulates `--artifactspath` pointing somewhere entirely outside the checkout (e.g. a Helix
            // work item's own scratch directory) -- the history file must still land next to the real
            // checkout's `artifacts` directory (found by walking up from where RunTests itself is running
            // from), not next to whatever unrelated directory --artifactspath happened to name.
            var elsewhereRoot = Directory.CreateTempSubdirectory(nameof(LocalTestTimingHistoryTests) + "Elsewhere").FullName;
            try
            {
                var overriddenArtifactsPath = Path.Combine(elsewhereRoot, "some-scratch-dir");
                Directory.CreateDirectory(overriddenArtifactsPath);

                var history = LocalTestTimingHistory.Load(overriddenArtifactsPath, _artifactsDirectory);
                history.RecordPassed("Some.Assembly", TimeSpan.FromSeconds(1));

                Assert.True(File.Exists(Path.Combine(_tempRoot, ".test-timings.json")));
                Assert.False(File.Exists(Path.Combine(elsewhereRoot, ".test-timings.json")));
            }
            finally
            {
                Directory.Delete(elsewhereRoot, recursive: true);
            }
        }

        [Fact]
        public void CorruptHistoryFile_LoadsEmptyRatherThanThrowing()
        {
            File.WriteAllText(Path.Combine(_tempRoot, ".test-timings.json"), "not valid json{{{");

            var history = LocalTestTimingHistory.Load(_artifactsDirectory, _artifactsDirectory);
            Assert.Null(history.TryGetPreviousDuration("Some.Assembly"));
        }

        [Fact]
        public void RecordPassed_MergesWithConcurrentWriteFromAnotherInstance_NeitherKeyIsLost()
        {
            // Simulates two concurrent `scry` processes (e.g. separate --core/--framework legs): each loads
            // its own independent snapshot up front, so neither ever sees the other's key in memory -- only
            // RecordPassed's re-read-and-merge immediately before writing can keep both alive on disk.
            var processA = LocalTestTimingHistory.Load(_artifactsDirectory, _artifactsDirectory);
            var processB = LocalTestTimingHistory.Load(_artifactsDirectory, _artifactsDirectory);

            processA.RecordPassed("A", TimeSpan.FromSeconds(1));
            processB.RecordPassed("B", TimeSpan.FromSeconds(2));

            var reloaded = LocalTestTimingHistory.Load(_artifactsDirectory, _artifactsDirectory);
            Assert.Equal(TimeSpan.FromSeconds(1), reloaded.TryGetPreviousDuration("A"));
            Assert.Equal(TimeSpan.FromSeconds(2), reloaded.TryGetPreviousDuration("B"));
        }
    }
}
