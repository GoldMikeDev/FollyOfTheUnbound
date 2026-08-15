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
    /// cursor positioning (or the alternate screen buffer, see below) at all.
    /// <para>
    /// Every work item is always tracked, sorted alphabetically, regardless of status -- queued and passed rows
    /// included, not just currently-running/failed ones. But a terminal only ever has <c>WindowHeight</c> physical
    /// rows on screen, and a full Roslyn run has far more work items than that -- there is no way to show more
    /// rows at once than the window physically has, on any terminal. What's drawn each redraw is therefore a
    /// scrolling *window* into the full sorted list (see <see cref="ComputeScrollStart(int, int, int, int, int)"/>) that follows whatever's
    /// currently running so the active rows stay in view, rather than a fixed, arbitrary slice.
    /// </para>
    /// <para>
    /// The table draws into the terminal's *alternate screen buffer* (entered in <see cref="TryCreate"/>, exited
    /// via <see cref="Complete"/>) -- the same mechanism full-screen terminal programs like <c>vim</c> or
    /// <c>htop</c> use: a dedicated grid of exactly <c>WindowHeight</c> rows, completely decoupled from the normal
    /// buffer's scrollback. Every redraw homes the cursor to (0,0) and repaints that whole grid, which is reliably
    /// addressable on every platform -- unlike writing a frame taller than the window directly into the normal
    /// buffer, which scrolls semantics-breaking content into scrollback that classic Windows consoles can still
    /// address by absolute row, but Unix and ConPTY-style (including current Windows Terminal) terminals cannot.
    /// Nothing drawn while in the alternate screen is ever part of real scrollback -- entering it hides whatever
    /// was in the normal buffer (restored automatically on exit), so <see cref="PrepareForExtraOutput"/> briefly
    /// exits it (rather than trying to draw interleaved diagnostics inside a fixed-size grid) whenever a caller
    /// needs to print something that should actually persist in the user's real scrollback history.
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

        /// <summary>Standard xterm/VT sequences for entering and leaving the alternate screen buffer.</summary>
        private const string EnterAltScreen = "\x1b[?1049h";
        private const string ExitAltScreen = "\x1b[?1049l";

        private readonly string _runLabel;
        private readonly List<Row> _rows;
        private readonly Dictionary<int, Row> _rowsByPartitionIndex;

        /// <summary>
        /// Guards every method that touches the alternate-screen state (<see cref="_inAltScreen"/>,
        /// <see cref="_disabled"/>) or writes to the console -- <see cref="Redraw"/> runs on the still-active run
        /// loop's own continuation, while <see cref="Suspend"/>/<see cref="PrepareForExtraOutput"/> can be called
        /// concurrently from unrelated code (<c>Program.HandleTimeout</c>) on a different continuation entirely.
        /// Without this, a <see cref="Redraw"/> already past its own state checks could still re-enter the
        /// alternate screen and paint over diagnostics a concurrent <see cref="Suspend"/> just exited it for.
        /// </summary>
        private readonly object _gate = new();

        /// <summary>
        /// Whether the alternate screen buffer is currently active. Starts <see langword="true"/> -- entered by
        /// <see cref="TryCreate"/>, which only returns an instance once entering it has actually succeeded --
        /// and toggles off/on around <see cref="PrepareForExtraOutput"/>/<see cref="Redraw"/> pairs so a caller's
        /// interleaved diagnostics land in the user's real, persistent scrollback rather than inside the
        /// fixed-size grid the table itself draws into.
        /// </summary>
        private bool _inAltScreen = true;

        /// <summary>The scroll offset (index into <see cref="_rows"/>) used by the last redraw, or 0 if none yet.</summary>
        private int _lastScrollStart;

        /// <summary>
        /// The scroll offset the user last picked with the keyboard (see <see cref="PollKeyboardInput"/>), or
        /// <see langword="null"/> if they haven't touched navigation yet -- in which case <see cref="ComputeScrollStart(int)"/>
        /// keeps auto-following whatever's running/queued, exactly as before this field existed. Once set, it wins
        /// over auto-follow every redraw (rows/status keep updating live underneath, but the visible window stops
        /// chasing them) until <c>Esc</c> clears it back to <see langword="null"/>, matching a spreadsheet's frozen
        /// header row: the header never moves, but the body underneath scrolls exactly where the user left it.
        /// </summary>
        private int? _manualScrollStart;

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
        /// <see cref="TestRuntime.Both"/> schedules the same assembly filename as two separate work items,
        /// one per framework.
        /// </summary>
        private static (string BaseName, string? TfmTag) GetNameParts(WorkItemInfo workItem)
        {
            var baseName = string.Join("_", workItem.Filters.Keys.Select(static a => System.IO.Path.GetFileNameWithoutExtension(a.AssemblyName)));
            var firstAssemblyPath = workItem.Filters.Keys.Select(static a => a.AssemblyPath).FirstOrDefault();
            var tfmTag = firstAssemblyPath is null ? null : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(firstAssemblyPath));
            return (baseName, string.IsNullOrEmpty(tfmTag) ? null : tfmTag);
        }

        /// <summary>
        /// The most recently created, still-live instance (if any), so code outside <see cref="TestRunner"/> --
        /// namely <c>Program.HandleTimeout</c>, which runs concurrently with the still-active run loop and prints
        /// its own timeout/dump diagnostics directly to the console -- can call <see cref="PrepareForExtraOutput"/>
        /// first and have those diagnostics land in real scrollback instead of the alternate-screen frame the
        /// still-running redraw loop would otherwise keep overwriting/eventually discarding them under. Set in
        /// <see cref="TryCreate"/>, cleared by <see cref="Complete"/>.
        /// </summary>
        internal static LiveTestProgressDisplay? Current { get; private set; }

        internal static LiveTestProgressDisplay? TryCreate(string runLabel, ImmutableArray<WorkItemInfo> workItems)
        {
            if (Console.IsOutputRedirected || workItems.Length == 0)
            {
                return null;
            }

            var enteredAltScreen = false;
            try
            {
                // Probe that cursor operations are actually usable here; some terminals/hosts report a non-redirected
                // stream but still throw on cursor queries (e.g. Windows Terminal profiles without a real console attached).
                _ = Console.WindowWidth;
                _ = Console.WindowHeight;
                _ = Console.CursorTop;

                // Entering the alternate screen buffer is the one step that can't be verified beyond "the write
                // didn't throw" -- a terminal that silently ignores the escape sequence (rather than erroring)
                // would look identical to one that honored it. That's an accepted risk here the same way the
                // cursor-position probes above are: this only ever runs on a real, non-redirected interactive
                // terminal in the first place, and Redraw/Complete still fail safe (falling back to _disabled)
                // if anything downstream goes wrong.
                Console.Out.Write(EnterAltScreen);
                Console.Out.Flush();
                enteredAltScreen = true;

                // Verified separately (rather than folded into the probes above, before EnterAltScreen) because
                // this specific call is the one every other codepath in this class relies on for every redraw --
                // worth confirming it actually works in the exact state (already in the alternate screen) it'll
                // always be called in from then on, not just that cursor queries in general don't throw.
                Console.SetCursorPosition(0, 0);
            }
            catch
            {
                if (enteredAltScreen)
                {
                    // Must not leave the user's terminal stuck showing the alternate screen with no instance
                    // left in existence for Complete() to ever restore it -- this is the only place that can
                    // still clean up after itself once TryCreate has decided to fail.
                    try
                    {
                        Console.Out.Write(ExitAltScreen);
                        Console.Out.Flush();
                    }
                    catch
                    {
                        // Best effort -- there's nothing more this can do if even exiting the alternate screen fails.
                    }
                }

                return null;
            }

            var display = new LiveTestProgressDisplay(runLabel, workItems);
            Current = display;
            return display;
        }

        /// <summary>
        /// Exits the alternate screen buffer, restoring the terminal to whatever it showed before the table
        /// started (standard alternate-screen semantics -- no manual clearing needed). Must be called once the
        /// run loop is done with this display, successful or not, or the user's terminal is left showing the
        /// table's now-static last frame indefinitely instead of returning to their real prompt/scrollback.
        /// Safe to call multiple times or on an already-<see langword="disabled"/> instance.
        /// </summary>
        internal void Complete()
        {
            lock (_gate)
            {
                ExitAltScreenIfActive();
            }

            if (Current == this)
            {
                Current = null;
            }
        }

        /// <summary>
        /// Exits the alternate screen buffer if it's currently active, restoring the terminal to whatever it
        /// showed before the table started (standard alternate-screen semantics -- no manual clearing needed).
        /// Deliberately not gated on <see cref="_disabled"/> -- every caller (<see cref="Complete"/>,
        /// <see cref="PrepareForExtraOutput"/>, <see cref="DisableAndExitAltScreen"/>) needs the normal buffer
        /// back regardless of whether redraws have stopped, since a disabled display can still have failure
        /// diagnostics printed after it that must land in real scrollback, not a frame that gets discarded.
        /// </summary>
        private void ExitAltScreenIfActive()
        {
            if (!_inAltScreen)
            {
                return;
            }

            try
            {
                Console.Out.Write(ExitAltScreen);
                Console.Out.Flush();
            }
            catch
            {
                // Best effort -- there's nothing more this can do if even exiting the alternate screen fails.
            }

            _inAltScreen = false;
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
        /// Briefly exits the alternate screen buffer so a caller can print something (e.g. failure diagnostics)
        /// that should persist in the user's real, normal-buffer scrollback -- the fixed-size grid the table
        /// draws into is not part of that scrollback at all, so printing "into" it here would just be lost the
        /// moment the table moves on to its next redraw. The next <see cref="Redraw"/> call re-enters the
        /// alternate screen and draws a fresh frame there. Works even once <see cref="_disabled"/> is set --
        /// a work item can still fail (and need its diagnostics printed) after redraws have stopped, e.g. because
        /// the window got too short, and those diagnostics must still reach real scrollback, not a now-frozen
        /// alternate-screen frame <see cref="Complete"/> will just discard.
        /// </summary>
        internal void PrepareForExtraOutput()
        {
            lock (_gate)
            {
                ExitAltScreenIfActive();
            }
        }

        /// <summary>
        /// Permanently stops this display from drawing anything further and exits the alternate screen, unlike
        /// <see cref="PrepareForExtraOutput"/> -- which only exits it for one caller's immediate print, and would
        /// otherwise be undone by the very next <see cref="Redraw"/> re-entering it. Needed for diagnostics that
        /// take longer than one redraw tick to finish printing (e.g. <c>Program.HandleTimeout</c>'s screenshot
        /// capture and per-process dump collection, which can each take a while) and run concurrently with a
        /// still-active run loop that keeps calling <see cref="Redraw"/> on its own one-second timer regardless --
        /// without this, that loop could re-enter the alternate screen mid-report and hide, overwrite, or
        /// interleave with whatever's being printed. The run is expected to be ending anyway once this is called
        /// (there is no corresponding <c>Resume</c>), so simply not drawing again is an acceptable outcome, unlike
        /// the failure paths <see cref="DisableAndExitAltScreen"/> handles for the same underlying reason.
        /// </summary>
        internal void Suspend()
        {
            lock (_gate)
            {
                DisableAndExitAltScreen();
            }
        }

        internal void Redraw()
        {
            lock (_gate)
            {
                RedrawCore();
            }
        }

        private void RedrawCore()
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
                    // Too short to safely show even the fixed header block with a spare bottom row.
                    DisableAndExitAltScreen();
                    return;
                }

                if (!_inAltScreen)
                {
                    Console.Out.Write(EnterAltScreen);
                    _inAltScreen = true;
                }

                PollKeyboardInput(Math.Max(height - FixedFrameLines, 0));

                // Always home the cursor and repaint the whole grid, rather than trying to track a "last frame
                // height" to selectively overwrite -- the alternate screen is a fixed WindowWidth x WindowHeight
                // grid regardless of what was drawn there before, so there's no scrolling, no off-screen origin
                // to lose track of, and nothing stale can ever be left behind as long as every redraw fills the
                // full grid (BuildFrameLines pads short frames with blank lines to exactly `height` for this).
                Console.Out.Write("\x1b[H");

                var lines = BuildFrameLines(width, height);
                for (var i = 0; i < lines.Count; i++)
                {
                    // The very last line must not end in a newline: the alternate screen is exactly `height` rows
                    // (0 through height-1), so writing one from the bottom row scrolls the whole grid up by one --
                    // shifting the title/counts off the top and leaving a blank row at the bottom, defeating the
                    // entire point of a fixed-size grid that never scrolls.
                    if (i == lines.Count - 1)
                    {
                        Console.Out.Write(lines[i]);
                    }
                    else
                    {
                        Console.WriteLine(lines[i]);
                    }
                }

                Console.Out.Flush();
            }
            catch
            {
                DisableAndExitAltScreen();
            }
        }

        /// <summary>
        /// Disables further redraws and, if the alternate screen is still active, exits it first -- so a caller
        /// that goes on to print failure/crash diagnostics via <see cref="PrepareForExtraOutput"/> (which itself
        /// does nothing once <see cref="_disabled"/> is set) still gets them into the user's real scrollback
        /// instead of a now-frozen alternate-screen frame that <see cref="Complete"/> ultimately discards.
        /// </summary>
        private void DisableAndExitAltScreen()
        {
            _disabled = true;
            ExitAltScreenIfActive();
        }

        /// <summary>
        /// Reads and applies any keystrokes the user has typed since the last redraw, so the row window can be
        /// scrolled by hand (like freezing a spreadsheet's header row and scrolling the body underneath) instead of
        /// only ever auto-following whichever row is running/queued. Non-blocking -- drains whatever's already
        /// buffered via <see cref="Console.KeyAvailable"/> rather than waiting for a key -- and best-effort: a host
        /// that doesn't support key polling (e.g. input redirected, or a probe that throws) just leaves navigation
        /// disabled for the rest of the run rather than taking the whole display down with it.
        /// </summary>
        private void PollKeyboardInput(int visibleRowBudget)
        {
            try
            {
                if (Console.IsInputRedirected)
                {
                    return;
                }

                var maxScrollStart = Math.Max(_rows.Count - visibleRowBudget, 0);
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;
                    _manualScrollStart = ApplyNavigationKey(key, _manualScrollStart, _lastScrollStart, visibleRowBudget, maxScrollStart);
                }
            }
            catch
            {
                // Best effort -- if key polling isn't usable here, the auto-following window still works fine
                // without it; there's nothing more useful to do than skip navigation for the rest of the run.
            }
        }

        /// <summary>
        /// The pure key-to-scroll-position mapping behind <see cref="PollKeyboardInput"/>, split out (like
        /// <see cref="ComputeScrollStart(int, int, int, int, int)"/>) so it's directly unit-testable without a real
        /// console. Returns the new <see cref="_manualScrollStart"/> value for a recognized navigation key, or the
        /// unchanged <paramref name="previousManualScrollStart"/> for any other key.
        /// </summary>
        internal static int? ApplyNavigationKey(ConsoleKey key, int? previousManualScrollStart, int lastScrollStart, int visibleRowBudget, int maxScrollStart)
        {
            var current = previousManualScrollStart ?? lastScrollStart;
            return key switch
            {
                ConsoleKey.UpArrow => Math.Clamp(current - 1, 0, maxScrollStart),
                ConsoleKey.DownArrow => Math.Clamp(current + 1, 0, maxScrollStart),
                ConsoleKey.PageUp => Math.Clamp(current - visibleRowBudget, 0, maxScrollStart),
                ConsoleKey.PageDown => Math.Clamp(current + visibleRowBudget, 0, maxScrollStart),
                ConsoleKey.Home => 0,
                ConsoleKey.End => maxScrollStart,
                // Hand control back to auto-follow instead of holding wherever the user last scrolled.
                ConsoleKey.Escape => null,
                _ => previousManualScrollStart,
            };
        }

        /// <summary>
        /// Picks which slice of <see cref="_rows"/> (already sorted alphabetically) to show given a viewport that
        /// can hold <paramref name="visibleRowBudget"/> rows. Defers to <see cref="_manualScrollStart"/> if the
        /// user has navigated (see <see cref="PollKeyboardInput"/>); otherwise a thin instance wrapper around the
        /// pure, independently testable <see cref="ComputeScrollStart(int, int, int, int, int)"/> overload -- just
        /// finds the two focus candidates in <see cref="_rows"/> and forwards to it.
        /// </summary>
        private int ComputeScrollStart(int visibleRowBudget)
        {
            if (_manualScrollStart is { } manual)
            {
                return Math.Clamp(manual, 0, Math.Max(_rows.Count - visibleRowBudget, 0));
            }

            var firstRunningIndex = _rows.FindIndex(static r => r.Status == LiveRowStatus.Running);
            var firstQueuedIndex = _rows.FindIndex(static r => r.Status == LiveRowStatus.Queued);
            return ComputeScrollStart(_rows.Count, visibleRowBudget, _lastScrollStart, firstRunningIndex, firstQueuedIndex);
        }

        /// <summary>
        /// The scrolling-window state machine itself, as a pure function of its inputs (no console/instance state)
        /// so it's directly unit-testable -- the whole list from the start if it already fits, otherwise a window
        /// centered on whatever's most worth watching right now: <paramref name="firstRunningIndex"/> if there is
        /// one (so active work stays visible; pass -1 if none), else <paramref name="firstQueuedIndex"/> (what's
        /// coming up next; -1 if none), else <paramref name="previousScrollStart"/> (so a run that's entirely
        /// finished doesn't suddenly snap back to the top). Sticking to the *previous* scroll position rather than
        /// recomputing a fresh "ideal" center every tick also avoids the window jittering up and down by a row or
        /// two as different items finish near the current focus.
        /// </summary>
        internal static int ComputeScrollStart(int rowCount, int visibleRowBudget, int previousScrollStart, int firstRunningIndex, int firstQueuedIndex)
        {
            var maxScrollStart = Math.Max(rowCount - visibleRowBudget, 0);
            if (maxScrollStart == 0)
            {
                return 0;
            }

            var focusIndex = firstRunningIndex >= 0 ? firstRunningIndex : firstQueuedIndex;

            if (focusIndex < 0)
            {
                // Nothing running or queued (the run is effectively done) -- hold the previous position instead
                // of snapping to a default, so the last thing the user was looking at doesn't jump around.
                return Math.Clamp(previousScrollStart, 0, maxScrollStart);
            }

            // Only actually move the window if the focus row has scrolled out of the *previously shown* range --
            // otherwise every redraw would recenter on the focus row exactly, causing the window to visibly drift
            // by a row or two on every tick as the running set changes, even though the focus row was still
            // perfectly visible.
            if (focusIndex >= previousScrollStart && focusIndex < previousScrollStart + visibleRowBudget)
            {
                return Math.Clamp(previousScrollStart, 0, maxScrollStart);
            }

            return Math.Clamp(focusIndex - visibleRowBudget / 2, 0, maxScrollStart);
        }

        private List<string> BuildFrameLines(int width, int height)
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

            var visibleRowBudget = Math.Max(height - FixedFrameLines, 0);
            var scrollStart = ComputeScrollStart(visibleRowBudget);
            _lastScrollStart = scrollStart;
            var visibleRows = _rows.Skip(scrollStart).Take(visibleRowBudget).ToList();

            var titleLine = $"{_runLabel}    {_rows.Count} total | {runningCount} running | {queuedCount} queued | {passedCount + attentionCount} done | {attentionCount} failed";
            if (visibleRowBudget < _rows.Count)
            {
                titleLine += $"    (showing {scrollStart + 1}-{scrollStart + visibleRows.Count} of {_rows.Count})";
                titleLine += _manualScrollStart is not null
                    ? "    [↑↓ PgUp/PgDn Home/End to scroll, Esc to follow]"
                    : "    [↑↓/PgUp/PgDn to scroll]";
            }

            var lines = new List<string>(height)
            {
                FitToWidth(titleLine, width),
                string.Empty,
                FitToWidth($"{Indent}{"Test Assembly".PadRight(nameColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad("Status", TestResultDisplay.StatusColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad("Elapsed", TestResultDisplay.ElapsedColumnWidth)}", width),
                // The Status underline fills its whole column (like the Test Assembly one) -- it only reads as
                // "one dash past the word" because the centered header text is inset from the column edges. The
                // Elapsed underline is different: the word's length plus one extra dash on each side, never the
                // full (wider, HH:mm:ss-sized) column, centered within it same as the data.
                FitToWidth($"{Indent}{new string('-', nameColumnWidth)}{ColumnGap}{new string('-', TestResultDisplay.StatusColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad(new string('-', "Elapsed".Length + 2), TestResultDisplay.ElapsedColumnWidth)}", width),
            };

            var now = DateTime.UtcNow;
            foreach (var row in visibleRows)
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

            // Pad to exactly `height` lines so every redraw fully overwrites the whole alternate-screen grid,
            // even when fewer rows are visible than the budget allows (a short run) or the window just grew.
            while (lines.Count < height)
            {
                lines.Add(new string(' ', width));
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
