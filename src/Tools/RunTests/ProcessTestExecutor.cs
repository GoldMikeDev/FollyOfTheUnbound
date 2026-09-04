// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace RunTests
{
    internal sealed class ProcessTestExecutor
    {
        public static string BuildRspFileContents(WorkItemInfo workItem, Options options, string xmlResultsFilePath, string? htmlResultsFilePath)
        {
            var fileContentsBuilder = new StringBuilder();

            // Add each assembly we want to test on a new line.
            var assemblyPaths = workItem.Filters.Keys.Select(assembly => assembly.AssemblyPath);
            foreach (var path in assemblyPaths)
            {
                fileContentsBuilder.AppendLine($"\"{path}\"");
            }

            fileContentsBuilder.AppendLine($@"/Platform:{options.Architecture}");
            fileContentsBuilder.AppendLine($@"/Logger:xunit;LogFilePath={xmlResultsFilePath}");
            if (htmlResultsFilePath != null)
            {
                fileContentsBuilder.AppendLine($@"/Logger:html;LogFileName={htmlResultsFilePath}");
            }

            // Always use CollectDump and CollectHangDump to ensure we get actionable data on both
            // crashes and hangs, matching the Helix configuration.
            var blameOption = "CollectDump;CollectHangDump";

            // The 25 minute timeout in integration tests accounts for the fact that VSIX deployment and/or experimental hive reset and
            // configuration can take significant time (seems to vary from ~10 seconds to ~15 minutes), and the blame
            // functionality cannot separate this configuration overhead from the first test which will eventually run.
            // https://github.com/dotnet/roslyn/issues/59851
            //
            // Helix timeout is 15 minutes as helix jobs fully timeout in 30minutes.  So in order to capture dumps we need the timeout
            // to be 2x shorter than the expected test run time (15min) in case only the last test hangs.
            var timeout = options.UseHelix ? "15minutes" : "25minutes";
            fileContentsBuilder.AppendLine($"/Blame:{blameOption};TestTimeout={timeout};DumpType=full");

            // Specifies the results directory - this is where dumps from the blame options will get published.
            // A per-work-item subdirectory (not the shared options.TestResultsDirectory) so CheckForCrashes can
            // scope its dump scan to exactly this work item's own output -- a timestamp-only filter still can't
            // rule out a dump from a work item that happens to be running concurrently, since dumps aren't
            // otherwise correlated with which work item produced them.
            fileContentsBuilder.AppendLine($"/ResultsDirectory:{GetDumpResultsDirectory(workItem, options)}");

            // Build the filter string
            var filterStringBuilder = new StringBuilder();
            var filters = workItem.Filters.Values.SelectMany(filter => filter).Where(filter => !string.IsNullOrEmpty(filter.FullyQualifiedName)).ToImmutableArray();

            if (filters.Length > 0 || !string.IsNullOrWhiteSpace(options.TestFilter))
            {
                filterStringBuilder.Append("/TestCaseFilter:\"");
                var any = false;
                foreach (var filter in filters)
                {
                    MaybeAddSeparator();
                    filterStringBuilder.Append($"FullyQualifiedName={filter.FullyQualifiedName}");
                }

                if (options.TestFilter is not null)
                {
                    MaybeAddSeparator();
                    filterStringBuilder.Append(options.TestFilter);
                }

                filterStringBuilder.Append('"');

                void MaybeAddSeparator(char separator = '|')
                {
                    if (any)
                    {
                        filterStringBuilder.Append(separator);
                    }

                    any = true;
                }
            }

            fileContentsBuilder.AppendLine(filterStringBuilder.ToString());
            return fileContentsBuilder.ToString();
        }

        private static string GetVsTestConsolePath(string dotnetPath)
        {
            var dotnetDir = Path.GetDirectoryName(dotnetPath)!;
            var sdkDir = Path.Combine(dotnetDir, "sdk");
            var vsTestConsolePath = Directory.EnumerateFiles(sdkDir, "vstest.console.dll", SearchOption.AllDirectories).Last();
            return vsTestConsolePath;
        }

        public static string GetResultsFilePath(WorkItemInfo workItemInfo, Options options, string suffix = "xml")
        {
            var fileName = $"WorkItem_{workItemInfo.PartitionIndex}_{options.Architecture}_test_results.{suffix}";
            return Path.Combine(options.TestResultsDirectory, fileName);
        }

        /// <summary>
        /// Where this work item's vstest <c>/Blame</c> dumps (and their sequence files) get published --
        /// isolated per work item so <see cref="CheckForCrashes"/> can scope its scan to exactly this work
        /// item's own output, rather than the shared <see cref="Options.TestResultsDirectory"/> where a dump
        /// from a concurrently-running or earlier work item could otherwise get misattributed.
        /// </summary>
        public static string GetDumpResultsDirectory(WorkItemInfo workItemInfo, Options options)
            => Path.Combine(options.TestResultsDirectory, "dumps", workItemInfo.PartitionIndex.ToString());

        public async Task<TestResult> RunTestAsync(WorkItemInfo workItemInfo, Options options, CancellationToken cancellationToken)
        {
            try
            {
                var resultsFilePath = GetResultsFilePath(workItemInfo, options);
                var htmlResultsFilePath = options.IncludeHtml ? GetResultsFilePath(workItemInfo, options, "html") : null;
                var rspFileContents = BuildRspFileContents(workItemInfo, options, resultsFilePath, htmlResultsFilePath);
                var rspFilePath = Path.Combine(getRspDirectory(), $"vstest_{workItemInfo.PartitionIndex}.rsp");
                File.WriteAllText(rspFilePath, rspFileContents);

                var vsTestConsolePath = GetVsTestConsolePath(options.DotnetFilePath);

                var commandLineArguments = $"exec \"{vsTestConsolePath}\" @\"{rspFilePath}\"";

                var resultsDir = Path.GetDirectoryName(resultsFilePath);
                var processResultList = new List<ProcessResult>();

                // NOTE: xUnit doesn't always create the log directory
                Directory.CreateDirectory(resultsDir!);
                Directory.CreateDirectory(GetDumpResultsDirectory(workItemInfo, options));

                // Define environment variables for processes started via ProcessRunner.
                var environmentVariables = new Dictionary<string, string>();

                // vstest's own /Blame:CollectDump;CollectHangDump (set below in BuildRspFileContents)
                // needs to locate procdump.exe itself, via the PROCDUMP_PATH environment variable --
                // see Microsoft.TestPlatform.Extensions.BlameDataCollector's own "Required environment
                // variable PROCDUMP_PATH was null or empty" message. Options.ProcDumpFilePath is
                // resolved by Ensure-ProcDump in eng/build.{ps1,sh} for exactly this purpose, but was
                // previously only ever surfaced in the "Proc dump location:" console line -- never
                // actually threaded into the vstest child process's environment, so the Blame collector
                // could never see it even when Ensure-ProcDump found a real procdump.exe.
                if (options.CollectDumps && options.ProcDumpFilePath is { } procDumpFilePath)
                {
                    environmentVariables["PROCDUMP_PATH"] = procDumpFilePath;
                }

                // NOTE: xUnit seems to have an occasional issue creating logs create
                // an empty log just in case, so our runner will still fail.
                File.Create(resultsFilePath).Close();

                var start = DateTime.UtcNow;
                var dotnetProcessInfo = ProcessRunner.CreateProcess(
                    ProcessRunner.CreateProcessStartInfo(
                        options.DotnetFilePath,
                        commandLineArguments,
                        displayWindow: false,
                        captureOutput: true,
                        environmentVariables: environmentVariables),
                    lowPriority: false,
                    cancellationToken: cancellationToken);
                Logger.Log($"Create xunit process with id {dotnetProcessInfo.Id} for test {workItemInfo.DisplayName}");

                var xunitProcessResult = await dotnetProcessInfo.Result;
                var span = DateTime.UtcNow - start;

                Logger.Log($"Exit xunit process with id {dotnetProcessInfo.Id} for test {workItemInfo.DisplayName} with code {xunitProcessResult.ExitCode}");
                processResultList.Add(xunitProcessResult);

                if (xunitProcessResult.ExitCode != 0)
                {
                    // On occasion we get a non-0 output but no actual data in the result file.  The could happen
                    // if xunit manages to crash when running a unit test (a stack overflow could cause this, for instance).
                    // To avoid losing information, write the process output to the console.  In addition, delete the results
                    // file to avoid issues with any tool attempting to interpret the (potentially malformed) text.
                    var resultData = string.Empty;
                    try
                    {
                        resultData = File.ReadAllText(resultsFilePath).Trim();
                    }
                    catch
                    {
                        // Happens if xunit didn't produce a log file
                    }

                    if (resultData.Length == 0)
                    {
                        // Delete the output file.
                        File.Delete(resultsFilePath);
                        resultsFilePath = null;
                        htmlResultsFilePath = null;
                    }
                }

                Logger.Log($"Command line {workItemInfo.DisplayName} completed in {span.TotalSeconds} seconds: {options.DotnetFilePath} {commandLineArguments}");
                var standardOutput = string.Join(Environment.NewLine, xunitProcessResult.OutputLines) ?? "";
                var errorOutput = string.Join(Environment.NewLine, xunitProcessResult.ErrorLines) ?? "";

                var exitCode = xunitProcessResult.ExitCode;
                var isTimeout = false;
                var crashDiagnostics = CrashDiagnostics.None;
                if (exitCode != 0)
                {
                    (isTimeout, crashDiagnostics) = CheckForCrashes(
                        resultsFilePath,
                        workItemInfo.DisplayName,
                        dumpScanDirectory: GetDumpResultsDirectory(workItemInfo, options),
                        artifactsDirectory: options.TestResultsDirectory,
                        sinceUtc: start);
                }

                var testResultInfo = new TestResultInfo(
                    exitCode: exitCode,
                    resultsFilePath: resultsFilePath,
                    htmlResultsFilePath: htmlResultsFilePath,
                    elapsed: span,
                    standardOutput: standardOutput,
                    errorOutput: errorOutput,
                    isTimeout: isTimeout,
                    crashDiagnostics: crashDiagnostics);

                return new TestResult(
                    workItemInfo,
                    testResultInfo,
                    commandLineArguments,
                    processResults: ImmutableArray.CreateRange(processResultList));

                string getRspDirectory()
                {
                    // There is no artifacts directory on Helix, just use the current directory
                    if (options.UseHelix)
                    {
                        return Directory.GetCurrentDirectory();
                    }

                    var dirPath = Path.Combine(options.ArtifactsDirectory, "tmp", options.Configuration, "vstest-rsp");
                    Directory.CreateDirectory(dirPath);
                    return dirPath;
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Unable to run {workItemInfo.DisplayName} with {options.DotnetFilePath}. {ex}");
            }
        }

        /// <summary>
        /// When vstest detects a test host crash or hang (via the /Blame option), it collects
        /// a dump file but the test runner may not produce a clear failure in the xunit results
        /// XML. This means AzDO's PublishTestResults task won't surface the crash as a failed
        /// test. This method scans for dump files, builds the crash diagnostics for the caller to print, and
        /// writes a standalone synthetic xunit results XML so the failure is visible in AzDO.
        /// </summary>
        /// <param name="dumpScanDirectory">
        /// This work item's own <see cref="GetDumpResultsDirectory"/> -- vstest is pointed at this isolated
        /// per-work-item directory (rather than the shared <paramref name="artifactsDirectory"/>) via
        /// <c>/ResultsDirectory</c> in <see cref="BuildRspFileContents"/>, so a dump can only ever be found here
        /// if it belongs to this work item. A directory-wide scan can't offer that guarantee on its own: two
        /// work items can be running concurrently, so even a "created after this work item started" timestamp
        /// filter can't rule out attributing a dump to the wrong one of them.
        /// </param>
        /// <param name="sinceUtc">
        /// Secondary safety net: only dump files created at or after this time are attributed to this work item.
        /// Directory isolation alone doesn't rule out a stale dump left over in the same per-partition-index
        /// directory from an earlier <c>RunTests</c> invocation that was never cleaned up.
        /// </param>
        /// <returns>
        /// Whether a hang dump (as opposed to a crash dump, or no dump at all) was found for this work item, so
        /// callers can distinguish a detected hang from an ordinary failure, plus the console diagnostics the
        /// caller should print (deferred rather than printed here -- see <see cref="RunTests.CrashDiagnostics"/>).
        /// </returns>
        private static (bool IsTimeout, CrashDiagnostics Diagnostics) CheckForCrashes(string? resultsFilePath, string displayName, string dumpScanDirectory, string artifactsDirectory, DateTime sinceUtc)
        {
            var (dumpFiles, sequenceFiles, crashingTest, isHang) = detectDumpFiles();
            if (dumpFiles.Length == 0)
            {
                return (false, CrashDiagnostics.None);
            }

            Logger.Log($"Detected dump files for {displayName}: {string.Join(", ", dumpFiles)}");

            // Emit as AzDO timeline errors so they display prominently in the build results
            var failureType = isHang ? "timeout" : "crash";
            var errorMessage = crashingTest is string test
                ? $"Test host {failureType} detected for {displayName}. Test running at time of {failureType}: {test}"
                : $"Test host {failureType} detected for {displayName}";
            var diagnostics = new CrashDiagnostics(errorMessage, ImmutableArray.CreateRange(dumpFiles));

            // Copy sequence files to the shared artifacts directory so they're collected alongside the rest of
            // this run's results, rather than left behind in the per-work-item scan directory.
            copySequenceFilesToArtifacts(sequenceFiles, artifactsDirectory);

            if (resultsFilePath != null)
            {
                writeSyntheticFailure(resultsFilePath, dumpFiles, crashingTest, isHang);
            }

            return (isHang, diagnostics);

            (string[] DumpFiles, string[] SequenceFiles, string? CrashingTest, bool IsHang) detectDumpFiles()
            {
                if (!Directory.Exists(dumpScanDirectory))
                {
                    return ([], [], null, false);
                }

                // A couple of seconds of slack for filesystem timestamp granularity/clock resolution -- sinceUtc
                // itself is captured immediately before this work item's process is started, so a dump can never
                // legitimately predate it by more than that.
                var earliestAllowedUtc = sinceUtc - TimeSpan.FromSeconds(2);
                var dumpFiles = Directory.GetFiles(dumpScanDirectory, "*.dmp", SearchOption.AllDirectories)
                    .Where(f => File.GetCreationTimeUtc(f) >= earliestAllowedUtc)
                    .ToArray();
                if (dumpFiles.Length == 0)
                {
                    return ([], [], null, false);
                }

                var isHang = dumpFiles.Any(f => Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase));
                string? crashingTest = null;
                var allSequenceFiles = new List<string>();

                var dumpDirectories = dumpFiles.Select(Path.GetDirectoryName).Distinct();
                foreach (var dir in dumpDirectories)
                {
                    if (dir == null) continue;
                    var sequenceFiles = Directory.GetFiles(dir, "Sequence_*.xml", SearchOption.TopDirectoryOnly);
                    allSequenceFiles.AddRange(sequenceFiles);
                    if (sequenceFiles.Length > 0 && crashingTest == null)
                    {
                        crashingTest = getLastTestFromSequenceFile(sequenceFiles[0]);
                    }
                }

                return (dumpFiles, allSequenceFiles.ToArray(), crashingTest, isHang);
            }

            static string? getLastTestFromSequenceFile(string sequenceFilePath)
            {
                try
                {
                    var doc = XDocument.Load(sequenceFilePath);
                    var tests = doc.Descendants("Test");
                    var incomplete = tests.Where(t => t.Attribute("Completed")?.Value == "False");
                    var target = incomplete.FirstOrDefault() ?? tests.LastOrDefault();
                    return target?.Attribute("Name")?.Value;
                }
                catch
                {
                    return null;
                }
            }

            static void copySequenceFilesToArtifacts(string[] sequenceFiles, string testResultsDirectory)
            {
                foreach (var sequenceFile in sequenceFiles)
                {
                    try
                    {
                        var destPath = Path.Combine(testResultsDirectory, Path.GetFileName(sequenceFile));
                        if (!string.Equals(Path.GetFullPath(sequenceFile), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(sequenceFile, destPath, overwrite: true);
                        }

                        Logger.Log($"Copied sequence file to artifacts: {destPath}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: Failed to copy sequence file {sequenceFile}: {ex.Message}");
                    }
                }
            }

            void writeSyntheticFailure(string resultsFilePath, string[] dumpFiles, string? crashingTest, bool isHang)
            {
                try
                {
                    var failureType = isHang ? "HANG" : "CRASH";
                    var testName = crashingTest ?? "Unknown";
                    var dumpFileNames = string.Join(", ", dumpFiles.Select(Path.GetFileName));
                    var dumpFilePaths = string.Join("\n", dumpFiles);

                    var escapedWorkItemName = SecurityElement.Escape(displayName);
                    var escapedTestName = SecurityElement.Escape(testName);
                    var escapedDumpFileNames = SecurityElement.Escape(dumpFileNames);
                    var escapedDumpFilePaths = SecurityElement.Escape(dumpFilePaths);

                    var syntheticPath = Path.Combine(
                        Path.GetDirectoryName(resultsFilePath)!,
                        Path.GetFileNameWithoutExtension(resultsFilePath) + "_synthetic_failure.xml");

                    var xml = $"""
                        <?xml version="1.0" encoding="utf-8"?>
                        <assemblies>
                          <assembly name="{escapedWorkItemName}" total="1" passed="0" failed="1" skipped="0">
                            <collection name="Crash/Hang Detection" total="1" passed="0" failed="1" skipped="0">
                              <test name="[{failureType}] {escapedTestName}" type="RunTests.{failureType}Detection" method="{escapedTestName}" time="0" result="Fail">
                                <failure exception-type="TestHost{failureType}Exception">
                                  <message>Test host {failureType.ToLower()} detected. Test running at time of {failureType.ToLower()}: {escapedTestName}. Dump files: {escapedDumpFileNames}</message>
                                  <stack-trace>Dump files collected:
                        {escapedDumpFilePaths}</stack-trace>
                                </failure>
                              </test>
                            </collection>
                          </assembly>
                        </assemblies>
                        """;

                    File.WriteAllText(syntheticPath, xml);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Warning: Failed to write synthetic test failure: {ex.Message}");
                }
            }
        }
    }
}
