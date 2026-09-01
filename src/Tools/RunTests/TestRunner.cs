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
            // Leave one processor free for the rest of the system (including the console itself, so the live
            // progress table's redraws stay responsive instead of getting starved by CPU-saturated test
            // processes), but only 1 processor for the open integration tests since they perform actual UI
            // operations (such as mouse clicks and sending keystrokes) and we don't want two tests to conflict
            // with one-another.
            //
            // Environment.ProcessorCount includes logical processors Windows currently has parked (idle,
            // dynamically taken out of scheduling -- e.g. on a hybrid CPU this can mean a whole
            // efficiency-class tier stays parked essentially permanently, as with Arrow Lake-H's "Low
            // Power Island" E-cores), so a work item can still be scheduled onto one and stall there.
            // Asking Windows which cores are parked right now (rather than assuming a fixed tier) and
            // excluding just those sidesteps that; on non-Windows, or if nothing is reported parked, this
            // is always 0 and the count is unchanged.
            var availableProcessorCount = Environment.ProcessorCount - ProcessorTopology.GetParkedLogicalProcessorCount();
            var max = _options.Sequential ? 1 : Math.Max(availableProcessorCount - 1, 1);
            var workItems = CreateWorkItemsForFullAssemblies(assemblies);
            var waiting = new Stack<WorkItemInfo>(workItems);
            var running = new List<(WorkItemInfo WorkItem, Task<TestResult> Task)>();
            var completed = new List<TestResult>();
            var failures = 0;

            var runLabel = $"{_options.Configuration} ({_options.TestRuntime})";
            var liveDisplay = LiveTestProgressDisplay.TryCreate(runLabel, workItems);

            try
            {
                await RunLoopAsync();
            }
            finally
            {
                // Must run even on cancellation/an unhandled exception -- otherwise the terminal is left stuck
                // showing the table's last static frame in the alternate screen buffer indefinitely, with the
                // user's real prompt and scrollback never coming back.
                liveDisplay?.Complete();
            }

            Print(completed);

            var processResults = ImmutableArray.CreateBuilder<ProcessResult>();
            foreach (var c in completed)
            {
                processResults.AddRange(c.ProcessResults);
            }

            return new RunAllResult((failures == 0), completed.ToImmutableArray(), processResults.ToImmutable());

            async Task RunLoopAsync()
            {
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
            }
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
            testResults.Sort((x, y) => string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase));

            foreach (var testResult in testResults.Where(x => !x.Succeeded))
            {
                PrintFailedTestResult(testResult);
            }

            WriteSummaryLine("================");
            var line = new StringBuilder();
            foreach (var testResult in testResults)
            {
                line.Length = 0;
                var color = testResult.Succeeded ? ConsoleColor.Green : ConsoleColor.Red;
                line.Append(TestResultDisplay.FitName(testResult.DisplayName, SummaryNameColumnWidth));
                line.Append(' ');
                line.Append(TestResultDisplay.CenterPad(TestResultDisplay.GetStatusText(testResult.Succeeded, testResult.IsTimeout), TestResultDisplay.StatusColumnWidth));
                line.Append(' ');
                line.Append(TestResultDisplay.CenterPad(TestResultDisplay.FormatElapsed(testResult.Elapsed), TestResultDisplay.ElapsedColumnWidth));
                line.Append(' ');
                line.Append(!string.IsNullOrEmpty(testResult.Diagnostics) ? "?" : "");

                var message = line.ToString();
                WriteSummaryLineColored(color, message);
            }
            WriteSummaryLine("================");

            // Print diagnostics out last so they are cleanly visible at the end of the test summary
            WriteSummaryLine("Extra run diagnostics for logging, did not impact run results");
            foreach (var testResult in testResults.Where(x => !string.IsNullOrEmpty(x.Diagnostics)))
            {
                WriteSummaryLine(testResult.Diagnostics!);
            }

            // Unlike PrintFailedTestResult's live per-failure dumps above (real-time diagnostics useful the
            // moment a failure happens, never duplicated anywhere else), this final table is also exactly
            // what a caller running multiple TestRunner passes back-to-back (see folly.ps1 scry, which runs
            // one TestRunner per --core/--framework leg and combines both legs' tables into one summary of
            // its own, read back from each leg's already-written log file) may want to build its own combined
            // presentation from instead of seeing this pass's copy printed to the console separately. This
            // never affects what's logged -- only whether this table specifically also goes to the console.
            void WriteSummaryLine(string message)
            {
                if (_options.SuppressConsoleSummary)
                {
                    Logger.Log(message);
                }
                else
                {
                    ConsoleUtil.WriteLine(message);
                }
            }

            void WriteSummaryLineColored(ConsoleColor color, string message)
            {
                if (_options.SuppressConsoleSummary)
                {
                    Logger.Log(message);
                }
                else
                {
                    ConsoleUtil.WriteLine(color, message);
                }
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

            // Nothing else creates this directory before results start coming in (Program.WriteLogFile only does
            // so after the whole run finishes), so without this a failing test host crash before then throws
            // DirectoryNotFoundException here and takes down the entire run with an unhandled exception, masking
            // the actual crash that was being reported.
            Directory.CreateDirectory(_options.LogFilesDirectory);
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
