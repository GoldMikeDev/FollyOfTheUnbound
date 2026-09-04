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
        /// <param name="artifactsDirectory">Only gates whether history is enabled at all -- see <see cref="GetFilePath"/>.</param>
        /// <param name="binaryDirectoryForRepoRootLookup">
        /// Where to start walking up for the enclosing <c>artifacts</c> directory (see <see cref="FindRepoRoot"/>).
        /// Defaults to this process's own binary location; overridable only so tests can point it at a synthetic
        /// checkout layout instead of this repo's real one.
        /// </param>
        internal static LocalTestTimingHistory Load(string? artifactsDirectory, string? binaryDirectoryForRepoRootLookup = null)
        {
            var filePath = GetFilePath(artifactsDirectory, binaryDirectoryForRepoRootLookup ?? AppContext.BaseDirectory);
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

        // Mirrors HelixTestRunner's own IOperationEnvironmentVariable/RuntimeAsyncEnvironmentVariable/
        // UsedAssembliesEnvironmentVariable + AddEnvironmentVariableToken, which distinguishes Helix job
        // names the same way and for the same reason: each of these materially changes how long a work
        // item takes (IOperation validation in particular can turn a normal run into one many times
        // slower -- see TESTING_STRATEGY.md), so a "Previous" duration recorded under one combination is
        // not a meaningful comparison for a run under another.
        private const string IOperationEnvironmentVariable = "ROSLYN_TEST_IOPERATION";
        private const string RuntimeAsyncEnvironmentVariable = "DOTNET_RuntimeAsync";
        private const string UsedAssembliesEnvironmentVariable = "ROSLYN_TEST_USEDASSEMBLIES";

        /// <summary>
        /// Key used both to look up and to record a work item's history -- see <see cref="LiveTestProgressDisplay"/>'s
        /// row construction. Includes <paramref name="configuration"/> (Debug/Release) and
        /// <paramref name="architecture"/> (x86/x64/arm64) alongside the assembly/TFM identity: Debug and Release
        /// builds of the same assembly can have very different runtimes (JIT optimizations, assertion/diagnostic
        /// code compiled in), so without this a Release "research" run and a Debug "truth" run would silently
        /// overwrite each other's "Previous" baseline. Also folds in a token per environment variable in
        /// <see cref="IOperationEnvironmentVariable"/>/<see cref="RuntimeAsyncEnvironmentVariable"/>/
        /// <see cref="UsedAssembliesEnvironmentVariable"/> that's currently set, for the same reason.
        /// </summary>
        internal static string GetKey(string baseName, string? tfmTag, string configuration, string architecture)
        {
            var key = tfmTag is null
                ? $"{baseName}|{configuration}|{architecture}"
                : $"{baseName}|{tfmTag}|{configuration}|{architecture}";

            var tokens = new List<string>();
            addToken(IOperationEnvironmentVariable, "IOperation");
            addToken(RuntimeAsyncEnvironmentVariable, "RuntimeAsync");
            addToken(UsedAssembliesEnvironmentVariable, "UsedAssemblies");

            return tokens.Count == 0 ? key : $"{key}|{string.Join(",", tokens)}";

            void addToken(string environmentVariable, string token)
            {
                if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 })
                {
                    tokens.Add(token);
                }
            }
        }

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

        /// <summary>
        /// <paramref name="artifactsDirectory"/> only gates whether history is enabled at all (mirroring
        /// <see cref="Options.ArtifactsDirectory"/> itself being unresolved) -- the file's location is found
        /// independently by walking up from <paramref name="binaryDirectoryForRepoRootLookup"/>, the same
        /// technique <c>Options.TryGetArtifactsPath</c> uses to find the real <c>artifacts</c> directory by
        /// default. Deriving it from <paramref name="artifactsDirectory"/>'s parent instead would break under
        /// <c>--artifactspath</c>: that switch accepts an arbitrary directory (e.g. a Helix work item's own
        /// scratch path, or any other override), whose parent isn't necessarily this checkout's root at all.
        /// </summary>
        private static string? GetFilePath(string? artifactsDirectory, string binaryDirectoryForRepoRootLookup)
        {
            if (string.IsNullOrEmpty(artifactsDirectory))
            {
                return null;
            }

            var repoRoot = FindRepoRoot(binaryDirectoryForRepoRootLookup);
            return repoRoot is null ? null : Path.Combine(repoRoot, ".test-timings.json");
        }

        /// <summary>
        /// Walks up from <paramref name="startDirectory"/> looking for a directory literally named
        /// <c>artifacts</c> (matching this repo's layout: <c>RunTests</c> itself always builds to
        /// <c>artifacts/bin/RunTests/...</c> regardless of where <c>--artifactspath</c> tells it to look for
        /// *other* things) and returns that directory's parent, or <see langword="null"/> if none is found
        /// (e.g. a standalone/relocated copy of the binaries outside any checkout).
        /// </summary>
        private static string? FindRepoRoot(string startDirectory)
        {
            var path = startDirectory;
            while (path is not null && Path.GetFileName(path) != "artifacts")
            {
                path = Path.GetDirectoryName(path);
            }

            return path is null ? null : Path.GetDirectoryName(path);
        }
    }
}
