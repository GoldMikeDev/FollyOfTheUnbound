// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;

namespace RunTests.UnitTests
{
    // Exercises TestRunner.Print directly (via reflection, since it's private) against the real
    // static Logger/ConsoleUtil, rather than only the folly.ps1/.sh forwarding tests -- those only
    // prove the switch reaches RunTests, not that Options.SuppressConsoleSummary actually changes
    // what TestRunner.Print sends to the console vs. the log.
    public sealed class TestRunnerPrintTests
    {
        private static List<TestResult> CreateOnePassingResult()
        {
            var assembly = new AssemblyInfo("Fake.UnitTests.dll");
            var filters = ImmutableSortedDictionary<AssemblyInfo, ImmutableArray<TestMethodInfo>>.Empty
                .Add(assembly, ImmutableArray<TestMethodInfo>.Empty);
            var workItem = new WorkItemInfo(filters, PartitionIndex: 0);
            var resultInfo = new TestResultInfo(
                exitCode: 0,
                resultsFilePath: null,
                htmlResultsFilePath: null,
                elapsed: TimeSpan.FromSeconds(1),
                standardOutput: "",
                errorOutput: "",
                isTimeout: false,
                crashDiagnostics: CrashDiagnostics.None);
            var testResult = new TestResult(workItem, resultInfo, commandLine: "");
            return new List<TestResult> { testResult };
        }

        private static string InvokePrintAndCaptureConsole(bool suppressConsoleSummary, out string loggedText)
        {
            var options = new Options(
                dotnetFilePath: "dotnet",
                artifactsDirectory: "artifacts",
                configuration: "Debug",
                testResultsDirectory: "TestResults",
                logFilesDirectory: "log",
                architecture: "x64")
            {
                SuppressConsoleSummary = suppressConsoleSummary,
            };
            var testRunner = new TestRunner(options, new ProcessTestExecutor());
            var printMethod = typeof(TestRunner).GetMethod("Print", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(printMethod);

            Logger.Clear();
            var consoleWriter = new StringWriter();
            var savedOut = Console.Out;
            Console.SetOut(consoleWriter);
            try
            {
                printMethod!.Invoke(testRunner, new object[] { CreateOnePassingResult() });
            }
            finally
            {
                Console.SetOut(savedOut);
            }

            var loggerWriter = new StringWriter();
            Logger.WriteTo(loggerWriter);
            loggedText = loggerWriter.ToString();
            return consoleWriter.ToString();
        }

        [Fact]
        public void Print_SuppressConsoleSummaryFalse_WritesTableToConsoleAndLog()
        {
            var consoleText = InvokePrintAndCaptureConsole(suppressConsoleSummary: false, out var loggedText);

            Assert.Contains("================", consoleText);
            Assert.Contains("Fake.UnitTests", consoleText);
            Assert.Contains("================", loggedText);
            Assert.Contains("Fake.UnitTests", loggedText);
        }

        [Fact]
        public void Print_SuppressConsoleSummaryTrue_OmitsTableFromConsoleButKeepsItInLog()
        {
            var consoleText = InvokePrintAndCaptureConsole(suppressConsoleSummary: true, out var loggedText);

            Assert.DoesNotContain("================", consoleText);
            Assert.DoesNotContain("Fake.UnitTests", consoleText);
            Assert.Contains("================", loggedText);
            Assert.Contains("Fake.UnitTests", loggedText);
        }
    }
}
