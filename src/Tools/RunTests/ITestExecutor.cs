// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;

namespace RunTests
{
    /// <summary>
    /// Console diagnostics <see cref="ProcessTestExecutor"/>'s crash/hang detection wants printed for a work
    /// item, deferred (rather than written directly from inside <c>RunTestAsync</c>, which can run concurrently
    /// with other work items and race an in-progress console redraw) so <see cref="TestRunner"/> can print them
    /// at the one point it already synchronizes extra console output with <see cref="LiveTestProgressDisplay"/>.
    /// </summary>
    internal readonly struct CrashDiagnostics
    {
        internal static readonly CrashDiagnostics None = new(errorMessage: null, ImmutableArray<string>.Empty);

        internal string? ErrorMessage { get; }
        internal ImmutableArray<string> DumpPaths { get; }

        internal CrashDiagnostics(string? errorMessage, ImmutableArray<string> dumpPaths)
        {
            ErrorMessage = errorMessage;
            DumpPaths = dumpPaths;
        }
    }

    internal readonly struct TestExecutionOptions
    {
        internal string DotnetFilePath { get; }
        internal string TestResultsDirectory { get; }
        internal string? TestFilter { get; }
        internal bool IncludeHtml { get; }
        internal bool Retry { get; }
        internal bool CollectDumps { get; }

        internal TestExecutionOptions(string dotnetFilePath, string testResultsDirectory, string? testFilter, bool includeHtml, bool retry, bool collectDumps)
        {
            DotnetFilePath = dotnetFilePath;
            TestResultsDirectory = testResultsDirectory;
            TestFilter = testFilter;
            IncludeHtml = includeHtml;
            Retry = retry;
            CollectDumps = collectDumps;
        }
    }

    /// <summary>
    /// The actual results from running the xunit tests.
    /// </summary>
    /// <remarks>
    /// The difference between <see cref="TestResultInfo"/>  and <see cref="TestResult"/> is the former 
    /// is specifically for the actual test execution results while the latter can contain extra metadata
    /// about the results.  For example whether it was cached, or had diagnostic, output, etc ...
    /// </remarks>
    internal readonly struct TestResultInfo
    {
        internal int ExitCode { get; }
        internal TimeSpan Elapsed { get; }
        internal string StandardOutput { get; }
        internal string ErrorOutput { get; }

        /// <summary>
        /// Path to the XML results file.
        /// </summary>
        internal string? ResultsFilePath { get; }

        /// <summary>
        /// Path to the HTML results file if HTML output is enabled, otherwise, <see langword="null"/>.
        /// </summary>
        internal string? HtmlResultsFilePath { get; }

        /// <summary>
        /// Whether vstest's <c>/Blame</c> hang detection collected a hang dump for this work item (see
        /// <see cref="ProcessTestExecutor"/>'s <c>CheckForCrashes</c>). Only ever true when dump collection is
        /// enabled and actually caught a hung test host -- a test killed by <c>RunTests</c>' own separate,
        /// whole-run <c>--timeout</c> watchdog has no such signal and is indistinguishable from an ordinary
        /// failure here.
        /// </summary>
        internal bool IsTimeout { get; }

        /// <summary>Console diagnostics from crash/hang detection, if any, for the caller to print. See <see cref="RunTests.CrashDiagnostics"/>.</summary>
        internal CrashDiagnostics CrashDiagnostics { get; }

        internal TestResultInfo(int exitCode, string? resultsFilePath, string? htmlResultsFilePath, TimeSpan elapsed, string standardOutput, string errorOutput, bool isTimeout, CrashDiagnostics crashDiagnostics)
        {
            ExitCode = exitCode;
            ResultsFilePath = resultsFilePath;
            HtmlResultsFilePath = htmlResultsFilePath;
            Elapsed = elapsed;
            StandardOutput = standardOutput;
            ErrorOutput = errorOutput;
            IsTimeout = isTimeout;
            CrashDiagnostics = crashDiagnostics;
        }
    }

    internal readonly struct TestResult
    {
        internal TestResultInfo TestResultInfo { get; }
        internal WorkItemInfo WorkItemInfo { get; }
        internal string CommandLine { get; }
        internal string? Diagnostics { get; }

        /// <summary>
        /// Collection of processes the runner explicitly ran to get the result.
        /// </summary>
        internal ImmutableArray<ProcessResult> ProcessResults { get; }

        internal string DisplayName => WorkItemInfo.DisplayName;
        internal bool Succeeded => ExitCode == 0;
        internal bool IsTimeout => TestResultInfo.IsTimeout;
        internal CrashDiagnostics CrashDiagnostics => TestResultInfo.CrashDiagnostics;
        internal int ExitCode => TestResultInfo.ExitCode;
        internal TimeSpan Elapsed => TestResultInfo.Elapsed;
        internal string StandardOutput => TestResultInfo.StandardOutput;
        internal string ErrorOutput => TestResultInfo.ErrorOutput;
        internal string? ResultsDisplayFilePath => TestResultInfo.HtmlResultsFilePath ?? TestResultInfo.ResultsFilePath;

        internal TestResult(WorkItemInfo workItemInfo, TestResultInfo testResultInfo, string commandLine, ImmutableArray<ProcessResult> processResults = default, string? diagnostics = null)
        {
            WorkItemInfo = workItemInfo;
            TestResultInfo = testResultInfo;
            CommandLine = commandLine;
            ProcessResults = processResults.IsDefault ? ImmutableArray<ProcessResult>.Empty : processResults;
            Diagnostics = diagnostics;
        }
    }
}
