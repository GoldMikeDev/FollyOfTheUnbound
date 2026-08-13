// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace RunTests
{
    internal enum LiveRowStatus
    {
        Queued,
        Running,
        Passed,
        Failed,
        Timeout,
    }

    /// <summary>
    /// Redraws an in-place, fixed-height table of every work item (name / status / elapsed) in the current
    /// console window, replacing the old "N running, N queued, N completed" line-per-tick output. Only usable
    /// when attached to a real, interactive terminal -- <see cref="TryCreate"/> returns <see langword="null"/>
    /// (and callers fall back to the original line-based output) when output is redirected (as it always is in
    /// CI, where a redrawn table would just spam the log file with a full-table snapshot on every tick) or the
    /// terminal doesn't support cursor positioning at all.
    /// <para>
    /// The table's row *count* is fixed for the lifetime of a display (one row per work item, known up front),
    /// which is what makes the redraw simple: move the cursor up by the table's height and overwrite each line,
    /// padded to the current window width so old content can never show through. Only the column *widths*
    /// (particularly the name column) are recomputed on every redraw from the current <see cref="Console.WindowWidth"/>
    /// -- that's a cheap property read, so there's no need to special-case "only on resize" (which .NET doesn't
    /// expose a portable event for anyway); this also means the table self-corrects for free if the terminal is
    /// resized mid-run.
    /// </para>
    /// </summary>
    internal sealed class LiveTestProgressDisplay
    {
        private const int StatusColumnWidth = 9;
        private const int ElapsedColumnWidth = 7;
        private const int MinimumNameColumnWidth = 15;
        private const string Indent = "  ";
        private const string ColumnGap = "  ";

        private readonly string _runLabel;
        private readonly List<Row> _rows;
        private readonly Dictionary<int, Row> _rowsByPartitionIndex;

        /// <summary>Height (in lines) of the last frame drawn, or 0 if nothing has been drawn yet.</summary>
        private int _lastFrameHeight;
        private bool _disabled;

        private sealed class Row
        {
            internal required string FullName { get; init; }
            internal LiveRowStatus Status { get; set; } = LiveRowStatus.Queued;
            internal DateTime? StartTimeUtc { get; set; }
            internal TimeSpan? FinalElapsed { get; set; }
        }

        private LiveTestProgressDisplay(string runLabel, ImmutableArray<WorkItemInfo> workItems)
        {
            _runLabel = runLabel;
            _rows = workItems.Select(static item => new Row { FullName = GetShortName(item) }).ToList();
            _rowsByPartitionIndex = new Dictionary<int, Row>(workItems.Length);
            for (var i = 0; i < workItems.Length; i++)
            {
                _rowsByPartitionIndex[workItems[i].PartitionIndex] = _rows[i];
            }
        }

        /// <summary>
        /// The work item's assembly name(s) without the trailing <c>_&lt;partition&gt;</c> suffix
        /// <see cref="WorkItemInfo.DisplayName"/> adds for Helix work-item naming -- not useful in a table where
        /// every row is already visually distinct.
        /// </summary>
        private static string GetShortName(WorkItemInfo workItem)
            => string.Join("_", workItem.Filters.Keys.Select(static a => System.IO.Path.GetFileNameWithoutExtension(a.AssemblyName)));

        internal static LiveTestProgressDisplay? TryCreate(string runLabel, ImmutableArray<WorkItemInfo> workItems)
        {
            if (Console.IsOutputRedirected || workItems.Length == 0)
            {
                return null;
            }

            try
            {
                // Probe that cursor operations are actually usable here; some terminals/hosts report a non-redirected
                // stream but still throw on cursor queries (e.g. Windows Terminal profiles without a real console attached).
                _ = Console.WindowWidth;
                _ = Console.CursorTop;
            }
            catch
            {
                return null;
            }

            return new LiveTestProgressDisplay(runLabel, workItems);
        }

        internal void MarkRunning(WorkItemInfo workItem)
        {
            if (_rowsByPartitionIndex.TryGetValue(workItem.PartitionIndex, out var row))
            {
                row.Status = LiveRowStatus.Running;
                row.StartTimeUtc = DateTime.UtcNow;
            }
        }

        internal void MarkCompleted(WorkItemInfo workItem, TimeSpan elapsed, bool succeeded, bool isTimeout)
        {
            if (_rowsByPartitionIndex.TryGetValue(workItem.PartitionIndex, out var row))
            {
                row.Status = isTimeout ? LiveRowStatus.Timeout : succeeded ? LiveRowStatus.Passed : LiveRowStatus.Failed;
                row.FinalElapsed = elapsed;
            }
        }

        /// <summary>
        /// Clears the currently-drawn frame (if any) and leaves the cursor where it started, so a caller can
        /// print something else (e.g. failure diagnostics) that should persist above the table's next redraw
        /// rather than being overwritten by it. The next <see cref="Redraw"/> call draws a fresh frame
        /// immediately below whatever was just printed.
        /// </summary>
        internal void PrepareForExtraOutput()
        {
            if (_disabled || _lastFrameHeight == 0)
            {
                return;
            }

            try
            {
                var width = Math.Max(Console.WindowWidth - 1, 1);
                var top = Math.Max(Console.CursorTop - _lastFrameHeight, 0);
                Console.SetCursorPosition(0, top);
                var blank = new string(' ', width);
                for (var i = 0; i < _lastFrameHeight; i++)
                {
                    Console.WriteLine(blank);
                }
                Console.SetCursorPosition(0, top);
            }
            catch
            {
                _disabled = true;
            }

            _lastFrameHeight = 0;
        }

        internal void Redraw(int runningCount, int queuedCount, int completedCount, int failureCount)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                var width = Math.Max(Console.WindowWidth - 1, 1);
                var lines = BuildFrameLines(width, runningCount, queuedCount, completedCount, failureCount);

                if (_lastFrameHeight > 0)
                {
                    var top = Math.Max(Console.CursorTop - _lastFrameHeight, 0);
                    Console.SetCursorPosition(0, top);
                }

                foreach (var line in lines)
                {
                    Console.WriteLine(line);
                }

                _lastFrameHeight = lines.Count;
            }
            catch
            {
                _disabled = true;
            }
        }

        private List<string> BuildFrameLines(int width, int runningCount, int queuedCount, int completedCount, int failureCount)
        {
            var fixedOverhead = Indent.Length + ColumnGap.Length + StatusColumnWidth + ColumnGap.Length + ElapsedColumnWidth;
            var longestName = _rows.Count == 0 ? MinimumNameColumnWidth : _rows.Max(static r => r.FullName.Length);
            var nameColumnWidth = Math.Max(MinimumNameColumnWidth, Math.Min(longestName, width - fixedOverhead));

            var lines = new List<string>(_rows.Count + 4)
            {
                FitToWidth($"{_runLabel}    {_rows.Count} total | {runningCount} running | {queuedCount} queued | {completedCount} done | {failureCount} failed", width),
                string.Empty,
                FitToWidth($"{Indent}{"Test Assembly".PadRight(nameColumnWidth)}{ColumnGap}{"Status".PadRight(StatusColumnWidth)}{ColumnGap}{"Elapsed".PadLeft(ElapsedColumnWidth)}", width),
                FitToWidth($"{Indent}{new string('-', nameColumnWidth)}{ColumnGap}{new string('-', StatusColumnWidth)}{ColumnGap}{new string('-', ElapsedColumnWidth)}", width),
            };

            var now = DateTime.UtcNow;
            foreach (var row in _rows)
            {
                var name = row.FullName.Length > nameColumnWidth
                    ? row.FullName[..Math.Max(nameColumnWidth - 1, 0)] + "…"
                    : row.FullName;

                var statusText = row.Status switch
                {
                    LiveRowStatus.Queued => "QUEUED",
                    LiveRowStatus.Running => "RUNNING",
                    LiveRowStatus.Passed => "PASSED",
                    LiveRowStatus.Failed => "FAILED",
                    LiveRowStatus.Timeout => "TIMEOUT",
                    _ => "",
                };

                var elapsedText = row.Status switch
                {
                    LiveRowStatus.Queued => "--:--",
                    LiveRowStatus.Running => FormatElapsed(now - row.StartTimeUtc!.Value),
                    _ => FormatElapsed(row.FinalElapsed ?? TimeSpan.Zero),
                };

                var line = $"{Indent}{name.PadRight(nameColumnWidth)}{ColumnGap}{statusText.PadRight(StatusColumnWidth)}{ColumnGap}{elapsedText.PadLeft(ElapsedColumnWidth)}";
                lines.Add(FitToWidth(line, width));
            }

            return lines;
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            return elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}:{elapsed:mm\\:ss}"
                : $"{elapsed:mm\\:ss}";
        }

        /// <summary>
        /// Truncates a line that's too long for the window -- a hard safety net so a pathologically narrow
        /// window can never wrap a row onto a second physical line, which would break the fixed-height-frame
        /// assumption the redraw logic above depends on (column-width math already targets the current width, so
        /// this should rarely actually trim anything) -- and pads a line that's shorter, so every redraw fully
        /// overwrites whatever was on that screen row last time even if the window narrowed between redraws.
        /// </summary>
        private static string FitToWidth(string line, int width)
            => line.Length == width ? line : line.Length > width ? line[..width] : line.PadRight(width);
    }
}
