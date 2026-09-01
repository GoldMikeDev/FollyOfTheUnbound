// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Win32Console = Windows.Win32.System.Console;

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

        /// <summary>
        /// How long <see cref="WaitForMoreInput"/> will wait for a split escape sequence's remaining bytes to
        /// arrive before giving up. Generous relative to how fast local PTY delivery actually is (sub-millisecond)
        /// while still cheap against this display's ~1-second redraw cadence -- this only ever blocks the one poll
        /// that happens to catch a sequence mid-delivery, not every tick.
        /// </summary>
        private const int EscapeSequenceCompletionTimeoutMs = 25;

        /// <summary>Standard xterm/VT sequences for entering and leaving the alternate screen buffer.</summary>
        private const string EnterAltScreen = "\x1b[?1049h";
        private const string ExitAltScreen = "\x1b[?1049l";

        /// <summary>Standard SGR reset, closing whatever <see cref="GetAnsiColorCode"/> opened for a row.</summary>
        private const string ResetColor = "\x1b[0m";

        /// <summary>Standard SGR foreground color code for a row's <see cref="GetRowColor"/>.</summary>
        private static string GetAnsiColorCode(ConsoleColor color) => color switch
        {
            ConsoleColor.Green => "\x1b[32m",
            ConsoleColor.Yellow => "\x1b[33m",
            ConsoleColor.Red => "\x1b[31m",
            _ => "",
        };

        /// <summary>
        /// Standard xterm sequences for enabling/disabling mouse button+wheel reporting (mode 1000) with SGR
        /// extended coordinates (mode 1006) -- SGR is what lets the button code arrive unmodified (rather than
        /// offset into a single byte that overflows past column/row 223), which is all <see cref="TryParseSgrMouseWheel"/>
        /// actually needs. Only meaningful on non-Windows terminals: Windows consoles don't read xterm escapes off
        /// stdin at all -- <see cref="TryDetectWindowsConsoleInputSupport"/> gets the equivalent behavior there by
        /// flipping <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c> (plus <c>ENABLE_MOUSE_INPUT</c>) via <c>SetConsoleMode</c>
        /// instead, on Windows Terminal specifically -- see that method's doc comment for why this doesn't extend
        /// to every Windows console host.
        /// </summary>
        private const string EnableMouseTracking = "\x1b[?1000h\x1b[?1006h";
        private const string DisableMouseTracking = "\x1b[?1000l\x1b[?1006l";

        /// <summary>
        /// Whether this run enables mouse-wheel scrolling at all. True unconditionally on non-Windows (an xterm-
        /// compatible terminal is assumed; a terminal that doesn't understand the escapes in
        /// <see cref="EnableMouseTracking"/> just never sends anything back, so scrolling silently falls back to
        /// keyboard-only). On Windows, only true once <see cref="TryDetectWindowsConsoleInputSupport"/> has
        /// confirmed both that this is a Windows Terminal session (see its doc comment for why that specifically,
        /// not just any Windows console, matters) and that <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c> actually takes
        /// effect when set -- older Windows Terminal builds or restricted environments that silently ignore the
        /// flag fall back to keyboard-only too, since forcing raw-escape parsing without the OS actually emitting
        /// escapes would just break navigation instead of adding the wheel to it.
        /// </summary>
        private readonly bool _supportsMouseWheel;

        /// <summary>
        /// The console's standard input handle, and its console mode before/after the tweaks
        /// <see cref="EnableMouseSupport"/>/<see cref="DisableMouseSupport"/> toggle -- populated only when
        /// <see cref="_supportsMouseWheel"/> is true on Windows; unused (and left default) on every other path,
        /// since non-Windows terminals need only the xterm escapes in <see cref="EnableMouseTracking"/>.
        /// </summary>
        private readonly SafeFileHandle? _windowsStdInHandle;
        private readonly Win32Console.CONSOLE_MODE _windowsOriginalConsoleMode;
        private readonly Win32Console.CONSOLE_MODE _windowsModifiedConsoleMode;

        /// <summary>
        /// Bytes read off <see cref="Console.In"/> by <see cref="_rawInputReaderThread"/>, drained by
        /// <see cref="ReadRawByte"/>. Only ever populated when <see cref="_supportsMouseWheel"/> is true --
        /// see <see cref="_rawInputReaderThread"/>'s doc comment for why a dedicated thread feeds this instead
        /// of each read spawning its own task.
        /// </summary>
        private readonly ConcurrentQueue<int> _rawInputQueue = new();

        /// <summary>
        /// A single long-lived background thread that owns every read from <see cref="Console.In"/> for this
        /// display's lifetime, publishing each byte into <see cref="_rawInputQueue"/>. Started once, lazily, the
        /// first time raw input is actually needed (<see cref="EnsureRawInputReaderStarted"/>). Never explicitly
        /// stopped -- see <see cref="StopRawInputReader"/>'s doc comment for why that's a deliberate choice, not
        /// an oversight: this thread instead simply outlives the display, blocked forever in its last
        /// <see cref="System.IO.TextReader.Read()"/> call. That's safe only because it's a single
        /// <see cref="Thread.IsBackground"/> thread (never more than one), so it can never keep the process
        /// itself from exiting -- unlike the unbounded thread-pool leak described below, which this design
        /// exists to eliminate.
        /// <para>
        /// Replaces an earlier design where <see cref="ReadRawByte"/> spawned a fresh <c>Task.Run(Console.In.Read)</c>
        /// on every single byte, bounded by a short timeout: whenever that read didn't complete within the bound
        /// (e.g. a lone Esc keypress, or the tail of an escape sequence a slow PTY hadn't finished delivering
        /// yet), the task was abandoned but its <c>Console.In.Read()</c> call kept running forever, permanently
        /// blocked inside <see cref="Console.In"/>'s internal synchronization lock. <see cref="Console.In"/> only
        /// lets one reader through that lock at a time, so every later abandoned read piled up queued behind the
        /// stuck ones instead of ever returning -- silently leaking one blocked thread-pool thread per timed-out
        /// read, for the rest of the run. On a long run with any scrolling at all (each wheel notch or arrow key
        /// is a multi-byte escape sequence, i.e. multiple <see cref="ReadRawByte"/> calls), those leaked threads
        /// accumulate and starve the very thread pool this run's own async test loop depends on to schedule its
        /// once-a-second continuation -- turning a table that should redraw every second into one that visibly
        /// stalls for many seconds at a time the longer the run goes on. A single dedicated thread makes exactly
        /// one blocking read call at a time, ever, so there is nothing left to leak.
        /// </para>
        /// </summary>
        private Thread? _rawInputReaderThread;

        private readonly string _runLabel;
        private readonly List<Row> _rows;
        private readonly Dictionary<int, Row> _rowsByPartitionIndex;
        private readonly LocalTestTimingHistory _history;

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

            /// <summary>Key into <see cref="LocalTestTimingHistory"/> -- see <see cref="LocalTestTimingHistory.GetKey"/>.</summary>
            internal required string HistoryKey { get; init; }

            /// <summary>How long this work item took the last time it passed on this machine, if known.</summary>
            internal TimeSpan? PreviousElapsed { get; init; }

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

        private LiveTestProgressDisplay(string runLabel, ImmutableArray<WorkItemInfo> workItems, LocalTestTimingHistory history)
        {
            _runLabel = runLabel;
            _history = history;

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

                // Always keyed by (baseName, tfmTag) regardless of whether Suffix ended up set above -- unlike
                // Suffix, which only disambiguates when *this run* has a duplicate, the persisted history must
                // stay stable across runs where the duplicate-ness of a given baseName can differ (e.g. a
                // TestRuntime.Both run schedules both TFMs, a single-TFM run doesn't).
                var historyKey = LocalTestTimingHistory.GetKey(baseName, tfmTag);
                unsortedRows.Add(new Row
                {
                    BaseName = baseName,
                    Suffix = suffix,
                    HistoryKey = historyKey,
                    PreviousElapsed = history.TryGetPreviousDuration(historyKey),
                });
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

            if (Console.IsInputRedirected)
            {
                // Nothing downstream will ever be able to read the wheel reports these escapes ask for --
                // PollKeyboardInput bails out immediately on redirected input every tick -- so turning mouse
                // tracking on here would just hijack wheel/click gestures on whatever the real terminal is
                // showing for the rest of the run (or, on Windows, leave GetConsoleMode/SetConsoleMode operating
                // on a handle that isn't a real console at all) for no benefit.
                _supportsMouseWheel = false;
            }
            else if (OperatingSystem.IsWindows())
            {
                (_supportsMouseWheel, _windowsStdInHandle, _windowsOriginalConsoleMode, _windowsModifiedConsoleMode) = TryDetectWindowsConsoleInputSupport();
            }
            else
            {
                _supportsMouseWheel = true;
            }

            if (_supportsMouseWheel)
            {
                EnsureRawInputReaderStarted();
            }
        }

        /// <summary>
        /// Starts <see cref="_rawInputReaderThread"/> the first (and only) time it's needed. Safe to call more
        /// than once -- only the constructor does today, but idempotency here is cheap insurance against that
        /// changing later.
        /// </summary>
        private void EnsureRawInputReaderStarted()
        {
            if (_rawInputReaderThread is not null)
            {
                return;
            }

            _rawInputReaderThread = new Thread(RawInputReaderLoop)
            {
                IsBackground = true,
                Name = "LiveTestProgressDisplay raw input reader",
            };
            _rawInputReaderThread.Start();
        }

        /// <summary>
        /// Body of <see cref="_rawInputReaderThread"/>: blocks on <see cref="Console.In"/> one byte at a time,
        /// forever, publishing each into <see cref="_rawInputQueue"/>. This is the only code anywhere in this
        /// class that ever calls <see cref="Console.In"/> directly -- exactly one blocking call in flight at a
        /// time, for this display's entire lifetime, is what makes the leak described on
        /// <see cref="_rawInputReaderThread"/> impossible. Exits (rather than spinning) on EOF or any read
        /// failure; the display just stops seeing further input from that point on, same as before.
        /// </summary>
        private void RawInputReaderLoop()
        {
            try
            {
                while (true)
                {
                    var b = Console.In.Read();
                    if (b < 0)
                    {
                        return;
                    }

                    _rawInputQueue.Enqueue(b);
                }
            }
            catch
            {
                // Best effort -- if Console.In itself becomes unusable mid-run, navigation just stops working
                // for the rest of it rather than taking the whole display down.
            }
        }

        /// <summary>
        /// Probes whether this Windows console actually honors <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c> -- the mode
        /// that makes <c>ReadFile</c>/<c>ReadConsole</c> on stdin emit the same xterm-style escape sequences
        /// (arrow keys, mouse reports) that <see cref="PollKeyboardAndMouseInputRaw"/> already knows how to parse
        /// for non-Windows terminals, so no separate Windows-specific input decoder is needed -- only a
        /// Windows-specific way of getting the OS to start emitting those bytes in the first place. Actually sets
        /// the mode, reads it back to confirm it stuck, then immediately reverts to the original mode (returned in
        /// the tuple for the real switch to use later) rather than trusting <c>SetConsoleMode</c>'s bare
        /// success return, because older Windows builds are documented to silently ignore unsupported mode bits
        /// instead of failing the call -- forcing raw-escape parsing on a console that never actually emits
        /// escapes would just break arrow-key navigation instead of adding the wheel to it. The real switch for
        /// this display's actual lifetime happens later via <see cref="EnableMouseSupport"/>/<see cref="DisableMouseSupport"/>.
        /// <para>
        /// UNVERIFIED: written and compiled on a Linux sandbox with no Windows host available -- CsWin32's source
        /// generator runs cross-platform, so this compiles cleanly here, but nothing in this method has executed
        /// against a real Windows Terminal session. Needs a real pass on Windows before it's trusted the way
        /// <c>Win32BreakawayProcessLauncher</c> (same caveat, same reason) eventually was.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static (bool Supported, SafeFileHandle? Handle, Win32Console.CONSOLE_MODE OriginalMode, Win32Console.CONSOLE_MODE ModifiedMode) TryDetectWindowsConsoleInputSupport()
        {
            try
            {
                // ENABLE_VIRTUAL_TERMINAL_INPUT + ENABLE_MOUSE_INPUT is confirmed (github.com/microsoft/terminal
                // issue #15296) to translate mouse/wheel events into VT sequences on stdin under Windows
                // Terminal -- but *not* under legacy conhost (a plain cmd.exe/powershell.exe console window not
                // hosted by Windows Terminal), which keeps delivering only native MOUSE_EVENT_RECORDs there
                // regardless of these mode bits, and PollKeyboardAndMouseInputRaw's stdin byte parser would never
                // see those. SetConsoleMode/GetConsoleMode round-tripping the flag below only proves the bit was
                // accepted, not that mouse events actually get funneled into the text stream -- there's no
                // official API to ask "will this console translate mouse to VT," so WT_SESSION (set by Windows
                // Terminal in every process it hosts) is the practical way to avoid confidently claiming wheel
                // support on a legacy conhost session where it would silently never fire.
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")))
                {
                    return (false, null, default, default);
                }

                var handle = PInvoke.GetStdHandle_SafeHandle(Win32Console.STD_HANDLE.STD_INPUT_HANDLE);
                if (handle.IsInvalid || !PInvoke.GetConsoleMode(handle, out var originalMode))
                {
                    return (false, null, default, default);
                }

                var addBits = Win32Console.CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_INPUT
                    | Win32Console.CONSOLE_MODE.ENABLE_MOUSE_INPUT
                    | Win32Console.CONSOLE_MODE.ENABLE_EXTENDED_FLAGS;
                var removeBits = Win32Console.CONSOLE_MODE.ENABLE_QUICK_EDIT_MODE
                    | Win32Console.CONSOLE_MODE.ENABLE_LINE_INPUT
                    | Win32Console.CONSOLE_MODE.ENABLE_ECHO_INPUT;
                var modifiedMode = (originalMode | addBits) & ~removeBits;

                if (!PInvoke.SetConsoleMode(handle, modifiedMode) ||
                    !PInvoke.GetConsoleMode(handle, out var confirmedMode) ||
                    (confirmedMode & Win32Console.CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_INPUT) == 0)
                {
                    PInvoke.SetConsoleMode(handle, originalMode);
                    return (false, null, default, default);
                }

                PInvoke.SetConsoleMode(handle, originalMode);
                return (true, handle, originalMode, modifiedMode);
            }
            catch
            {
                // Best effort -- any failure here just means Windows keyboard-only navigation instead of a crash.
                return (false, null, default, default);
            }
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

        internal static LiveTestProgressDisplay? TryCreate(string runLabel, ImmutableArray<WorkItemInfo> workItems, string? artifactsDirectory)
        {
            if (Console.IsOutputRedirected || workItems.Length == 0)
            {
                return null;
            }

            var enteredAltScreen = false;
            LiveTestProgressDisplay? display = null;
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

                // The constructor itself probes Windows mouse-wheel support (see TryDetectWindowsConsoleInputSupport);
                // EnableMouseSupport here does the actual (non-Windows escape write / Windows SetConsoleMode) switch
                // for the rest of this display's lifetime -- both no-op silently if support wasn't available.
                display = new LiveTestProgressDisplay(runLabel, workItems, LocalTestTimingHistory.Load(artifactsDirectory));
                display.EnableMouseSupport();
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
                StopRawInputReader();
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
                // Mouse support is terminal/console-wide, independent of which screen buffer is active, so it must
                // be turned back off here too -- otherwise it would keep hijacking the wheel (and clicks, and on
                // Windows the ordinary QuickEdit text-selection gesture) in the user's real scrollback/shell after
                // this display is done with it, not just while the table itself was on screen.
                DisableMouseSupport();

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

                // Only a genuine pass is worth remembering as the "Previous" baseline -- a timeout/failure's
                // elapsed time reflects however long it took to hang or crash, not how long the work item
                // actually takes to run.
                if (succeeded)
                {
                    _history.RecordPassed(row.HistoryKey, elapsed);
                }
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
                    EnableMouseSupport();
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
                    var (text, color) = lines[i];
                    var coloredText = color is { } c ? $"{GetAnsiColorCode(c)}{text}{ResetColor}" : text;

                    // The very last line must not end in a newline: the alternate screen is exactly `height` rows
                    // (0 through height-1), so writing one from the bottom row scrolls the whole grid up by one --
                    // shifting the title/counts off the top and leaving a blank row at the bottom, defeating the
                    // entire point of a fixed-size grid that never scrolls.
                    if (i == lines.Count - 1)
                    {
                        Console.Out.Write(coloredText);
                    }
                    else
                    {
                        Console.WriteLine(coloredText);
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
            StopRawInputReader();
        }

        /// <summary>
        /// Deliberately does <b>not</b> attempt to unblock <see cref="_rawInputReaderThread"/>'s pending
        /// <see cref="Console.In"/> read. <see cref="Console.In"/> is <see cref="System.IO.TextReader.Synchronized(System.IO.TextReader)"/>-
        /// wrapped, and that wrapper takes the same internal monitor for every call, including <c>Close()</c> --
        /// so calling <c>Console.In.Close()</c> from here (an earlier version of this method did exactly that)
        /// would itself block on that monitor for as long as the reader thread's <c>Read()</c> call holds it,
        /// which is until the next byte of real input arrives. Since this method runs synchronously inside
        /// <see cref="Complete"/>/<see cref="DisableAndExitAltScreen"/>, that would turn "stop the display" into
        /// "hang until the user presses another key" -- worse than the thread it was trying to clean up,
        /// especially after a whole-run timeout where nothing is left prompting for keystrokes at all.
        /// <see cref="System.IO.TextReader.Read()"/> has no cancellation token, and there is no way to safely interrupt a
        /// blocking read on a real console/terminal input handle out from under another thread on every
        /// platform this runs on. The thread is therefore simply abandoned once this display is done with it --
        /// safe only because it's a single <see cref="Thread.IsBackground"/> thread (never more than one, thanks
        /// to the dedicated-reader design this replaces per-byte <c>Task.Run</c> leaks with), so it can never
        /// keep the process itself from exiting, unlike the unbounded thread-pool leak the per-byte design risked.
        /// </summary>
        private void StopRawInputReader()
        {
            // Intentionally a no-op beyond this comment -- see the doc comment above for why actually closing
            // Console.In here would risk a worse bug (a synchronous hang in Complete()/DisableAndExitAltScreen())
            // than the one leaked background thread it would have cleaned up.
        }

        /// <summary>
        /// Turns on whatever this platform needs for <see cref="PollKeyboardAndMouseInputRaw"/> to start seeing
        /// mouse-wheel escape bytes on stdin -- the xterm escapes in <see cref="EnableMouseTracking"/> on
        /// non-Windows, or flipping the previously-verified <see cref="_windowsModifiedConsoleMode"/> on via
        /// <c>SetConsoleMode</c> on Windows. A no-op wherever <see cref="_supportsMouseWheel"/> is false. Paired
        /// with <see cref="DisableMouseSupport"/> around every alt-screen enter/exit (see
        /// <see cref="ExitAltScreenIfActive"/>, <see cref="RedrawCore"/>) the same way the alt-screen escapes
        /// themselves are, so a <see cref="PrepareForExtraOutput"/> pause doesn't leave mouse/console-mode capture
        /// hijacking the user's real scrollback while diagnostics print there.
        /// </summary>
        private void EnableMouseSupport()
        {
            if (!_supportsMouseWheel)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                PInvoke.SetConsoleMode(_windowsStdInHandle!, _windowsModifiedConsoleMode);
            }
            else
            {
                Console.Out.Write(EnableMouseTracking);
            }
        }

        /// <summary>Reverts whatever <see cref="EnableMouseSupport"/> turned on. See its doc comment for the pairing.</summary>
        private void DisableMouseSupport()
        {
            if (!_supportsMouseWheel)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                PInvoke.SetConsoleMode(_windowsStdInHandle!, _windowsOriginalConsoleMode);
            }
            else
            {
                Console.Out.Write(DisableMouseTracking);
            }
        }

        /// <summary>
        /// Reads and applies any input the user has produced since the last redraw -- keystrokes on every
        /// platform, plus mouse-wheel scroll notches wherever <see cref="_supportsMouseWheel"/> allows it -- so
        /// the row window can be scrolled by hand (like freezing a spreadsheet's header row and scrolling the body
        /// underneath) instead of only ever auto-following whichever row is running/queued. Non-blocking -- drains
        /// whatever's already buffered via <see cref="Console.KeyAvailable"/> rather than waiting for input -- and
        /// best-effort: a host that doesn't support input polling (e.g. input redirected, or a probe that throws)
        /// just leaves navigation disabled for the rest of the run rather than taking the whole display down with
        /// it.
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
                if (_supportsMouseWheel)
                {
                    PollKeyboardAndMouseInputRaw(maxScrollStart);
                }
                else
                {
                    while (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true).Key;
                        _manualScrollStart = ApplyNavigationKey(key, _manualScrollStart, _lastScrollStart, maxScrollStart);
                    }
                }
            }
            catch
            {
                // Best effort -- if input polling isn't usable here, the auto-following window still works fine
                // without it; there's nothing more useful to do than skip navigation for the rest of the run.
            }
        }

        /// <summary>
        /// Counterpart to the plain <see cref="Console.ReadKey()"/> loop in <see cref="PollKeyboardInput"/> used
        /// whenever <see cref="_supportsMouseWheel"/> is true -- reads genuinely raw, undecoded bytes via
        /// <see cref="ReadRawByte"/> instead of <see cref="Console.ReadKey()"/>'s own decoding, because that method
        /// only ever matches escape sequences against its own fixed table of *key* sequences, silently discarding
        /// (or mis-decoding, one leftover character at a time) anything outside it -- like a mouse report -- so it
        /// can never surface wheel input no matter how mouse tracking is configured upstream, and on Unix it
        /// collapses e.g. <c>ESC [ A</c> into a single <see cref="ConsoleKey.UpArrow"/> before this parser ever
        /// sees the individual bytes. This re-implements just enough of the arrow-key decoding
        /// ReadKey normally provides for free, since going around its decoding here means going around it for
        /// every key on this platform, not only the mouse-specific bytes. See <see cref="ReadRawByte"/>'s own doc
        /// comment for how it stays safe against blocking despite reading through <see cref="Console.In"/>, which
        /// isn't guaranteed to pair safely with the <see cref="Console.KeyAvailable"/> checks this loop and
        /// <see cref="WaitForMoreInput"/> depend on.
        /// </summary>
        private void PollKeyboardAndMouseInputRaw(int maxScrollStart)
        {
            while (!_rawInputQueue.IsEmpty)
            {
                var first = ReadRawByte();
                if (first != 0x1b)
                {
                    // Not part of an escape sequence -- nothing this display reacts to is a bare character.
                    continue;
                }

                // A terminal almost always delivers a whole escape sequence in one write(), so the rest of it is
                // typically sitting in the buffer already -- but a PTY is just a byte stream with no message
                // boundaries, so a slow link or an unusually scheduled terminal process could still deliver it in
                // pieces. WaitForMoreInput gives a split delivery a brief bounded window to catch up before this
                // gives up and treats "nothing showed up" as a real, bare Esc keypress (which resumes auto-follow,
                // same as on Windows).
                if (!WaitForMoreInput())
                {
                    _manualScrollStart = null;
                    continue;
                }

                if (ReadRawByte() != '[' || !WaitForMoreInput())
                {
                    continue;
                }

                var third = ReadRawByte();
                if (third == '<')
                {
                    if (TryParseSgrMouseWheel(out var wheelDown))
                    {
                        var current = _manualScrollStart ?? _lastScrollStart;
                        _manualScrollStart = Math.Clamp(current + (wheelDown ? 1 : -1), 0, maxScrollStart);
                    }

                    continue;
                }

                var key = third switch
                {
                    'A' => ConsoleKey.UpArrow,
                    'B' => ConsoleKey.DownArrow,
                    _ => (ConsoleKey?)null,
                };

                if (key is { } navigationKey)
                {
                    _manualScrollStart = ApplyNavigationKey(navigationKey, _manualScrollStart, _lastScrollStart, maxScrollStart);
                }
            }
        }

        /// <summary>
        /// Dequeues a single genuinely raw, undecoded byte already read off <see cref="Console.In"/> by
        /// <see cref="_rawInputReaderThread"/> -- unlike <see cref="Console.ReadKey()"/>, which is not usable
        /// here: on Unix it applies its own terminfo-based key-sequence decoding, collapsing e.g. <c>ESC [ A</c>
        /// into a single <see cref="ConsoleKey.UpArrow"/> <see cref="ConsoleKeyInfo"/> with
        /// <see cref="ConsoleKeyInfo.KeyChar"/> <c>'\0'</c> instead of surfacing the individual <c>ESC</c>/<c>[</c>/<c>A</c>
        /// bytes this parser depends on seeing one at a time, and it has no table entry for a mouse report at all
        /// -- so unrecognized sequences would be consumed or misdecoded unpredictably, exactly the reason this
        /// class avoided <see cref="Console.ReadKey()"/> in the first place.
        /// <para>
        /// Never blocks: <see cref="_rawInputReaderThread"/> is the only thing that ever calls into
        /// <see cref="Console.In"/>, so this only ever touches the lock-free <see cref="_rawInputQueue"/>. Callers
        /// that need to wait for a byte that hasn't arrived yet use <see cref="WaitForMoreInput"/> first; this
        /// returns <c>-1</c> immediately if the queue is empty, same contract as the old bounded-wait version had
        /// on timeout.
        /// </para>
        /// </summary>
        private int ReadRawByte()
            => _rawInputQueue.TryDequeue(out var b) ? b : -1;

        /// <summary>
        /// Bounded wait for more input to show up, used everywhere <see cref="PollKeyboardAndMouseInputRaw"/> and
        /// its helper (<see cref="TryParseSgrMouseWheel"/>) are partway
        /// through a multi-byte escape sequence and need to know whether the next byte is really not coming, or
        /// just hasn't arrived on the wire yet. A single non-empty-queue check alone can't tell those apart -- a
        /// PTY is a byte stream with no message boundaries, so nothing guarantees a terminal's write() lands in
        /// one piece by the time <see cref="_rawInputReaderThread"/> has drained it into <see cref="_rawInputQueue"/>,
        /// even though it almost always does locally. Blocking here (rather than leaving it to the *next* redraw
        /// tick, roughly a second later) keeps a merely-slow-to-arrive sequence from being torn in half -- its
        /// already-read prefix discarded or misparsed, and its second half misread as bare characters on the next
        /// poll -- instead of correctly waiting the extra few milliseconds for the rest to show up. The wait
        /// itself only ever spins this calling thread (never blocks on <see cref="Console.In"/>), so it can't
        /// contribute to the thread-pool leak <see cref="_rawInputReaderThread"/>'s doc comment describes.
        /// </summary>
        private bool WaitForMoreInput()
        {
            if (!_rawInputQueue.IsEmpty)
            {
                return true;
            }

            var deadline = Environment.TickCount64 + EscapeSequenceCompletionTimeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (!_rawInputQueue.IsEmpty)
                {
                    return true;
                }

                Thread.Sleep(1);
            }

            return !_rawInputQueue.IsEmpty;
        }

        /// <summary>
        /// Parses the remainder of an SGR mouse report (<c>Pb;Px;Py</c> followed by <c>M</c> or <c>m</c>),
        /// positioned just past the leading <c>ESC [ &lt;</c>, extracting only what wheel scrolling needs: bit
        /// <c>0x40</c> of the button code <c>Pb</c> is xterm's marker for a wheel event specifically (as opposed
        /// to an ordinary button click/drag/release), with the low bit then distinguishing up from down --
        /// independent of whatever Shift/Meta/Ctrl modifier bits are also folded into the same byte, which is why
        /// masking rather than an exact equality check against 64/65 is what's tested. Coordinates are read past
        /// but otherwise ignored -- nothing here is scoped to where in the terminal the wheel moved.
        /// </summary>
        private bool TryParseSgrMouseWheel(out bool wheelDown)
        {
            wheelDown = false;
            var buffer = new StringBuilder();
            while (WaitForMoreInput())
            {
                var c = ReadRawByte();
                if (c < 0)
                {
                    return false;
                }

                if (c is 'M' or 'm')
                {
                    break;
                }

                buffer.Append((char)c);
            }

            return TryGetWheelDirection(buffer.ToString(), out wheelDown);
        }

        /// <summary>
        /// The pure button-code-to-direction decision behind <see cref="TryParseSgrMouseWheel"/>, split out (like
        /// <see cref="ApplyNavigationKey"/>) so it's directly unit-testable without a real console. Bit
        /// <c>0x40</c> of the leading <c>Pb</c> field is xterm's marker for a wheel-class event generally (as
        /// opposed to an ordinary button click/drag/release, which this returns <see langword="false"/> for) --
        /// but that class covers four directions, not two: bit <c>0x02</c> then distinguishes horizontal
        /// tilt/scroll (buttons 66/67, from a trackpad or a tilt wheel) from the vertical wheel this table actually
        /// scrolls with (buttons 64/65), and only once that's ruled out does the low bit distinguish up from down.
        /// All of this is independent of whatever Shift/Meta/Ctrl modifier bits are also folded into the same
        /// byte (they occupy separate bits from both wheel markers), which is why masking rather than an exact
        /// equality check against 64/65 is what's tested. <paramref name="sgrParameters"/> is the raw
        /// <c>Pb;Px;Py</c> text read off the wire; only the first (button) field is used -- coordinates are
        /// irrelevant to a wheel-only feature.
        /// </summary>
        internal static bool TryGetWheelDirection(string sgrParameters, out bool wheelDown)
        {
            wheelDown = false;
            var buttonField = sgrParameters.Split(';')[0];
            if (!int.TryParse(buttonField, out var buttonCode))
            {
                return false;
            }

            const int WheelClassBit = 0x40;
            const int HorizontalBit = 0x02;
            const int DirectionBit = 0x01;

            if ((buttonCode & WheelClassBit) == 0 || (buttonCode & HorizontalBit) != 0)
            {
                // Either not a wheel event at all, or a horizontal tilt/scroll -- this table only scrolls vertically.
                return false;
            }

            wheelDown = (buttonCode & DirectionBit) != 0;
            return true;
        }

        /// <summary>
        /// The pure key-to-scroll-position mapping behind <see cref="PollKeyboardInput"/>, split out (like
        /// <see cref="ComputeScrollStart(int, int, int, int, int)"/>) so it's directly unit-testable without a real
        /// console. Returns the new <see cref="_manualScrollStart"/> value for a recognized navigation key, or the
        /// unchanged <paramref name="previousManualScrollStart"/> for any other key.
        /// </summary>
        internal static int? ApplyNavigationKey(ConsoleKey key, int? previousManualScrollStart, int lastScrollStart, int maxScrollStart)
        {
            var current = previousManualScrollStart ?? lastScrollStart;
            return key switch
            {
                ConsoleKey.UpArrow => Math.Clamp(current - 1, 0, maxScrollStart),
                ConsoleKey.DownArrow => Math.Clamp(current + 1, 0, maxScrollStart),
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

        /// <summary>
        /// The row-line foreground color for a completed status, or <see langword="null"/> for
        /// <see cref="LiveRowStatus.Queued"/>/<see cref="LiveRowStatus.Running"/> (drawn in the terminal's normal
        /// color -- there's nothing to flag yet).
        /// </summary>
        internal static ConsoleColor? GetRowColor(LiveRowStatus status) => status switch
        {
            LiveRowStatus.Passed => ConsoleColor.Green,
            LiveRowStatus.Timeout => ConsoleColor.Yellow,
            LiveRowStatus.Failed => ConsoleColor.Red,
            _ => null,
        };

        private List<(string Text, ConsoleColor? Color)> BuildFrameLines(int width, int height)
        {
            var runningCount = _rows.Count(static r => r.Status == LiveRowStatus.Running);
            var queuedCount = _rows.Count(static r => r.Status == LiveRowStatus.Queued);
            var passedCount = _rows.Count(static r => r.Status == LiveRowStatus.Passed);
            var attentionCount = _rows.Count(static r => r.Status is LiveRowStatus.Failed or LiveRowStatus.Timeout);

            var fixedOverhead = Indent.Length + ColumnGap.Length + TestResultDisplay.StatusColumnWidth + ColumnGap.Length + TestResultDisplay.ElapsedColumnWidth + ColumnGap.Length + TestResultDisplay.ElapsedColumnWidth;
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
                var scrollHint = _supportsMouseWheel ? "scroll wheel / ↑↓" : "↑↓";
                titleLine += _manualScrollStart is not null
                    ? $"    [{scrollHint} to scroll, Esc to follow]"
                    : $"    [{scrollHint} to scroll]";
            }

            var lines = new List<(string Text, ConsoleColor? Color)>(height)
            {
                (FitToWidth(titleLine, width), null),
                (string.Empty, null),
                (FitToWidth($"{Indent}{"Test Assembly".PadRight(nameColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad("Status", TestResultDisplay.StatusColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad("Elapsed", TestResultDisplay.ElapsedColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad("Previous", TestResultDisplay.ElapsedColumnWidth)}", width), null),
                // The Status underline fills its whole column (like the Test Assembly one) -- it only reads as
                // "one dash past the word" because the centered header text is inset from the column edges. The
                // Elapsed/Previous underlines are different: the word's length plus one extra dash on each side,
                // never the full (wider, HH:mm:ss-sized) column, centered within it same as the data.
                (FitToWidth($"{Indent}{new string('-', nameColumnWidth)}{ColumnGap}{new string('-', TestResultDisplay.StatusColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad(new string('-', "Elapsed".Length + 2), TestResultDisplay.ElapsedColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad(new string('-', "Previous".Length + 2), TestResultDisplay.ElapsedColumnWidth)}", width), null),
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
                var previousText = row.PreviousElapsed is { } previousElapsed ? TestResultDisplay.FormatElapsed(previousElapsed) : "--:--";

                var line = $"{Indent}{name.PadRight(nameColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad(statusText, TestResultDisplay.StatusColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad(elapsedText, TestResultDisplay.ElapsedColumnWidth)}{ColumnGap}{TestResultDisplay.CenterPad(previousText, TestResultDisplay.ElapsedColumnWidth)}";
                lines.Add((FitToWidth(line, width), GetRowColor(row.Status)));
            }

            // Pad to exactly `height` lines so every redraw fully overwrites the whole alternate-screen grid,
            // even when fewer rows are visible than the budget allows (a short run) or the window just grew.
            while (lines.Count < height)
            {
                lines.Add((new string(' ', width), null));
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
