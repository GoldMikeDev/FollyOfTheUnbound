// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Xunit;

namespace RunTests.UnitTests
{
    public sealed class TestResultDisplayTests
    {
        [Fact]
        public void FitName_ShorterThanWidth_PadsRight()
        {
            var result = TestResultDisplay.FitName("Foo", 10);
            Assert.Equal("Foo       ", result);
            Assert.Equal(10, result.Length);
        }

        [Fact]
        public void FitName_ExactlyWidth_ReturnedUnchanged()
        {
            var name = "ExactlyTen";
            Assert.Equal(10, name.Length);
            Assert.Equal(name, TestResultDisplay.FitName(name, 10));
        }

        [Fact]
        public void FitName_LongerThanWidth_TruncatesWithEllipsis()
        {
            var result = TestResultDisplay.FitName("Microsoft.CodeAnalysis.CSharp.Workspaces.UnitTests", 20);
            Assert.Equal(20, result.Length);
            Assert.EndsWith("…", result);
            Assert.StartsWith("Microsoft.CodeAnaly", result);
        }

        [Fact]
        public void FitName_LongNameWithPartitionSuffix_PreservesSuffix()
        {
            // Simulates two work items for the same >75-char assembly name under different target frameworks
            // (TestRuntime.Both), distinguished only by their trailing Helix partition suffix.
            var baseName = new string('X', 80);
            var name42 = $"{baseName}_42";
            var name43 = $"{baseName}_43";

            var result42 = TestResultDisplay.FitName(name42, 75);
            var result43 = TestResultDisplay.FitName(name43, 75);

            Assert.EndsWith("_42", result42);
            Assert.EndsWith("_43", result43);
            Assert.NotEqual(result42, result43);
            Assert.Equal(75, result42.Length);
            Assert.Equal(75, result43.Length);
        }

        [Fact]
        public void FitName_NoPartitionSuffix_TruncatesPlainly()
        {
            // A trailing "_<digits>" is only treated as a preserve-worthy suffix when the whole tail after the
            // last underscore is digits; this name's tail isn't, so it should just truncate normally.
            var name = new string('X', 80) + "_NotANumber";
            var result = TestResultDisplay.FitName(name, 20);
            Assert.Equal(20, result.Length);
            Assert.EndsWith("…", result);
        }

        [Theory]
        [InlineData(0, 0, 30, "00:30")]
        [InlineData(0, 5, 0, "05:00")]
        [InlineData(0, 59, 59, "59:59")]
        [InlineData(1, 0, 0, "01:00:00")]
        [InlineData(10, 0, 0, "10:00:00")]
        [InlineData(23, 59, 59, "23:59:59")]
        public void FormatElapsed_FormatsAsExpected(int hours, int minutes, int seconds, string expected)
        {
            var elapsed = new TimeSpan(hours, minutes, seconds);
            Assert.Equal(expected, TestResultDisplay.FormatElapsed(elapsed));
        }

        [Fact]
        public void FormatElapsed_Negative_ClampsToZero()
        {
            Assert.Equal("00:00", TestResultDisplay.FormatElapsed(TimeSpan.FromSeconds(-5)));
        }

        [Fact]
        public void FormatElapsed_PathologicallyLongRun_CapsAtNinetyNineHoursRatherThanOverflowingTheColumn()
        {
            var elapsed = TimeSpan.FromHours(150);
            var result = TestResultDisplay.FormatElapsed(elapsed);
            Assert.Equal("99:00:00", result);
            Assert.True(result.Length <= TestResultDisplay.ElapsedColumnWidth);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(45)]
        [InlineData(59 * 60 + 59)]
        [InlineData(60 * 60)]
        [InlineData(99 * 60 * 60 + 59 * 60 + 59)]
        [InlineData(1000 * 60 * 60)]
        public void FormatElapsed_NeverExceedsColumnWidth(int totalSeconds)
        {
            var result = TestResultDisplay.FormatElapsed(TimeSpan.FromSeconds(totalSeconds));
            Assert.True(result.Length <= TestResultDisplay.ElapsedColumnWidth, $"'{result}' exceeded {TestResultDisplay.ElapsedColumnWidth} characters");
        }

        [Fact]
        public void GetStatusText_Succeeded_ReturnsPassed()
        {
            Assert.Equal("PASSED", TestResultDisplay.GetStatusText(succeeded: true, isTimeout: false));
        }

        [Fact]
        public void GetStatusText_FailedNotTimeout_ReturnsFailed()
        {
            Assert.Equal("FAILED", TestResultDisplay.GetStatusText(succeeded: false, isTimeout: false));
        }

        [Fact]
        public void GetStatusText_Timeout_ReturnsTimeout()
        {
            Assert.Equal("TIMEOUT", TestResultDisplay.GetStatusText(succeeded: false, isTimeout: true));
        }
    }
}
