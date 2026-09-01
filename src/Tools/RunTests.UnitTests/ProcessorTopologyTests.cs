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
            var buffer = BuildBuffer(
                (CpuSetInformationType, (byte)(AllocatedFlag | AllocatedToTargetProcessFlag)),
                (CpuSetInformationType, (byte)(AllocatedFlag | AllocatedToTargetProcessFlag)),
                (CpuSetInformationType, AllocatedToTargetProcessFlag));

            Assert.Equal(0, ProcessorTopology.CountParkedLogicalProcessors(buffer));
        }

        [Fact]
        public void CountsOnlyEntriesWithTheParkedBitSet()
        {
            // 6 logical processors, all allocated to this process: 4 active (2 of which are also
            // allocated -- an unrelated flag that must not be mistaken for Parked), 2 parked.
            var buffer = BuildBuffer(
                (CpuSetInformationType, AllocatedToTargetProcessFlag),
                (CpuSetInformationType, (byte)(AllocatedFlag | AllocatedToTargetProcessFlag)),
                (CpuSetInformationType, (byte)(AllocatedFlag | AllocatedToTargetProcessFlag)),
                (CpuSetInformationType, AllocatedToTargetProcessFlag),
                (CpuSetInformationType, (byte)(ParkedFlag | AllocatedToTargetProcessFlag)),
                (CpuSetInformationType, (byte)(ParkedFlag | AllocatedFlag | AllocatedToTargetProcessFlag)));

            Assert.Equal(2, ProcessorTopology.CountParkedLogicalProcessors(buffer));
        }

        [Fact]
        public void IgnoresNonCpuSetEntryTypes()
        {
            // A non-CpuSetInformation entry that happens to have the same bits set at the Flags offset
            // must not be counted -- only Type == CpuSetInformation entries represent a logical processor.
            var buffer = BuildBuffer(
                (CpuSetInformationType, (byte)(ParkedFlag | AllocatedToTargetProcessFlag)),
                (OtherRelationshipType, (byte)(ParkedFlag | AllocatedToTargetProcessFlag)));

            Assert.Equal(1, ProcessorTopology.CountParkedLogicalProcessors(buffer));
        }

        [Fact]
        public void IgnoresParkedProcessorsNotAllocatedToThisProcess()
        {
            // GetSystemCpuSetInformation always returns every CPU set on the whole system -- the process
            // handle only controls each entry's AllocatedToTargetProcess flag. A process narrowed by CPU
            // affinity/a CPU set assignment/a job object CPU limit must not have parked CPUs it can never
            // be scheduled on counted against it, or this could subtract more than Environment.ProcessorCount
            // even sees and clamp concurrency to 1.
            var buffer = BuildBuffer(
                (CpuSetInformationType, (byte)(ParkedFlag | AllocatedToTargetProcessFlag)), // parked, ours
                (CpuSetInformationType, ParkedFlag)); // parked, but not allocated to this process

            Assert.Equal(1, ProcessorTopology.CountParkedLogicalProcessors(buffer));
        }

        [Fact]
        public void TruncatedTrailingEntry_StopsWithoutReadingPastTheBuffer()
        {
            var buffer = BuildBuffer((CpuSetInformationType, (byte)(ParkedFlag | AllocatedToTargetProcessFlag)));
            var truncated = buffer[..(EntrySize - 1)];

            Assert.Equal(0, ProcessorTopology.CountParkedLogicalProcessors(truncated));
        }
    }
}
