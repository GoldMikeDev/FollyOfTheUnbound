// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace RunTests
{
    /// <summary>
    /// Detects hybrid (P-core/E-core) CPU topology on Windows so <see cref="TestRunner"/> can size its
    /// concurrency around the performant cores only. Windows dynamically parks idle low-power (E-)cores
    /// under light load, so a scheduled work item can still land on a parked core and stall; excluding
    /// E-cores from the count up front avoids that instead of trying to detect parking state at runtime
    /// (which has no simple, documented API).
    /// </summary>
    internal static class ProcessorTopology
    {
        /// <summary>
        /// Returns the number of logical processors that belong to a low-power (E-)core, or 0 if the CPU
        /// isn't a hybrid design, this isn't Windows, or the topology couldn't be determined for any reason.
        /// Never throws.
        /// </summary>
        internal static int GetLowPowerLogicalProcessorCount()
        {
            if (!OperatingSystem.IsWindows())
            {
                return 0;
            }

            try
            {
                return GetLowPowerLogicalProcessorCountCore();
            }
            catch
            {
                // Best-effort: any failure here should fall back to the old Environment.ProcessorCount-based
                // sizing rather than take down the test runner.
                return 0;
            }
        }

        [SupportedOSPlatform("windows")]
        private static unsafe int GetLowPowerLogicalProcessorCountCore()
        {
            uint returnedLength = 0;
            _ = PInvoke.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, null, ref returnedLength);
            if (returnedLength == 0)
            {
                return 0;
            }

            var buffer = new byte[returnedLength];
            fixed (byte* bufferPtr = buffer)
            {
                if (!PInvoke.GetLogicalProcessorInformationEx(
                        LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore,
                        (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)bufferPtr,
                        ref returnedLength))
                {
                    return 0;
                }
            }

            return CountLowPowerLogicalProcessors(buffer);
        }

        // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX / PROCESSOR_RELATIONSHIP / GROUP_AFFINITY are fixed,
        // documented Win32 layouts (winnt.h); we walk the raw bytes by hand rather than through the
        // generated struct because PROCESSOR_RELATIONSHIP.GroupMask is a variable-length trailing array
        // (ANYSIZE_ARRAY) that doesn't project cleanly onto a fixed-size managed field.
        //
        //   offset  0: Relationship (int32); RelationProcessorCore == 0
        //   offset  4: Size (uint32) -- byte size of this entry, used to step to the next one
        //   offset  8: Flags (byte)                  \ PROCESSOR_RELATIONSHIP union, starting at offset 8
        //   offset  9: EfficiencyClass (byte)         |
        //   offset 10: Reserved[20]                   |
        //   offset 30: GroupCount (uint16)             /
        //   offset 32: GroupMask[GroupCount] -- each GROUP_AFFINITY is 16 bytes: an 8-byte Mask (KAFFINITY)
        //              followed by a 2-byte Group and 6 bytes reserved.
        private const int RelationProcessorCore = 0;
        private const int RelationshipOffset = 0;
        private const int SizeOffset = 4;
        private const int EfficiencyClassOffset = 9;
        private const int GroupCountOffset = 30;
        private const int GroupMaskOffset = 32;
        private const int GroupAffinitySize = 16;

        /// <summary>
        /// Pure buffer walk, split out from <see cref="GetLowPowerLogicalProcessorCountCore"/> so it can be
        /// unit tested with a synthetic buffer instead of requiring hybrid-CPU hardware.
        /// </summary>
        internal static int CountLowPowerLogicalProcessors(ReadOnlySpan<byte> buffer)
        {
            // First pass: find every processor-core entry's efficiency class and logical processor count.
            Span<(byte EfficiencyClass, int LogicalProcessorCount)> cores = stackalloc (byte, int)[256];
            var coreCount = 0;

            var offset = 0;
            while (offset + SizeOffset + sizeof(uint) <= buffer.Length)
            {
                var relationship = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + RelationshipOffset));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + SizeOffset));
                if (size == 0 || offset + size > buffer.Length)
                {
                    break;
                }

                if (relationship == RelationProcessorCore && offset + GroupCountOffset + sizeof(ushort) <= buffer.Length)
                {
                    var efficiencyClass = buffer[offset + EfficiencyClassOffset];
                    var groupCount = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + GroupCountOffset));

                    var logicalProcessorCount = 0;
                    for (var i = 0; i < groupCount; i++)
                    {
                        var maskOffset = offset + GroupMaskOffset + i * GroupAffinitySize;
                        if (maskOffset + sizeof(ulong) > buffer.Length)
                        {
                            break;
                        }

                        var mask = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(maskOffset));
                        logicalProcessorCount += BitOperations.PopCount(mask);
                    }

                    if (coreCount < cores.Length)
                    {
                        cores[coreCount++] = (efficiencyClass, logicalProcessorCount);
                    }
                }

                offset += (int)size;
            }

            if (coreCount == 0)
            {
                return 0;
            }

            var maxEfficiencyClass = cores[0].EfficiencyClass;
            for (var i = 1; i < coreCount; i++)
            {
                if (cores[i].EfficiencyClass > maxEfficiencyClass)
                {
                    maxEfficiencyClass = cores[i].EfficiencyClass;
                }
            }

            // Not a hybrid CPU (every core reports the same efficiency class): nothing to exclude.
            var lowPowerLogicalProcessorCount = 0;
            var sawLowerClass = false;
            for (var i = 0; i < coreCount; i++)
            {
                if (cores[i].EfficiencyClass < maxEfficiencyClass)
                {
                    sawLowerClass = true;
                    lowPowerLogicalProcessorCount += cores[i].LogicalProcessorCount;
                }
            }

            return sawLowerClass ? lowPowerLogicalProcessorCount : 0;
        }
    }
}
