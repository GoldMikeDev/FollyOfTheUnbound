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
        internal const int StatusColumnWidth = 9;
        internal const int ElapsedColumnWidth = 7;

        internal static string GetStatusText(bool succeeded, bool isTimeout)
            => succeeded ? "PASSED" : isTimeout ? "TIMEOUT" : "FAILED";

        /// <summary>Formats as compact <c>mm:ss</c>, or <c>h:mm:ss</c> past an hour -- fixed-width-friendly, unlike <see cref="TimeSpan"/>'s own default (variable-length, fractional-second) <c>ToString()</c>.</summary>
        internal static string FormatElapsed(TimeSpan elapsed)
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
        /// Fits <paramref name="name"/> to exactly <paramref name="width"/> columns: truncated with a trailing
        /// ellipsis if too long (so a long name can never push later columns out of alignment), or padded if
        /// shorter (so every row's later columns start at the same fixed position regardless of name length).
        /// </summary>
        internal static string FitName(string name, int width)
        {
            if (name.Length > width)
            {
                return name[..Math.Max(width - 1, 0)] + "…";
            }

            return name.PadRight(width);
        }
    }
}
