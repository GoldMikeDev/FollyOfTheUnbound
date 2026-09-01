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
        public void GetKey_NullTfmTag_IsJustTheBaseName()
        {
            Assert.Equal("Some.Assembly", LocalTestTimingHistory.GetKey("Some.Assembly", null));
        }

        [Fact]
        public void GetKey_WithTfmTag_DisambiguatesByTfm()
        {
            var net10 = LocalTestTimingHistory.GetKey("Some.Assembly", "net10.0");
            var net472 = LocalTestTimingHistory.GetKey("Some.Assembly", "net472");

            Assert.NotEqual(net10, net472);
        }

        [Fact]
        public void FreshHistory_NoFileYet_ReturnsNullForAnyKey()
        {
            var history = LocalTestTimingHistory.Load(_artifactsDirectory);
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

            var history = LocalTestTimingHistory.Load(_artifactsDirectory);
            history.RecordPassed("Some.Assembly", duration);

            var reloaded = LocalTestTimingHistory.Load(_artifactsDirectory);
            var previous = reloaded.TryGetPreviousDuration("Some.Assembly");

            Assert.NotNull(previous);
            Assert.Equal(duration, previous.Value);
        }

        [Fact]
        public void RecordPassed_Twice_LastWriteWinsForThatKey_OthersUnaffected()
        {
            var history = LocalTestTimingHistory.Load(_artifactsDirectory);
            history.RecordPassed("A", TimeSpan.FromSeconds(1));
            history.RecordPassed("B", TimeSpan.FromSeconds(2));
            history.RecordPassed("A", TimeSpan.FromSeconds(9));

            var reloaded = LocalTestTimingHistory.Load(_artifactsDirectory);
            Assert.Equal(TimeSpan.FromSeconds(9), reloaded.TryGetPreviousDuration("A"));
            Assert.Equal(TimeSpan.FromSeconds(2), reloaded.TryGetPreviousDuration("B"));
        }

        [Fact]
        public void HistoryFile_IsWrittenOutsideArtifactsDirectory()
        {
            var history = LocalTestTimingHistory.Load(_artifactsDirectory);
            history.RecordPassed("Some.Assembly", TimeSpan.FromSeconds(1));

            var expectedPath = Path.Combine(_tempRoot, ".test-timings.json");
            Assert.True(File.Exists(expectedPath));
            Assert.False(File.Exists(Path.Combine(_artifactsDirectory, ".test-timings.json")));
        }

        [Fact]
        public void CorruptHistoryFile_LoadsEmptyRatherThanThrowing()
        {
            File.WriteAllText(Path.Combine(_tempRoot, ".test-timings.json"), "not valid json{{{");

            var history = LocalTestTimingHistory.Load(_artifactsDirectory);
            Assert.Null(history.TryGetPreviousDuration("Some.Assembly"));
        }
    }
}
