// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers.Binary;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace RunTests
{
    /// <summary>
    /// Queries live per-core parking state on Windows so <see cref="TestRunner"/> can size its concurrency
    /// around the logical processors actually schedulable right now. Windows dynamically parks idle
    /// logical processors under light load -- on a hybrid CPU this can mean a whole efficiency-class tier
    /// stays parked essentially permanently (e.g. a P-core/E-core/"Low Power Island" E-core design may
    /// schedule the middle E-core tier normally but never touch the bottom LP-E-core tier), while on
    /// others the exact set of parked cores shifts with load. Either way, a work item scheduled onto a
    /// currently-parked core can stall there, so this asks Windows directly (<c>GetSystemCpuSetInformation</c>,
    /// the same live parking signal .NET's own thread pool consults) rather than assuming a fixed tier.
    /// </summary>
    internal static class ProcessorTopology
    {
        /// <summary>
        /// Returns the number of logical processors that are currently parked, or 0 if this isn't Windows,
        /// no processors are reported parked, or the topology couldn't be determined for any reason. Never
        /// throws. This is a live snapshot taken once at the call site, not a continuously-updated value.
        /// </summary>
        internal static int GetParkedLogicalProcessorCount()
        {
            if (!OperatingSystem.IsWindows())
            {
                return 0;
            }

            try
            {
                return GetParkedLogicalProcessorCountCore();
            }
            catch
            {
                // Best-effort: any failure here should fall back to the old Environment.ProcessorCount-based
                // sizing rather than take down the test runner.
                return 0;
            }
        }

        [SupportedOSPlatform("windows")]
        private static unsafe int GetParkedLogicalProcessorCountCore()
        {
            // Scoped to this process's own handle, not NULL (which would return every CPU set on the whole
            // system): a process restricted below the full machine -- CPU affinity, an explicit CPU set
            // assignment, or a job object's CPU limit -- has a correspondingly smaller Environment.ProcessorCount,
            // and subtracting a system-wide parked count from that mismatched, larger universe of CPU sets
            // could subtract more than this process can even see, undercounting (or, saturating at 0 in
            // TestRunner's Math.Max clamp, silently zeroing out) the concurrency this process can actually use.
            // GetCurrentProcess() is a pseudo-handle (always -1, never needs closing) -- see its own Win32 docs.
            var currentProcess = PInvoke.GetCurrentProcess();

            uint returnedLength = 0;
            _ = PInvoke.GetSystemCpuSetInformation(null, 0, &returnedLength, currentProcess, 0);
            if (returnedLength == 0)
            {
                return 0;
            }

            var buffer = new byte[returnedLength];
            fixed (byte* bufferPtr = buffer)
            {
                if (!PInvoke.GetSystemCpuSetInformation(
                        (Windows.Win32.System.SystemInformation.SYSTEM_CPU_SET_INFORMATION*)bufferPtr,
                        returnedLength,
                        &returnedLength,
                        currentProcess,
                        0))
                {
                    return 0;
                }
            }

            return CountParkedLogicalProcessors(buffer);
        }

        // SYSTEM_CPU_SET_INFORMATION is a fixed-size (no trailing flexible array), documented Win32 struct
        // (winnt.h), but we still walk it by raw offset -- like ProcessorTopology's previous
        // GetLogicalProcessorInformationEx-based implementation -- to sidestep depending on the exact
        // generated names for its nested anonymous unions:
        //
        //   offset  0: Size (uint32) -- byte size of this entry, used to step to the next one
        //   offset  4: Type (int32); CpuSetInformation == 0
        //   offset  8: Id (uint32)
        //   offset 19: a union (AllFlags) whose bits are, low to high: Parked (0x1), Allocated (0x2),
        //              AllocatedToTargetProcess (0x4), RealTimeAffinity (0x8)
        private const int CpuSetInformationType = 0;
        private const int SizeOffset = 0;
        private const int TypeOffset = 4;
        private const int FlagsOffset = 19;
        private const byte ParkedFlag = 0x1;
        private const byte AllocatedToTargetProcessFlag = 0x4;

        /// <summary>
        /// Pure buffer walk, split out from <see cref="GetParkedLogicalProcessorCountCore"/> so it can be
        /// unit tested with a synthetic buffer instead of requiring live OS state.
        /// </summary>
        /// <remarks>
        /// <c>GetSystemCpuSetInformation</c> always returns the whole system's CPU sets regardless of which
        /// process handle is passed -- the handle only controls each entry's <c>AllocatedToTargetProcess</c>
        /// flag (true for a CPU set in this process's affinity/assigned-CPU-set view, or every CPU set when
        /// the process has no such restriction). So a process narrowed by CPU affinity, an explicit CPU set
        /// assignment, or a job object's CPU limit would otherwise have system-wide parked CPUs -- including
        /// ones it can never be scheduled on at all -- subtracted from its much smaller
        /// <see cref="Environment.ProcessorCount"/>, potentially clamping concurrency to 1. Filtering to only
        /// <c>AllocatedToTargetProcess</c> entries keeps this counting exclusively within the set of CPUs this
        /// process can actually be scheduled on, matching <see cref="Environment.ProcessorCount"/>'s scope.
        /// </remarks>
        internal static int CountParkedLogicalProcessors(ReadOnlySpan<byte> buffer)
        {
            var parkedCount = 0;
            var offset = 0;
            while (offset + FlagsOffset + sizeof(byte) <= buffer.Length)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + SizeOffset));
                if (size == 0 || offset + size > buffer.Length)
                {
                    break;
                }

                var type = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + TypeOffset));
                if (type == CpuSetInformationType)
                {
                    var flags = buffer[offset + FlagsOffset];
                    if ((flags & AllocatedToTargetProcessFlag) != 0 && (flags & ParkedFlag) != 0)
                    {
                        parkedCount++;
                    }
                }

                offset += (int)size;
            }

            return parkedCount;
        }
    }
}
