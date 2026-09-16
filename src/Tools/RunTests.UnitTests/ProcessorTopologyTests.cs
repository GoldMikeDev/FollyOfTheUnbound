// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace RunTests.UnitTests
{
    // Exercises ProcessorTopology.CountParkedLogicalProcessors against synthetic
    // SYSTEM_CPU_SET_INFORMATION buffers, since live parking state depends on real hardware and
    // current system load, neither of which a test can control.
    public sealed class ProcessorTopologyTests
    {
        private const int CpuSetInformationType = 0;
        private const int OtherRelationshipType = 1;
        private const byte ParkedFlag = 0x1;
        private const byte AllocatedFlag = 0x2;
        private const byte AllocatedToTargetProcessFlag = 0x4;

        private const int EntrySize = 32; // sizeof(SYSTEM_CPU_SET_INFORMATION)

        private static byte[] BuildBuffer(params (int Type, byte Flags)[] entries)
            => entries.SelectMany(e => BuildEntry(e.Type, e.Flags, group: 0, logicalProcessorIndex: 0)).ToArray();

        private static byte[] BuildBuffer(params (int Type, byte Flags, byte LogicalProcessorIndex)[] entries)
            => entries.SelectMany(e => BuildEntry(e.Type, e.Flags, group: 0, e.LogicalProcessorIndex)).ToArray();

        private static byte[] BuildBuffer(params (int Type, byte Flags, ushort Group, byte LogicalProcessorIndex)[] entries)
            => entries.SelectMany(e => BuildEntry(e.Type, e.Flags, e.Group, e.LogicalProcessorIndex)).ToArray();

        private static byte[] BuildEntry(int type, byte flags, ushort group, byte logicalProcessorIndex)
        {
            var entry = new byte[EntrySize];
            BitConverter.GetBytes((uint)EntrySize).CopyTo(entry, 0); // Size
            BitConverter.GetBytes(type).CopyTo(entry, 4); // Type
            BitConverter.GetBytes(group).CopyTo(entry, 12); // Group
            entry[14] = logicalProcessorIndex; // LogicalProcessorIndex
            entry[19] = flags; // AllFlags (bit 0 == Parked)
            return entry;
        }

        [Fact]
        public void EmptyBuffer_ReturnsZero()
        {
            Assert.Equal(0, ProcessorTopology.CountParkedLogicalProcessors(ReadOnlySpan<byte>.Empty));
        }

        [Fact]
        public void NoParkedProcessors_ReturnsZero()
        {
            // The common, unrestricted case: no entry has AllocatedToTargetProcess set at all.
            var buffer = BuildBuffer(
                (CpuSetInformationType, AllocatedFlag),
                (CpuSetInformationType, AllocatedFlag),
                (CpuSetInformationType, 0));

            Assert.Equal(0, ProcessorTopology.CountParkedLogicalProcessors(buffer));
        }

        [Fact]
        public void CountsOnlyEntriesWithTheParkedBitSet()
        {
            // 6 logical processors, none explicitly allocated (the common, unrestricted case): 4 active (2
            // of which are also allocated -- an unrelated flag that must not be mistaken for Parked), 2 parked.
            var buffer = BuildBuffer(
                (CpuSetInformationType, 0),
                (CpuSetInformationType, AllocatedFlag),
                (CpuSetInformationType, AllocatedFlag),
                (CpuSetInformationType, 0),
                (CpuSetInformationType, ParkedFlag),
                (CpuSetInformationType, (byte)(ParkedFlag | AllocatedFlag)));

            Assert.Equal(2, ProcessorTopology.CountParkedLogicalProcessors(buffer));
        }

        [Fact]
        public void IgnoresNonCpuSetEntryTypes()
        {
            // A non-CpuSetInformation entry that happens to have the same bits set at the Flags offset
            // must not be counted -- only Type == CpuSetInformation entries represent a logical processor.
            var buffer = BuildBuffer(
                (CpuSetInformationType, ParkedFlag),
                (OtherRelationshipType, ParkedFlag));

            Assert.Equal(1, ProcessorTopology.CountParkedLogicalProcessors(buffer));
        }

        [Fact]
        public void UnrestrictedProcess_CountsEveryParkedEntry_EvenThoughNoneAreFlaggedAllocated()
        {
            // AllocatedToTargetProcess is true only for a CPU set the process was explicitly restricted to
            // (SetProcessDefaultCpuSets, a CPU-set-aware job object limit, etc) -- an ordinary process (the
            // overwhelmingly common scry case) has it false on every entry, including CPUs it can freely run
            // on. Filtering to only flagged entries in that case would count zero parked processors and
            // silently disable the hybrid-CPU fix entirely, so with no entry flagged, every parked entry
            // counts.
            var buffer = BuildBuffer(
                (CpuSetInformationType, 0),
                (CpuSetInformationType, ParkedFlag),
                (CpuSetInformationType, ParkedFlag));

            Assert.Equal(2, ProcessorTopology.CountParkedLogicalProcessors(buffer));
        }

        [Fact]
        public void ExplicitlyRestrictedProcess_IgnoresParkedProcessorsNotAllocatedToIt()
        {
            // Once at least one entry is flagged AllocatedToTargetProcess, the process has an explicit
            // CPU-set restriction (unlike the ordinary/unrestricted case above) -- only flagged entries
            // represent CPUs it can actually be scheduled on, so a parked CPU outside that restricted set
            // must not count against it (it could never subtract more than the process can even see and
            // clamp concurrency to 1).
            var buffer = BuildBuffer(
                (CpuSetInformationType, (byte)(ParkedFlag | AllocatedToTargetProcessFlag)), // parked, ours
                (CpuSetInformationType, AllocatedToTargetProcessFlag), // active, ours
                (CpuSetInformationType, ParkedFlag)); // parked, but not allocated to this process

            Assert.Equal(1, ProcessorTopology.CountParkedLogicalProcessors(buffer));
        }

        [Fact]
        public void AffinityMask_IgnoresParkedProcessorsOutsideTheMask()
        {
            // AllocatedToTargetProcess only covers an explicit CPU-set restriction, not a plain
            // CPU-affinity-mask restriction (Process.ProcessorAffinity, a non-CPU-set-aware job object CPU
            // limit) -- Environment.ProcessorCount respects the latter too, so the affinity mask must be
            // cross-referenced independently. Logical processors 0 and 2 parked, but the process is only
            // allowed to run on processors 0 and 1 (mask 0b011) -- only processor 0 should count.
            var buffer = BuildBuffer(
                (CpuSetInformationType, ParkedFlag, LogicalProcessorIndex: (byte)0),
                (CpuSetInformationType, (byte)0, LogicalProcessorIndex: (byte)1),
                (CpuSetInformationType, ParkedFlag, LogicalProcessorIndex: (byte)2));

            Assert.Equal(1, ProcessorTopology.CountParkedLogicalProcessors(buffer, processAffinityMask: 0b011, processGroup: 0));
        }

        [Fact]
        public void AffinityMask_FullMask_CountsEveryParkedEntry()
        {
            // An unrestricted process's affinity mask covers every logical processor -- passing it through
            // must behave the same as passing no mask at all.
            var buffer = BuildBuffer(
                (CpuSetInformationType, ParkedFlag, LogicalProcessorIndex: (byte)0),
                (CpuSetInformationType, ParkedFlag, LogicalProcessorIndex: (byte)1));

            Assert.Equal(2, ProcessorTopology.CountParkedLogicalProcessors(buffer, processAffinityMask: 0b11, processGroup: 0));
        }

        [Fact]
        public void NoAffinityMaskProvided_DoesNotFilterByProcessorIndex()
        {
            // When the affinity mask couldn't be queried (null), behavior must be unchanged from before this
            // parameter existed -- every parked entry counts (in the common, unrestricted case).
            var buffer = BuildBuffer(
                (CpuSetInformationType, ParkedFlag, LogicalProcessorIndex: (byte)5),
                (CpuSetInformationType, ParkedFlag, LogicalProcessorIndex: (byte)40));

            Assert.Equal(2, ProcessorTopology.CountParkedLogicalProcessors(buffer, processAffinityMask: null, processGroup: 0));
        }

        [Fact]
        public void NoProcessGroupProvided_DoesNotFilterByAffinityMask()
        {
            // GetProcessAffinityMask can succeed while GetProcessGroupAffinity's actual group can't be
            // determined -- without a known group, the mask can't be safely matched to any entry, so it must
            // be ignored entirely (falling back to AllocatedToTargetProcess-only filtering) rather than
            // guessing group 0.
            var buffer = BuildBuffer(
                (CpuSetInformationType, ParkedFlag, LogicalProcessorIndex: (byte)0),
                (CpuSetInformationType, ParkedFlag, LogicalProcessorIndex: (byte)5));

            Assert.Equal(2, ProcessorTopology.CountParkedLogicalProcessors(buffer, processAffinityMask: 0b1, processGroup: null));
        }

        [Fact]
        public void AffinityMask_OnlyAppliesToEntriesInTheProcessesActualGroup()
        {
            // GetProcessAffinityMask's mask is relative to whichever single group the process is confined to
            // (from GetProcessGroupAffinity), not necessarily group 0. A group-1 entry parked at logical index
            // 0 must be filtered against the group-1 mask, not misapplied as if it were in group 0.
            var buffer = BuildBuffer(
                (CpuSetInformationType, ParkedFlag, Group: (ushort)1, LogicalProcessorIndex: (byte)0), // parked, in our group and mask
                (CpuSetInformationType, ParkedFlag, Group: (ushort)1, LogicalProcessorIndex: (byte)1)); // parked, in our group but outside mask

            Assert.Equal(1, ProcessorTopology.CountParkedLogicalProcessors(buffer, processAffinityMask: 0b1, processGroup: 1));
        }

        [Fact]
        public void AffinityMask_ExcludesParkedEntriesInOtherGroups()
        {
            // GetProcessAffinityMask only ever succeeds for a process confined to a single group -- a parked
            // entry in any other group isn't schedulable by this process at all and must not count, even
            // though AllocatedToTargetProcess-only filtering alone (with no entry flagged) would otherwise
            // treat it as visible.
            var buffer = BuildBuffer(
                (CpuSetInformationType, ParkedFlag, Group: (ushort)0, LogicalProcessorIndex: (byte)0), // parked, but a different group than ours
                (CpuSetInformationType, ParkedFlag, Group: (ushort)1, LogicalProcessorIndex: (byte)0)); // parked, in our group and mask

            Assert.Equal(1, ProcessorTopology.CountParkedLogicalProcessors(buffer, processAffinityMask: 0b1, processGroup: 1));
        }

        [Fact]
        public void TruncatedTrailingEntry_StopsWithoutReadingPastTheBuffer()
        {
            var buffer = BuildBuffer((CpuSetInformationType, ParkedFlag));
            var truncated = buffer[..(EntrySize - 1)];

            Assert.Equal(0, ProcessorTopology.CountParkedLogicalProcessors(truncated));
        }
    }
}
