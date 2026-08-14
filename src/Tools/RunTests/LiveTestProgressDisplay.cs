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
    /// Redraws an in-place console table showing every work item (name / status / elapsed), sorted alphabetically,
    /// replacing the old "N running, N queued, N completed" line-per-tick output. Only usable when attached to a
    /// real, interactive terminal -- <see cref="TryCreate"/> returns <see langword="null"/> (and callers fall back
    /// to the original line-based output) when output is redirected (as it always is in CI, where a redrawn table
    /// would just spam the log file with a full-table snapshot on every tick) or the terminal doesn't support
    /// cursor positioning at all.
    /// <para>
    /// Every row is always shown, alphabetically, regardless of status -- queued and passed rows included, not
    /// just currently-running/failed ones. A full Roslyn run has far more work items than any terminal has visible
    /// rows, so on a real run most of the list is off-screen at any given moment; that's accepted here rather than
    /// filtering rows down to what fits, so the full list -- and its alphabetical order -- stays intact. Each
    /// redraw still targets the table's true, fixed origin (the screen-buffer row its first line was originally
    /// drawn on -- see <see cref="_frameTopRow"/>) rather than a position derived from wherever the previous frame
    /// happened to leave the cursor, so it stays anchored and redraws in place even once the frame is taller than
    /// the visible window and the terminal has to scroll to keep up.
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
        private const int MinimumNameColumnWidth = 15;
        private const string Indent = "  ";
        private const string ColumnGap = "  ";

        /// <summary>Lines always printed outside the per-work-item rows: title, blank, column header, separator.</summary>
        private const int FixedFrameLines = 4;

        private const int DefaultWindowHeight = 24;

        private readonly string _runLabel;
        private readonly List<Row> _rows;
        private readonly Dictionary<int, Row> _rowsByPartitionIndex;

        /// <summary>Height (in lines) of the last frame drawn, or 0 if nothing has been drawn yet.</summary>
        private int _lastFrameHeight;

        /// <summary>
        /// The screen-buffer row the table's first line was originally drawn on, or <see langword="null"/> if
        /// nothing has been drawn yet (or the last frame was cleared by <see cref="PrepareForExtraOutput"/> and
        /// the next <see cref="Redraw"/> hasn't re-anchored yet). Recorded once, not recomputed as
        /// <c>Console.CursorTop - _lastFrameHeight</c> on every redraw: once the table's row count exceeds the
        /// visible window (the common case for a full run now that every work item is always shown), writing a
        /// frame scrolls the terminal, so the cursor's position immediately after a frame no longer has a fixed
        /// relationship to that frame's true top -- only the buffer row recorded before the very first write does.
        /// <see cref="Console.SetCursorPosition"/> targets this absolute buffer row directly on every redraw
        /// (the terminal scrolls back to bring it into view as needed), so the table always redraws in place from
        /// its real origin instead of from wherever the previous frame happened to leave the cursor.
        /// </summary>
        private int? _frameTopRow;

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

            var unsortedRows = new List<Row>(workItems.Length);
            for (var i = 0; i < workItems.Length; i++)
            {
                var (baseName, tfmTag) = nameParts[i];
                var suffix = duplicateBaseNames.Contains(baseName) && tfmTag is not null ? $" ({tfmTag})" : null;
                unsortedRows.Add(new Row { BaseName = baseName, Suffix = suffix });
            }

            _rowsByPartitionIndex = new Dictionary<int, Row>(workItems.Length);
            for (var i = 0; i < workItems.Length; i++)
            {
                _rowsByPartitionIndex[workItems[i].PartitionIndex] = unsortedRows[i];
            }

            // Sorted once up front (not on every redraw): row identity never changes after construction, only
            // status/elapsed do, so the alphabetical order established here stays valid for the whole run.
            _rows = unsortedRows
                .OrderBy(static r => r.BaseName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static r => r.Suffix, StringComparer.OrdinalIgnoreCase)
                .ToList();
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
            if (_disabled || _frameTopRow is not { } top)
            {
                return;
            }

            try
            {
                var width = Math.Max(Console.WindowWidth - 1, 1);
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
            _frameTopRow = null;
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

                var lines = BuildFrameLines(width);

                if (_frameTopRow is { } top)
                {
                    Console.SetCursorPosition(0, top);
                }
                else
                {
                    _frameTopRow = Console.CursorTop;
                }

                foreach (var line in lines)
                {
                    Console.WriteLine(line);
                }

                // The frame's line count is normally constant (every row is always printed, regardless of status),
                // but this still guards the one case where it isn't: TryCreate's initial work-item count vs. this
                // redraw's actual row count could differ if that ever changes. Blank out any leftover stale lines
                // below the new, shorter frame, then move back up to the fixed top so the cursor position this
                // frame actually used doesn't drift from _frameTopRow for the next redraw.
                if (lines.Count < _lastFrameHeight)
                {
                    var blank = new string(' ', width);
                    var extraLines = _lastFrameHeight - lines.Count;
                    for (var i = 0; i < extraLines; i++)
                    {
                        Console.WriteLine(blank);
                    }
                }

                _lastFrameHeight = lines.Count;
            }
            catch
            {
                _disabled = true;
            }
        }

        private List<string> BuildFrameLines(int width)
        {
            var runningCount = _rows.Count(static r => r.Status == LiveRowStatus.Running);
            var queuedCount = _rows.Count(static r => r.Status == LiveRowStatus.Queued);
            var passedCount = _rows.Count(static r => r.Status == LiveRowStatus.Passed);
            var attentionCount = _rows.Count(static r => r.Status is LiveRowStatus.Failed or LiveRowStatus.Timeout);

            var fixedOverhead = Indent.Length + ColumnGap.Length + TestResultDisplay.StatusColumnWidth + ColumnGap.Length + TestResultDisplay.ElapsedColumnWidth;
            var longestName = _rows.Count == 0
                ? MinimumNameColumnWidth
                : _rows.Max(static r => r.BaseName.Length + (r.Suffix?.Length ?? 0));
            var nameColumnWidth = Math.Max(MinimumNameColumnWidth, Math.Min(longestName, width - fixedOverhead));

            var lines = new List<string>(_rows.Count + FixedFrameLines)
            {
                FitToWidth($"{_runLabel}    {_rows.Count} total | {runningCount} running | {queuedCount} queued | {passedCount + attentionCount} done | {attentionCount} failed", width),
                string.Empty,
                FitToWidth($"{Indent}{"Test Assembly".PadRight(nameColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad("Status", TestResultDisplay.StatusColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad("Elapsed", TestResultDisplay.ElapsedColumnWidth)}", width),
                // The Status underline fills its whole column (like the Test Assembly one) -- it only reads as
                // "one dash past the word" because the centered header text is inset from the column edges. The
                // Elapsed underline is different: exactly the word's length, never the full (wider, HH:mm:ss-sized)
                // column, centered within it same as the data.
                FitToWidth($"{Indent}{new string('-', nameColumnWidth)}{ColumnGap}{new string('-', TestResultDisplay.StatusColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad(new string('-', "Elapsed".Length), TestResultDisplay.ElapsedColumnWidth)}", width),
            };

            var now = DateTime.UtcNow;
            foreach (var row in _rows)
            {
                var name = row.GetDisplayName(nameColumnWidth);
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
                    LiveRowStatus.Running => TestResultDisplay.FormatElapsed(now - row.StartTimeUtc!.Value),
                    _ => TestResultDisplay.FormatElapsed(row.FinalElapsed ?? TimeSpan.Zero),
                };

                var line = $"{Indent}{name.PadRight(nameColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad(statusText, TestResultDisplay.StatusColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad(elapsedText, TestResultDisplay.ElapsedColumnWidth)}";
                lines.Add(FitToWidth(line, width));
            }

            return lines;
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
