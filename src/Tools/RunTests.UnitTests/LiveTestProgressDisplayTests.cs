// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Xunit;

namespace RunTests.UnitTests
{
    public sealed class LiveTestProgressDisplayTests
    {
        [Fact]
        public void ComputeScrollStart_EverythingFits_AlwaysZero()
        {
            // 10 rows, budget 10 -> maxScrollStart is 0, regardless of focus or previous position.
            Assert.Equal(0, LiveTestProgressDisplay.ComputeScrollStart(rowCount: 10, visibleRowBudget: 10, previousScrollStart: 3, firstRunningIndex: 7, firstQueuedIndex: 9));
        }

        [Fact]
        public void ComputeScrollStart_EverythingFits_EvenWithNoFocus()
        {
            Assert.Equal(0, LiveTestProgressDisplay.ComputeScrollStart(rowCount: 5, visibleRowBudget: 20, previousScrollStart: 0, firstRunningIndex: -1, firstQueuedIndex: -1));
        }

        [Fact]
        public void ComputeScrollStart_RunningFocusOutsideView_CentersOnRunningRow()
        {
            // 100 rows, budget 10, previous position 0 -- a running row at index 50 is nowhere near view.
            var result = LiveTestProgressDisplay.ComputeScrollStart(rowCount: 100, visibleRowBudget: 10, previousScrollStart: 0, firstRunningIndex: 50, firstQueuedIndex: 60);
            Assert.Equal(45, result); // 50 - 10/2
        }

        [Fact]
        public void ComputeScrollStart_PrefersRunningOverQueued()
        {
            var result = LiveTestProgressDisplay.ComputeScrollStart(rowCount: 100, visibleRowBudget: 10, previousScrollStart: 0, firstRunningIndex: 50, firstQueuedIndex: 5);
            Assert.Equal(45, result); // centered on the running row (50), not the queued one (5)
        }

        [Fact]
        public void ComputeScrollStart_NoRunning_FallsBackToQueued()
        {
            var result = LiveTestProgressDisplay.ComputeScrollStart(rowCount: 100, visibleRowBudget: 10, previousScrollStart: 0, firstRunningIndex: -1, firstQueuedIndex: 70);
            Assert.Equal(65, result); // 70 - 10/2
        }

        [Fact]
        public void ComputeScrollStart_NothingRunningOrQueued_HoldsPreviousPosition()
        {
            // The run is effectively done -- don't snap back to the top or anywhere else.
            var result = LiveTestProgressDisplay.ComputeScrollStart(rowCount: 100, visibleRowBudget: 10, previousScrollStart: 42, firstRunningIndex: -1, firstQueuedIndex: -1);
            Assert.Equal(42, result);
        }

        [Fact]
        public void ComputeScrollStart_NothingRunningOrQueued_ClampsHeldPositionToValidRange()
        {
            // previousScrollStart from a wider window (before a resize shrank maxScrollStart) must still clamp.
            var result = LiveTestProgressDisplay.ComputeScrollStart(rowCount: 20, visibleRowBudget: 10, previousScrollStart: 999, firstRunningIndex: -1, firstQueuedIndex: -1);
            Assert.Equal(10, result); // maxScrollStart = 20 - 10
        }

        [Fact]
        public void ComputeScrollStart_FocusAlreadyVisible_HoldsPositionInsteadOfRecentering()
        {
            // previousScrollStart 40 shows rows [40, 50); a running row at 45 is already visible, so the window
            // should not jump to recenter on it (45 - 5 = 40, which happens to match here, so use a case where
            // recentering would actually move the window to make the assertion meaningful).
            var result = LiveTestProgressDisplay.ComputeScrollStart(rowCount: 100, visibleRowBudget: 10, previousScrollStart: 35, firstRunningIndex: 42, firstQueuedIndex: -1);
            Assert.Equal(35, result); // 42 is within [35, 45) -- held, not recentered to 42 - 5 = 37
        }

        [Fact]
        public void ComputeScrollStart_FocusJustOutsideVisibleRange_Recenters()
        {
            // previousScrollStart 35 shows rows [35, 45); a running row at 45 is just past the end.
            var result = LiveTestProgressDisplay.ComputeScrollStart(rowCount: 100, visibleRowBudget: 10, previousScrollStart: 35, firstRunningIndex: 45, firstQueuedIndex: -1);
            Assert.Equal(40, result); // 45 - 10/2
        }

