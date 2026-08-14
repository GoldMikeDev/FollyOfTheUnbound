// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RunTests
{
    internal record struct WorkItemInfo(ImmutableSortedDictionary<AssemblyInfo, ImmutableArray<TestMethodInfo>> Filters, int PartitionIndex)
    {
        internal readonly string DisplayName
        {
            get
            {
                var assembliesString = string.Join("_", Filters.Keys.Select(a => Path.GetFileNameWithoutExtension(a.AssemblyName)));

                // Currently some helix APIs don't work when the work item friendly name is more than 200 characters.
                // Until that is fixed we manually truncate the name ourselves to a reasonable limit.
                // https://github.com/dotnet/arcade/issues/11079
                assembliesString = assembliesString.Length > 150 ? $"{assembliesString[..150]}..." : assembliesString;
                return $"{assembliesString}_{PartitionIndex}";
            }
        }
    }

    internal readonly struct RunAllResult
    {
        internal bool Succeeded { get; }
        internal ImmutableArray<TestResult> TestResults { get; }
        internal ImmutableArray<ProcessResult> ProcessResults { get; }

        internal RunAllResult(bool succeeded, ImmutableArray<TestResult> testResults, ImmutableArray<ProcessResult> processResults)
        {
            Succeeded = succeeded;
            TestResults = testResults;
            ProcessResults = processResults;
        }
    }

    internal sealed class TestRunner
    {
        private readonly ProcessTestExecutor _testExecutor;
        private readonly Options _options;

        internal TestRunner(Options options, ProcessTestExecutor testExecutor)
        {
            _testExecutor = testExecutor;
            _options = options;
        }

        private static ImmutableArray<WorkItemInfo> CreateWorkItemsForFullAssemblies(ImmutableArray<AssemblyInfo> assemblies)
        {
            var workItems = new List<WorkItemInfo>();
            var partitionIndex = 0;
            foreach (var assembly in assemblies)
            {
                var currentWorkItem = ImmutableSortedDictionary<AssemblyInfo, ImmutableArray<TestMethodInfo>>.Empty.Add(assembly, ImmutableArray<TestMethodInfo>.Empty);
                workItems.Add(new WorkItemInfo(currentWorkItem, partitionIndex++));
            }

            return workItems.ToImmutableArray();
        }

        internal async Task<RunAllResult> RunAllAsync(ImmutableArray<AssemblyInfo> assemblies, CancellationToken cancellationToken)
        {
            // Use 1.5 times the number of processors for unit tests, but only 1 processor for the open integration tests
            // since they perform actual UI operations (such as mouse clicks and sending keystrokes) and we don't want two
            // tests to conflict with one-another.
            var max = _options.Sequential ? 1 : (int)(Environment.ProcessorCount * 1.5);
            var workItems = CreateWorkItemsForFullAssemblies(assemblies);
            var waiting = new Stack<WorkItemInfo>(workItems);
            var running = new List<(WorkItemInfo WorkItem, Task<TestResult> Task)>();
            var completed = new List<TestResult>();
            var failures = 0;

            var runLabel = $"{_options.Configuration} ({_options.TestRuntime})";
            var liveDisplay = LiveTestProgressDisplay.TryCreate(runLabel, workItems);

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var i = 0;
                while (i < running.Count)
                {
                    var (workItem, task) = running[i];
                    if (task.IsCompleted)
                    {
                        try
                        {
                            var testResult = await task.ConfigureAwait(false);
                            liveDisplay?.MarkCompleted(workItem, testResult.Elapsed, testResult.Succeeded, testResult.IsTimeout);

                            if (!testResult.Succeeded)
                            {
                                failures++;
                                liveDisplay?.PrepareForExtraOutput();

                                // Printed here (rather than from inside ProcessTestExecutor.RunTestAsync, where
                                // it's detected) so it lands after the PrepareForExtraOutput call above -- that
                                // executor code can run concurrently with other still-running work items, and
                                // printing directly from there would race the live table's own redraws.
                                if (testResult.CrashDiagnostics.ErrorMessage is string crashErrorMessage)
                                {
                                    ConsoleUtil.Error(crashErrorMessage);
                                    foreach (var dump in testResult.CrashDiagnostics.DumpPaths)
                                    {
                                        ConsoleUtil.WriteLine(ConsoleColor.Red, $"  Dump: {dump}");
                                    }
                                }

                                if (testResult.ResultsDisplayFilePath is string resultsPath)
                                {
                                    ConsoleUtil.WriteLine(ConsoleColor.Red, resultsPath);
                                }
                                else
                                {
                                    foreach (var result in testResult.ProcessResults)
                                    {
                                        foreach (var line in result.ErrorLines)
                                        {
                                            ConsoleUtil.WriteLine(ConsoleColor.Red, line);
                                        }
                                    }
                                }
                            }

                            completed.Add(testResult);
                        }
                        catch (Exception ex)
                        {
                            // The work item never got a normal completion (e.g. the response file or test process
                            // itself couldn't be created), so it's still marked RUNNING in the live table with an
                            // ever-climbing timer unless explicitly resolved here.
                            liveDisplay?.MarkFailed(workItem);
                            liveDisplay?.PrepareForExtraOutput();
                            ConsoleUtil.WriteLine(ConsoleColor.Red, $"Error: {ex.Message}");
                            failures++;
                        }

                        running.RemoveAt(i);
                    }
                    else
                    {
                        i++;
                    }
                }

                while (running.Count < max && waiting.Count > 0)
                {
                    var workItem = waiting.Pop();
                    liveDisplay?.MarkRunning(workItem);
                    var task = _testExecutor.RunTestAsync(workItem, _options, cancellationToken);
                    running.Add((workItem, task));
                }

                if (liveDisplay is not null)
                {
                    liveDisplay.Redraw();
                }
                else
                {
                    // Display the current status of the TestRunner.
                    // Note: The { ... , 2 } is to right align the values, thus aligns sections into columns.
                    ConsoleUtil.Write($"  {running.Count,2} running, {waiting.Count,2} queued, {completed.Count,2} completed");
                    if (failures > 0)
                    {
                        ConsoleUtil.Write($", {failures,2} failures");
                    }
                    ConsoleUtil.WriteLine();
                }

                if (running.Count > 0)
                {
                    if (liveDisplay is not null)
                    {
                        // Wake at least once a second even if nothing completes, purely so the live table's
                        // elapsed-time column visibly ticks for still-running rows; CI's line-based fallback above
                        // has no such need and keeps its original wake-only-on-completion behavior (waking on a
                        // timer there would just spam the log with an unchanged line every second).
                        var tasks = running.Select(static r => (Task)r.Task).Append(Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
                        await Task.WhenAny(tasks);
                    }
                    else
                    {
                        await Task.WhenAny(running.Select(static r => r.Task).ToArray());
                    }
                }
            } while (running.Count > 0);

            Print(completed);

            var processResults = ImmutableArray.CreateBuilder<ProcessResult>();
            foreach (var c in completed)
            {
                processResults.AddRange(c.ProcessResults);
            }

            return new RunAllResult((failures == 0), completed.ToImmutableArray(), processResults.ToImmutable());
        }

        /// <summary>
        /// Name column width for the final summary table below. Unlike <see cref="LiveTestProgressDisplay"/>'s
        /// live table, this isn't derived from <see cref="Console.WindowWidth"/> -- this summary is always
        /// printed (including to the log file in CI, where there's no real terminal width to read), so it needs
        /// a width that's sensible on its own. A name longer than this is truncated (see
        /// <see cref="TestResultDisplay.FitName"/>) rather than left to grow and push the Status/Elapsed columns
        /// out of alignment for every row after it, which is what the previous plain <c>{DisplayName,-75}</c>
        /// padding did for any work item name past 75 characters.
        /// </summary>
        private const int SummaryNameColumnWidth = 75;

        private void Print(List<TestResult> testResults)
        {
            testResults.Sort((x, y) => x.Elapsed.CompareTo(y.Elapsed));

            foreach (var testResult in testResults.Where(x => !x.Succeeded))
            {
                PrintFailedTestResult(testResult);
            }

            ConsoleUtil.WriteLine("================");
            var line = new StringBuilder();
            foreach (var testResult in testResults)
            {
                line.Length = 0;
                var color = testResult.Succeeded ? Console.ForegroundColor : ConsoleColor.Red;
                line.Append(TestResultDisplay.FitName(testResult.DisplayName, SummaryNameColumnWidth));
                line.Append(' ');
                line.Append(TestResultDisplay.GetStatusText(testResult.Succeeded, testResult.IsTimeout).PadRight(TestResultDisplay.StatusColumnWidth));
                line.Append(' ');
                line.Append(TestResultDisplay.FormatElapsed(testResult.Elapsed).PadLeft(TestResultDisplay.ElapsedColumnWidth));
                line.Append(' ');
                line.Append(!string.IsNullOrEmpty(testResult.Diagnostics) ? "?" : "");

                var message = line.ToString();
                ConsoleUtil.WriteLine(color, message);
            }
            ConsoleUtil.WriteLine("================");

            // Print diagnostics out last so they are cleanly visible at the end of the test summary
            ConsoleUtil.WriteLine("Extra run diagnostics for logging, did not impact run results");
            foreach (var testResult in testResults.Where(x => !string.IsNullOrEmpty(x.Diagnostics)))
            {
                ConsoleUtil.WriteLine(testResult.Diagnostics!);
            }
        }

        private void PrintFailedTestResult(TestResult testResult)
        {
            // Save out the error output for easy artifact inspecting
            var outputLogPath = Path.Combine(_options.LogFilesDirectory, $"xUnitFailure-{testResult.DisplayName}.log");

            ConsoleUtil.WriteLine($"Errors {testResult.DisplayName}");
            ConsoleUtil.WriteLine(testResult.ErrorOutput);

            // TODO: Put this in the log and take it off the ConsoleUtil output to keep it simple?
            ConsoleUtil.WriteLine($"Command: {testResult.CommandLine}");
            ConsoleUtil.WriteLine($"xUnit output log: {outputLogPath}");

            File.WriteAllText(outputLogPath, testResult.StandardOutput ?? "");

            if (!string.IsNullOrEmpty(testResult.ErrorOutput))
            {
                ConsoleUtil.WriteLine(testResult.ErrorOutput);
            }
            else
            {
                ConsoleUtil.WriteLine($"xunit produced no error output but had exit code {testResult.ExitCode}. Writing standard output:");
                ConsoleUtil.WriteLine(testResult.StandardOutput ?? "(no standard output)");
            }

            // If the results are html, use Process.Start to open in the browser.
            var htmlResultsFilePath = testResult.TestResultInfo.HtmlResultsFilePath;
            if (!string.IsNullOrEmpty(htmlResultsFilePath))
            {
                var startInfo = new ProcessStartInfo() { FileName = htmlResultsFilePath, UseShellExecute = true };
                Process.Start(startInfo);
            }
        }
    }
}
