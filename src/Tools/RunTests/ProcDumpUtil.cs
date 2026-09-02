// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace RunTests
{
    internal static class DumpUtil
    {
        // Captured by EnableRegistryDumpCollection so DisableRegistryDumpCollection can restore
        // whatever was there before (including "no value at all") instead of unconditionally
        // deleting these keys -- LocalDumps is a machine-wide (HKLM) setting, so unconditional
        // deletion would silently wipe out a developer's own pre-existing WER dump configuration
        // (or one set by a concurrently-running RunTests process) rather than just undoing this run's
        // own change.
        private static object? s_priorDumpType;
        private static RegistryValueKind s_priorDumpTypeKind;
        private static object? s_priorDumpCount;
        private static RegistryValueKind s_priorDumpCountKind;
        private static object? s_priorDumpFolder;
        private static RegistryValueKind s_priorDumpFolderKind;

#pragma warning disable CA1416 // Validate platform compatibility
        internal static void EnableRegistryDumpCollection(string dumpDirectory)
        {
            Debug.Assert(IsAdministrator());

            using var registryKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps", writable: true);
            (s_priorDumpType, s_priorDumpTypeKind) = GetValueAndKind(registryKey, "DumpType");
            (s_priorDumpCount, s_priorDumpCountKind) = GetValueAndKind(registryKey, "DumpCount");
            (s_priorDumpFolder, s_priorDumpFolderKind) = GetValueAndKind(registryKey, "DumpFolder");

            registryKey.SetValue("DumpType", 2, RegistryValueKind.DWord);
            registryKey.SetValue("DumpCount", 2, RegistryValueKind.DWord);
            registryKey.SetValue("DumpFolder", dumpDirectory, RegistryValueKind.String);
        }

        internal static void DisableRegistryDumpCollection()
        {
            Debug.Assert(IsAdministrator());

            using var registryKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps", writable: true);
            RestoreValue(registryKey, "DumpType", s_priorDumpType, s_priorDumpTypeKind);
            RestoreValue(registryKey, "DumpCount", s_priorDumpCount, s_priorDumpCountKind);
            RestoreValue(registryKey, "DumpFolder", s_priorDumpFolder, s_priorDumpFolderKind);
        }

        private static (object? Value, RegistryValueKind Kind) GetValueAndKind(RegistryKey registryKey, string name)
        {
            // DoNotExpandEnvironmentNames: a REG_EXPAND_SZ value (e.g. "%LOCALAPPDATA%\CrashDumps") must be
            // captured and restored exactly as stored -- GetValue's default behavior expands it, which would
            // silently and permanently turn a prior expandable string into a plain one once restored.
            var value = registryKey.GetValue(name, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
            {
                return (null, RegistryValueKind.Unknown);
            }

            return (value, registryKey.GetValueKind(name));
        }

        private static void RestoreValue(RegistryKey registryKey, string name, object? priorValue, RegistryValueKind kind)
        {
            if (priorValue is null)
            {
                registryKey.DeleteValue(name, throwOnMissingValue: false);
            }
            else
            {
                registryKey.SetValue(name, priorValue, kind);
            }
        }

        internal static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
#pragma warning restore CA1416 // Validate platform compatibility
    }
}