        [Fact]
        public void ComputeScrollStart_FocusNearStart_ClampsToZero()
        {
            var result = LiveTestProgressDisplay.ComputeScrollStart(rowCount: 100, visibleRowBudget: 10, previousScrollStart: 50, firstRunningIndex: 1, firstQueuedIndex: -1);
            Assert.Equal(0, result); // 1 - 10/2 would be negative
        }

        [Fact]
        public void ComputeScrollStart_FocusNearEnd_ClampsToMaxScrollStart()
        {
            var result = LiveTestProgressDisplay.ComputeScrollStart(rowCount: 100, visibleRowBudget: 10, previousScrollStart: 0, firstRunningIndex: 98, firstQueuedIndex: -1);
            Assert.Equal(90, result); // maxScrollStart = 100 - 10; 98 - 5 = 93 would exceed it
        }

        [Fact]
        public void ApplyNavigationKey_DownArrow_AdvancesOneRowFromLastScrollStart()
        {
            var result = LiveTestProgressDisplay.ApplyNavigationKey(ConsoleKey.DownArrow, previousManualScrollStart: null, lastScrollStart: 5, visibleRowBudget: 10, maxScrollStart: 50);
            Assert.Equal(6, result);
        }

        [Fact]
        public void ApplyNavigationKey_UpArrow_StepsBackFromExistingManualPosition()
        {
            var result = LiveTestProgressDisplay.ApplyNavigationKey(ConsoleKey.UpArrow, previousManualScrollStart: 20, lastScrollStart: 5, visibleRowBudget: 10, maxScrollStart: 50);
            Assert.Equal(19, result);
        }

        [Fact]
        public void ApplyNavigationKey_UpArrow_ClampsToZero()
        {
            var result = LiveTestProgressDisplay.ApplyNavigationKey(ConsoleKey.UpArrow, previousManualScrollStart: 0, lastScrollStart: 0, visibleRowBudget: 10, maxScrollStart: 50);
            Assert.Equal(0, result);
        }

        [Fact]
        public void ApplyNavigationKey_PageDown_AdvancesByFullBudget_ClampedToMax()
        {
            var result = LiveTestProgressDisplay.ApplyNavigationKey(ConsoleKey.PageDown, previousManualScrollStart: 45, lastScrollStart: 45, visibleRowBudget: 10, maxScrollStart: 50);
            Assert.Equal(50, result); // 45 + 10 = 55 would exceed maxScrollStart
        }

        [Fact]
        public void ApplyNavigationKey_Home_JumpsToZero()
        {
            var result = LiveTestProgressDisplay.ApplyNavigationKey(ConsoleKey.Home, previousManualScrollStart: 30, lastScrollStart: 30, visibleRowBudget: 10, maxScrollStart: 50);
            Assert.Equal(0, result);
        }

        [Fact]
        public void ApplyNavigationKey_End_JumpsToMaxScrollStart()
        {
            var result = LiveTestProgressDisplay.ApplyNavigationKey(ConsoleKey.End, previousManualScrollStart: 0, lastScrollStart: 0, visibleRowBudget: 10, maxScrollStart: 50);
            Assert.Equal(50, result);
        }

        [Fact]
        public void ApplyNavigationKey_Escape_ReturnsToAutoFollow()
        {
            var result = LiveTestProgressDisplay.ApplyNavigationKey(ConsoleKey.Escape, previousManualScrollStart: 30, lastScrollStart: 30, visibleRowBudget: 10, maxScrollStart: 50);
            Assert.Null(result);
        }

        [Fact]
        public void ApplyNavigationKey_UnrecognizedKey_LeavesPositionUnchanged()
        {
            var result = LiveTestProgressDisplay.ApplyNavigationKey(ConsoleKey.A, previousManualScrollStart: 12, lastScrollStart: 5, visibleRowBudget: 10, maxScrollStart: 50);
            Assert.Equal(12, result);
        }

        [Fact]
        public void ElapsedColumnWidth_IsTwoWiderThanUnderlineWithSideDashes()
        {
            // The header underline is "Elapsed".Length + 2 (one extra dash each side); the column itself must
            // still be at least that wide to center it without truncation.
            Assert.True(TestResultDisplay.ElapsedColumnWidth >= "Elapsed".Length + 2);
        }
    }
}
