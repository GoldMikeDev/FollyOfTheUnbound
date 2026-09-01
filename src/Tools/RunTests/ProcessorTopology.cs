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
            uint returnedLength = 0;
            _ = PInvoke.GetSystemCpuSetInformation(null, 0, &returnedLength, (HANDLE)default, 0);
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
                        (HANDLE)default,
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
        //   offset 19: a union whose low bit (0x1) is the live "Parked" flag for this logical processor
        private const int CpuSetInformationType = 0;
        private const int SizeOffset = 0;
        private const int TypeOffset = 4;
        private const int FlagsOffset = 19;
        private const byte ParkedFlag = 0x1;

        /// <summary>
        /// Pure buffer walk, split out from <see cref="GetParkedLogicalProcessorCountCore"/> so it can be
        /// unit tested with a synthetic buffer instead of requiring live OS state.
        /// </summary>
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
                if (type == CpuSetInformationType && (buffer[offset + FlagsOffset] & ParkedFlag) != 0)
                {
                    parkedCount++;
                }

                offset += (int)size;
            }

            return parkedCount;
        }
    }
}
