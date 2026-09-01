// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RunTests
{
    /// <summary>
    /// Per-machine record of how long each work item took the last time it <em>passed</em>, used to show a
    /// "Previous" column in <see cref="LiveTestProgressDisplay"/>'s live table. Deliberately stored outside
    /// <c>artifacts/</c> (which <c>folly.sh</c>/<c>folly.ps1 cleanse</c> deletes) at
    /// <c>&lt;repo root&gt;/.test-timings.json</c> so it survives a clean, and is itself gitignored so every
    /// machine keeps its own independent history rather than this becoming a file everyone's local runs fight
    /// over in source control.
    /// </summary>
    internal sealed class LocalTestTimingHistory
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

        private readonly string? _filePath;
        private readonly object _gate = new();
        private readonly Dictionary<string, TimeSpan> _timings;

        private LocalTestTimingHistory(string? filePath, Dictionary<string, TimeSpan> timings)
        {
            _filePath = filePath;
            _timings = timings;
        }

        /// <summary>
        /// Loads existing history from disk, or starts empty if the file doesn't exist or can't be read (e.g. a
        /// first run on this machine, or a corrupt/foreign-format file) -- this is a convenience display, never
        /// worth failing or warning about the run over.
        /// </summary>
        internal static LocalTestTimingHistory Load(string? artifactsDirectory)
        {
            var filePath = GetFilePath(artifactsDirectory);
            var timings = filePath is null ? new Dictionary<string, TimeSpan>(StringComparer.Ordinal) : ReadFromDisk(filePath);
            return new LocalTestTimingHistory(filePath, timings);
        }

        /// <summary>
        /// Starts empty (never throws) if the file doesn't exist or can't be read (e.g. a first run on this
        /// machine, a corrupt/foreign-format file, or another process's write landing mid-read) -- this is a
        /// convenience display, never worth failing or warning about the run over.
        /// </summary>
        private static Dictionary<string, TimeSpan> ReadFromDisk(string filePath)
        {
            var timings = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            if (!File.Exists(filePath))
            {
                return timings;
            }

            try
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(filePath));
                if (raw is not null)
                {
                    foreach (var (key, milliseconds) in raw)
                    {
                        timings[key] = TimeSpan.FromMilliseconds(milliseconds);
                    }
                }
            }
            catch
            {
                // As above -- start fresh (dropping only this read, not RecordPassed's caller) rather than fail.
            }

            return timings;
        }

        /// <summary>
        /// Key used both to look up and to record a work item's history -- see <see cref="LiveTestProgressDisplay"/>'s
        /// row construction. Includes <paramref name="configuration"/> (Debug/Release) and
        /// <paramref name="architecture"/> (x86/x64/arm64) alongside the assembly/TFM identity: Debug and Release
        /// builds of the same assembly can have very different runtimes (JIT optimizations, assertion/diagnostic
        /// code compiled in), so without this a Release "research" run and a Debug "truth" run would silently
        /// overwrite each other's "Previous" baseline.
        /// </summary>
        internal static string GetKey(string baseName, string? tfmTag, string configuration, string architecture)
            => tfmTag is null
                ? $"{baseName}|{configuration}|{architecture}"
                : $"{baseName}|{tfmTag}|{configuration}|{architecture}";

        internal TimeSpan? TryGetPreviousDuration(string key)
        {
            lock (_gate)
            {
                return _timings.TryGetValue(key, out var duration) ? duration : null;
            }
        }

        /// <summary>
        /// Records <paramref name="duration"/> for <paramref name="key"/> and rewrites the whole history file --
        /// called once per work item as it finishes, so a run that's interrupted partway through still leaves
        /// behind whatever completed rather than losing the whole run's timings. Best-effort: a failed write
        /// (e.g. the file is locked by another concurrent `scry`) is silently dropped, never surfaced as a test
        /// failure.
        /// <para>
        /// Re-reads the file fresh and merges into <em>that</em> (rather than blindly overwriting with this
        /// instance's own in-memory snapshot) immediately before writing: two `scry` processes running
        /// concurrently (e.g. separate `--core`/`--framework` invocations, or just two terminals) each load
        /// their own independent snapshot at startup, and a naive dump-this-instance's-whole-dict write would
        /// let whichever one writes last silently erase every key the other one had already recorded but this
        /// instance never loaded. This narrows the race to just the read-merge-write itself (not a true
        /// interprocess lock -- a key from a truly simultaneous write on the other process can still be lost if
        /// its own write lands in that exact window), which is an acceptable trade for a best-effort, display-only
        /// convenience file, unlike a `Directory.Build.props`/lockfile-style file changes must never race on.
        /// </para>
        /// </summary>
        internal void RecordPassed(string key, TimeSpan duration)
        {
            if (_filePath is null)
            {
                return;
            }

            lock (_gate)
            {
                _timings[key] = duration;

                try
                {
                    var merged = ReadFromDisk(_filePath);
                    merged[key] = duration;

                    File.WriteAllText(_filePath, JsonSerializer.Serialize(
                        merged.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value.TotalMilliseconds),
                        s_jsonOptions));

                    // Fold the merge back into this instance's own view too, so a later RecordPassed call in
                    // this same process merges against the fuller picture instead of re-reading from scratch
                    // (already about to happen anyway, but keeps _timings and the file from drifting apart for
                    // any interim TryGetPreviousDuration calls in between).
                    foreach (var (mergedKey, mergedDuration) in merged)
                    {
                        _timings[mergedKey] = mergedDuration;
                    }
                }
                catch
                {
                    // Best-effort, as above.
                }
            }
        }

        private static string? GetFilePath(string? artifactsDirectory)
        {
            if (string.IsNullOrEmpty(artifactsDirectory))
            {
                return null;
            }

            var repoRoot = Path.GetDirectoryName(artifactsDirectory);
            return repoRoot is null ? null : Path.Combine(repoRoot, ".test-timings.json");
        }
    }
}
