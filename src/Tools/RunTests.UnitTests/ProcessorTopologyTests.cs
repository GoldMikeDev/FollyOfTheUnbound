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
            => entries.SelectMany(e => BuildEntry(e.Type, e.Flags)).ToArray();

        private static byte[] BuildEntry(int type, byte flags)
        {
            var entry = new byte[EntrySize];
            BitConverter.GetBytes((uint)EntrySize).CopyTo(entry, 0); // Size
            BitConverter.GetBytes(type).CopyTo(entry, 4); // Type
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
        public void TruncatedTrailingEntry_StopsWithoutReadingPastTheBuffer()
        {
            var buffer = BuildBuffer((CpuSetInformationType, ParkedFlag));
            var truncated = buffer[..(EntrySize - 1)];

            Assert.Equal(0, ProcessorTopology.CountParkedLogicalProcessors(truncated));
        }
    }
}
