// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace RunTests.UnitTests
{
    // Exercises ProcessorTopology.CountLowPowerLogicalProcessors against synthetic
    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX buffers, since real hybrid (P-core/E-core) hardware
    // isn't available to every machine running this test suite.
    public sealed class ProcessorTopologyTests
    {
        private const int RelationProcessorCore = 0;
        private const int RelationNumaNode = 1;

        private static byte[] BuildBuffer(params (int Relationship, byte EfficiencyClass, ulong[] GroupMasks)[] entries)
        {
            var chunks = new List<byte[]>();
            foreach (var (relationship, efficiencyClass, groupMasks) in entries)
            {
                chunks.Add(BuildEntry(relationship, efficiencyClass, groupMasks));
            }

            var buffer = new byte[chunks.Count == 0 ? 0 : chunks.Sum(c => c.Length)];
            var offset = 0;
            foreach (var chunk in chunks)
            {
                chunk.CopyTo(buffer, offset);
                offset += chunk.Length;
            }

            return buffer;
        }

        private static byte[] BuildEntry(int relationship, byte efficiencyClass, ulong[] groupMasks)
        {
            var size = 8 + 24 + groupMasks.Length * 16;
            var entry = new byte[size];

            BitConverter.GetBytes(relationship).CopyTo(entry, 0);
            BitConverter.GetBytes((uint)size).CopyTo(entry, 4);

            if (relationship == RelationProcessorCore)
            {
                entry[9] = efficiencyClass; // EfficiencyClass
                BitConverter.GetBytes((ushort)groupMasks.Length).CopyTo(entry, 30); // GroupCount

                for (var i = 0; i < groupMasks.Length; i++)
                {
                    var groupOffset = 32 + i * 16;
                    BitConverter.GetBytes(groupMasks[i]).CopyTo(entry, groupOffset); // GROUP_AFFINITY.Mask
                }
            }

            return entry;
        }

        [Fact]
        public void EmptyBuffer_ReturnsZero()
        {
            Assert.Equal(0, ProcessorTopology.CountLowPowerLogicalProcessors(ReadOnlySpan<byte>.Empty));
        }

        [Fact]
        public void NonHybridCpu_AllSameEfficiencyClass_ReturnsZero()
        {
            // 4 physical cores, 2 logical processors each (hyperthreaded), all the same efficiency class.
            var buffer = BuildBuffer(
                (RelationProcessorCore, 0, new ulong[] { 0b0000_0011 }),
                (RelationProcessorCore, 0, new ulong[] { 0b0000_1100 }),
                (RelationProcessorCore, 0, new ulong[] { 0b0011_0000 }),
                (RelationProcessorCore, 0, new ulong[] { 0b1100_0000 }));

            Assert.Equal(0, ProcessorTopology.CountLowPowerLogicalProcessors(buffer));
        }

        [Fact]
        public void HybridCpu_CountsOnlyLowerEfficiencyClassLogicalProcessors()
        {
            // 2 P-cores (efficiency class 1, hyperthreaded -- 2 logical processors each) and
            // 4 E-cores (efficiency class 0, no hyperthreading -- 1 logical processor each).
            var buffer = BuildBuffer(
                (RelationProcessorCore, 1, new ulong[] { 0b0000_0011 }),
                (RelationProcessorCore, 1, new ulong[] { 0b0000_1100 }),
                (RelationProcessorCore, 0, new ulong[] { 0b0001_0000 }),
                (RelationProcessorCore, 0, new ulong[] { 0b0010_0000 }),
                (RelationProcessorCore, 0, new ulong[] { 0b0100_0000 }),
                (RelationProcessorCore, 0, new ulong[] { 0b1000_0000 }));

            Assert.Equal(4, ProcessorTopology.CountLowPowerLogicalProcessors(buffer));
        }

        [Fact]
        public void HybridCpu_MultiGroupMask_SumsAllGroupsForTheCore()
        {
            // A single E-core reporting affinity across two processor groups (rare, but the struct allows it).
            var buffer = BuildBuffer(
                (RelationProcessorCore, 1, new ulong[] { 0b11 }),
                (RelationProcessorCore, 0, new ulong[] { 0b1, 0b1 }));

            Assert.Equal(2, ProcessorTopology.CountLowPowerLogicalProcessors(buffer));
        }

        [Fact]
        public void IgnoresNonProcessorCoreRelationships()
        {
            // A RelationNumaNode entry interleaved between two differently-classed processor cores must
            // not be mistaken for a core, and must not break walking past it via its Size field.
            var buffer = BuildBuffer(
                (RelationProcessorCore, 1, new ulong[] { 0b1 }),
                (RelationNumaNode, 0, new ulong[] { 0b1111 }),
                (RelationProcessorCore, 0, new ulong[] { 0b1 }));

            Assert.Equal(1, ProcessorTopology.CountLowPowerLogicalProcessors(buffer));
        }
    }
}
