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
        private static object? s_priorDumpCount;
        private static object? s_priorDumpFolder;

#pragma warning disable CA1416 // Validate platform compatibility
        internal static void EnableRegistryDumpCollection(string dumpDirectory)
        {
            Debug.Assert(IsAdministrator());

            using var registryKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps", writable: true);
            s_priorDumpType = registryKey.GetValue("DumpType");
            s_priorDumpCount = registryKey.GetValue("DumpCount");
            s_priorDumpFolder = registryKey.GetValue("DumpFolder");

            registryKey.SetValue("DumpType", 2, RegistryValueKind.DWord);
            registryKey.SetValue("DumpCount", 2, RegistryValueKind.DWord);
            registryKey.SetValue("DumpFolder", dumpDirectory, RegistryValueKind.String);
        }

        internal static void DisableRegistryDumpCollection()
        {
            Debug.Assert(IsAdministrator());

            using var registryKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps", writable: true);
            RestoreValue(registryKey, "DumpType", s_priorDumpType, RegistryValueKind.DWord);
            RestoreValue(registryKey, "DumpCount", s_priorDumpCount, RegistryValueKind.DWord);
            RestoreValue(registryKey, "DumpFolder", s_priorDumpFolder, RegistryValueKind.String);
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
