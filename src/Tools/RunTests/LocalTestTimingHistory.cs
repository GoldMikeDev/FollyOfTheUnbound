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
            var timings = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            if (filePath is not null && File.Exists(filePath))
            {
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
                    // Corrupt or unreadable history file -- start fresh rather than fail the whole run over a
                    // display convenience.
                }
            }

            return new LocalTestTimingHistory(filePath, timings);
        }

        /// <summary>Key used both to look up and to record a work item's history -- see <see cref="LiveTestProgressDisplay"/>'s row construction.</summary>
        internal static string GetKey(string baseName, string? tfmTag)
            => tfmTag is null ? baseName : $"{baseName}|{tfmTag}";

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
                    var raw = _timings.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value.TotalMilliseconds);
                    File.WriteAllText(_filePath, JsonSerializer.Serialize(raw, s_jsonOptions));
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
