// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace RunTests
{
    /// <summary>
    /// Shared column widths and formatting for RunTests' test-result tables -- both
    /// <see cref="LiveTestProgressDisplay"/>'s in-place live table and <see cref="TestRunner"/>'s own final
    /// PASSED/FAILED/TIMEOUT summary -- so the two read consistently instead of drifting apart, and so neither
    /// implements its own copy of "what does a fixed-width, non-wrapping status/elapsed column look like".
    /// </summary>
    internal static class TestResultDisplay
    {
        internal const int StatusColumnWidth = 10;

        /// <summary>
        /// Wide enough for <see cref="FormatElapsed"/>'s longest possible output, <c>"99:59:59"</c> (8 chars),
        /// plus 2 extra columns of breathing room -- <see cref="FormatElapsed"/> itself caps displayed hours at 99
        /// specifically so its output can never exceed 8 characters, even though nothing enforces an upper bound
        /// on how long a single work item can run (the whole-run <c>--timeout</c> watchdog is optional, and
        /// per-item vstest hang detection is a much longer 15-25 minutes).
        /// </summary>
        internal const int ElapsedColumnWidth = 10;

        internal static string GetStatusText(bool succeeded, bool isTimeout)
            => succeeded ? "PASSED" : isTimeout ? "TIMEOUT" : "FAILED";

        /// <summary>
        /// Centers <paramref name="text"/> within <paramref name="width"/> columns, splitting any leftover
        /// padding with the extra space on the right when it can't be split evenly. If <paramref name="text"/> is
        /// already at least <paramref name="width"/> long, it's returned unchanged if exactly that width, or
        /// truncated (no ellipsis -- callers only ever pass known-short labels/values here, never arbitrary
        /// user-facing names) if longer.
        /// </summary>
        internal static string CenterPad(string text, int width)
        {
            if (text.Length >= width)
            {
                return text.Length == width ? text : text[..width];
            }

            var totalPad = width - text.Length;
            var left = totalPad / 2;
            var right = totalPad - left;
            return new string(' ', left) + text + new string(' ', right);
        }

        /// <summary>
        /// Formats as compact <c>mm:ss</c>, or <c>HH:mm:ss</c> past an hour -- fixed-width-friendly (always at
        /// most <see cref="ElapsedColumnWidth"/> characters), unlike <see cref="TimeSpan"/>'s own default
        /// (variable-length, fractional-second) <c>ToString()</c>.
        /// </summary>
        internal static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            if (elapsed.TotalHours < 1)
            {
                return $"{elapsed:mm\\:ss}";
            }

            // Capped (rather than left to grow to 3+ digits for a pathologically long-running work item) so this
            // can never exceed ElapsedColumnWidth -- a run that's actually been going for over 99 hours has
            // bigger problems than a display width overflow.
            var hours = Math.Min((int)elapsed.TotalHours, 99);
            return $"{hours:00}:{elapsed:mm\\:ss}";
        }

        /// <summary>
        /// Fits <paramref name="name"/> to exactly <paramref name="width"/> columns: truncated with a trailing
        /// ellipsis if too long (so a long name can never push later columns out of alignment), or padded if
        /// shorter (so every row's later columns start at the same fixed position regardless of name length).
        /// A trailing <c>_&lt;digits&gt;</c> Helix work-item partition suffix (see
        /// <see cref="WorkItemInfo.DisplayName"/>) is preserved through truncation rather than trimmed away --
        /// it's what disambiguates two otherwise-identical names (e.g. the same assembly scheduled once per
        /// target framework under <see cref="TestRuntime.Both"/>), so losing it could make unrelated work items
        /// display identically.
        /// </summary>
        internal static string FitName(string name, int width)
        {
            if (name.Length <= width)
            {
                return name.PadRight(width);
            }

            var suffixStart = FindTrailingPartitionSuffixStart(name);
            var suffix = name[suffixStart..];
            var prefix = name[..suffixStart];

            var availableForPrefix = Math.Max(width - suffix.Length, 1);
            var truncatedPrefix = prefix.Length > availableForPrefix
                ? prefix[..Math.Max(availableForPrefix - 1, 0)] + "…"
                : prefix;

            var result = truncatedPrefix + suffix;

            // Hard safety net for a pathologically narrow width where even a 1-character ellipsis-only prefix
            // plus the suffix still doesn't fit (e.g. the suffix alone is longer than width).
            return result.Length <= width ? result : result[..width];
        }

        /// <summary>Index where a trailing <c>_&lt;digits&gt;</c> suffix begins (including the underscore), or <c>name.Length</c> if there is none.</summary>
        private static int FindTrailingPartitionSuffixStart(string name)
        {
            var lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore < 0 || lastUnderscore == name.Length - 1)
            {
                return name.Length;
            }

            for (var i = lastUnderscore + 1; i < name.Length; i++)
            {
                if (!char.IsAsciiDigit(name[i]))
                {
                    return name.Length;
                }
            }

            return lastUnderscore;
        }
    }
}
