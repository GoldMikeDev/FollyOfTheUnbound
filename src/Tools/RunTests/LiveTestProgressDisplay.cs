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
    /// Redraws an in-place console table showing every currently-running work item (name / status / elapsed),
    /// plus any that finished with a failure or timeout, replacing the old "N running, N queued, N completed"
    /// line-per-tick output. Only usable when attached to a real, interactive terminal -- <see cref="TryCreate"/>
    /// returns <see langword="null"/> (and callers fall back to the original line-based output) when output is
    /// redirected (as it always is in CI, where a redrawn table would just spam the log file with a full-table
    /// snapshot on every tick) or the terminal doesn't support cursor positioning at all.
    /// <para>
    /// Unlike a naive "one row per work item" table, the row *set* shown is bounded to what actually fits in
    /// <see cref="Console.WindowHeight"/> every redraw -- a full Roslyn run has far more work items than any
    /// terminal has visible rows, and writing more lines than the window can hold makes the terminal scroll out
    /// from under the cursor-position math this depends on (the frame's "top" silently becomes unrecoverable, and
    /// the whole table gets re-appended to scrollback every tick instead of redrawing in place). Queued and
    /// already-passed items aren't shown individually -- they're not actionable moment to moment -- so the row
    /// budget goes to every currently-running item (inherently bounded by <c>RunTests</c>' own concurrency limit)
    /// plus every failed/timed-out item, with a trailing summary line accounting for anything still left over.
    /// </para>
    /// <para>
    /// Column *widths* (particularly the name column) are recomputed on every redraw from the current
    /// <see cref="Console.WindowWidth"/> -- that's a cheap property read, so there's no need to special-case
    /// "only on resize" (which .NET doesn't expose a portable event for anyway); this also means the table
    /// self-corrects for free if the terminal is resized mid-run.
    /// </para>
    /// </summary>
    internal sealed class LiveTestProgressDisplay
    {
        private const int StatusColumnWidth = 9;
        private const int ElapsedColumnWidth = 7;
        private const int MinimumNameColumnWidth = 15;
        private const string Indent = "  ";
        private const string ColumnGap = "  ";

        /// <summary>Lines always reserved outside the row budget: title, blank, column header, separator.</summary>
        private const int FixedFrameLines = 4;

        /// <summary>
        /// Extra lines reserved on top of <see cref="FixedFrameLines"/>: one for the optional trailing "N more /
        /// N queued" summary line (present on nearly every redraw of a real run, so always budgeted for rather
        /// than only when actually printed), and one spare so the frame's last line never lands exactly on the
        /// terminal's bottom row -- writing there would itself trigger a scroll and silently invalidate every
        /// "move cursor up by the last frame's height" redraw from then on.
        /// </summary>
        private const int ReservedTrailingLines = 2;

        private const int DefaultWindowHeight = 24;

        private readonly string _runLabel;
        private readonly List<Row> _rows;
        private readonly Dictionary<int, Row> _rowsByPartitionIndex;

        /// <summary>Height (in lines) of the last frame drawn, or 0 if nothing has been drawn yet.</summary>
        private int _lastFrameHeight;
        private bool _disabled;

        private sealed class Row
        {
            internal required string BaseName { get; init; }

            /// <summary>e.g. <c>" (net472)"</c> when another work item shares this row's <see cref="BaseName"/> and needs disambiguating; otherwise <see langword="null"/>.</summary>
            internal string? Suffix { get; init; }

            internal LiveRowStatus Status { get; set; } = LiveRowStatus.Queued;
            internal DateTime? StartTimeUtc { get; set; }
            internal TimeSpan? FinalElapsed { get; set; }

            /// <summary>
            /// The name to display within <paramref name="totalWidth"/> columns, truncating only
            /// <see cref="BaseName"/> (never <see cref="Suffix"/>, which is what disambiguates otherwise-identical
            /// rows and so must survive truncation) with a trailing ellipsis if it doesn't fit.
            /// </summary>
            internal string GetDisplayName(int totalWidth)
            {
                var suffix = Suffix ?? "";
                var availableForBase = Math.Max(totalWidth - suffix.Length, 1);
                var baseName = BaseName.Length > availableForBase
                    ? BaseName[..Math.Max(availableForBase - 1, 0)] + "…"
                    : BaseName;
                return baseName + suffix;
            }
        }

        private LiveTestProgressDisplay(string runLabel, ImmutableArray<WorkItemInfo> workItems)
        {
            _runLabel = runLabel;

            var nameParts = workItems.Select(GetNameParts).ToList();
            var duplicateBaseNames = nameParts
                .Select(static p => p.BaseName)
                .GroupBy(static n => n)
                .Where(static g => g.Count() > 1)
                .Select(static g => g.Key)
                .ToHashSet();

            _rows = new List<Row>(workItems.Length);
            for (var i = 0; i < workItems.Length; i++)
            {
                var (baseName, tfmTag) = nameParts[i];
                var suffix = duplicateBaseNames.Contains(baseName) && tfmTag is not null ? $" ({tfmTag})" : null;
                _rows.Add(new Row { BaseName = baseName, Suffix = suffix });
            }

            _rowsByPartitionIndex = new Dictionary<int, Row>(workItems.Length);
            for (var i = 0; i < workItems.Length; i++)
            {
                _rowsByPartitionIndex[workItems[i].PartitionIndex] = _rows[i];
            }
        }

        /// <summary>
        /// The work item's assembly name(s) without the trailing <c>_&lt;partition&gt;</c> suffix
        /// <see cref="WorkItemInfo.DisplayName"/> adds for Helix work-item naming (not useful in a table where
        /// every row is already visually distinct), plus the target-framework directory name of its first
        /// assembly (e.g. <c>net472</c>, <c>net10.0</c>) -- used to disambiguate rows only when needed, since
        /// <see cref="Options.TestRuntime.Both"/> schedules the same assembly filename as two separate work items,
        /// one per framework.
        /// </summary>
        private static (string BaseName, string? TfmTag) GetNameParts(WorkItemInfo workItem)
        {
            var baseName = string.Join("_", workItem.Filters.Keys.Select(static a => System.IO.Path.GetFileNameWithoutExtension(a.AssemblyName)));
            var firstAssemblyPath = workItem.Filters.Keys.Select(static a => a.AssemblyPath).FirstOrDefault();
            var tfmTag = firstAssemblyPath is null ? null : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(firstAssemblyPath));
            return (baseName, string.IsNullOrEmpty(tfmTag) ? null : tfmTag);
        }

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
                _ = Console.WindowHeight;
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
        /// Marks a work item failed without a <see cref="TestResult"/> to draw a real elapsed time from -- used
        /// when starting or awaiting it threw outright, so the row doesn't stay stuck at <c>RUNNING</c> with an
        /// ever-climbing timer for the rest of the run.
        /// </summary>
        internal void MarkFailed(WorkItemInfo workItem)
        {
            if (_rowsByPartitionIndex.TryGetValue(workItem.PartitionIndex, out var row))
            {
                row.Status = LiveRowStatus.Failed;
                row.FinalElapsed = row.StartTimeUtc is { } startTimeUtc ? DateTime.UtcNow - startTimeUtc : TimeSpan.Zero;
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

        internal void Redraw()
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                var width = Math.Max(Console.WindowWidth - 1, 1);
                var height = Console.WindowHeight > 0 ? Console.WindowHeight : DefaultWindowHeight;

                if (height <= FixedFrameLines)
                {
                    // Too short to safely show even the fixed header block with a spare bottom row -- stop
                    // updating rather than risk writing past the viewport, which would scroll the terminal and
                    // invalidate every cursor-position assumption this redraw depends on from then on.
                    _disabled = true;
                    return;
                }

                var lines = BuildFrameLines(width, height);

                if (_lastFrameHeight > 0)
                {
                    var top = Math.Max(Console.CursorTop - _lastFrameHeight, 0);
                    Console.SetCursorPosition(0, top);
                }

                foreach (var line in lines)
                {
                    Console.WriteLine(line);
                }

                // If this frame is shorter than the last one, the leftover lines below it are now stale (e.g. a
                // RUNNING row that just completed and dropped out of the body-row budget) and would otherwise
                // never get cleared, since _lastFrameHeight is about to shrink to only cover the new, smaller
                // frame. Blank them out, then move back up so the cursor still ends up right after the new
                // frame's actual content.
                if (lines.Count < _lastFrameHeight)
                {
                    var blank = new string(' ', width);
                    var extraLines = _lastFrameHeight - lines.Count;
                    for (var i = 0; i < extraLines; i++)
                    {
                        Console.WriteLine(blank);
                    }
                    Console.SetCursorPosition(0, Console.CursorTop - extraLines);
                }

                _lastFrameHeight = lines.Count;
            }
            catch
            {
                _disabled = true;
            }
        }

        private List<string> BuildFrameLines(int width, int windowHeight)
        {
            var runningRows = _rows.Where(static r => r.Status == LiveRowStatus.Running).ToList();
            var attentionRows = _rows.Where(static r => r.Status is LiveRowStatus.Failed or LiveRowStatus.Timeout).ToList();
            var queuedCount = _rows.Count(static r => r.Status == LiveRowStatus.Queued);
            var passedCount = _rows.Count(static r => r.Status == LiveRowStatus.Passed);

            // 0, not floored to at least 1: Redraw already refuses to draw anything at all once the window is too
            // short for the fixed frame alone, but between that point and a genuinely comfortable height, a short
            // window should still be allowed to show zero individual rows (just the summary line) rather than
            // forcing one in and risking exceeding the viewport anyway.
            var maxBodyRows = Math.Max(windowHeight - FixedFrameLines - ReservedTrailingLines, 0);

            var bodyRows = new List<Row>(Math.Min(runningRows.Count + attentionRows.Count, maxBodyRows));
            bodyRows.AddRange(runningRows.Take(maxBodyRows));
            if (bodyRows.Count < maxBodyRows)
            {
                bodyRows.AddRange(attentionRows.Take(maxBodyRows - bodyRows.Count));
            }

            var shownRunning = bodyRows.Count(static r => r.Status == LiveRowStatus.Running);
            var shownAttention = bodyRows.Count - shownRunning;
            var hiddenRunning = runningRows.Count - shownRunning;
            var hiddenAttention = attentionRows.Count - shownAttention;

            var fixedOverhead = Indent.Length + ColumnGap.Length + StatusColumnWidth + ColumnGap.Length + ElapsedColumnWidth;
            var longestName = bodyRows.Count == 0
                ? MinimumNameColumnWidth
                : bodyRows.Max(static r => r.BaseName.Length + (r.Suffix?.Length ?? 0));
            var nameColumnWidth = Math.Max(MinimumNameColumnWidth, Math.Min(longestName, width - fixedOverhead));

            var lines = new List<string>(bodyRows.Count + FixedFrameLines + 1)
            {
                FitToWidth($"{_runLabel}    {_rows.Count} total | {runningRows.Count} running | {queuedCount} queued | {passedCount + attentionRows.Count} done | {attentionRows.Count} failed", width),
                string.Empty,
                FitToWidth($"{Indent}{"Test Assembly".PadRight(nameColumnWidth)}{ColumnGap}{"Status".PadRight(StatusColumnWidth)}{ColumnGap}{"Elapsed".PadLeft(ElapsedColumnWidth)}", width),
                FitToWidth($"{Indent}{new string('-', nameColumnWidth)}{ColumnGap}{new string('-', StatusColumnWidth)}{ColumnGap}{new string('-', ElapsedColumnWidth)}", width),
            };

            var now = DateTime.UtcNow;
            foreach (var row in bodyRows)
            {
                var name = row.GetDisplayName(nameColumnWidth);
                var statusText = row.Status switch
                {
                    LiveRowStatus.Running => "RUNNING",
                    LiveRowStatus.Failed => "FAILED",
                    LiveRowStatus.Timeout => "TIMEOUT",
                    _ => "",
                };
                var elapsedText = row.Status == LiveRowStatus.Running
                    ? FormatElapsed(now - row.StartTimeUtc!.Value)
                    : FormatElapsed(row.FinalElapsed ?? TimeSpan.Zero);

                var line = $"{Indent}{name.PadRight(nameColumnWidth)}{ColumnGap}{statusText.PadRight(StatusColumnWidth)}{ColumnGap}{elapsedText.PadLeft(ElapsedColumnWidth)}";
                lines.Add(FitToWidth(line, width));
            }

            // Queued items were never going to be interesting to watch individually, and anything actively
            // running or needing attention that didn't fit the viewport is at least accounted for here instead
            // of silently vanishing.
            if (hiddenRunning > 0 || hiddenAttention > 0 || queuedCount > 0)
            {
                var parts = new List<string>();
                if (hiddenRunning > 0)
                {
                    parts.Add($"{hiddenRunning} more running");
                }
                if (hiddenAttention > 0)
                {
                    parts.Add($"{hiddenAttention} more failed/timeout");
                }
                if (queuedCount > 0)
                {
                    parts.Add($"{queuedCount} queued");
                }

                lines.Add(FitToWidth($"{Indent}... {string.Join(", ", parts)}", width));
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
