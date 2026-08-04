// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CodeAnalysis.LanguageServer.Daemon;

/// <summary>
/// Per-connection configuration a client sends to an already-running daemon immediately after connecting, so
/// fields that used to be captured once (from whichever client happened to launch the daemon) and silently
/// applied to every later connection can instead be routed to just the connection they belong to. Written by
/// the thin client right after its pipe connects and read by the daemon before that same stream becomes the
/// LSP JSON-RPC channel -- see <see cref="WriteAsync"/>/<see cref="ReadAsync"/>.
/// <para>
/// This file is source-shared (linked) into both the thin client and the language server (see
/// <see cref="DaemonPipeName"/>'s own remarks), so it stays dependency-light: no JSON, just a small
/// hand-rolled length-prefixed binary framing, matching the connection's raw <see cref="Stream"/> transport.
/// </para>
/// <para>
/// General-purpose by design rather than carrying only today's one field (<see cref="ExtensionLogDirectory"/>):
/// see docs/ide/specs/daemon-per-connection-isolation.md's phase 5/7 notes for why
/// <see cref="SourceGeneratorExecutionPreference"/> is included even though nothing reads it per-connection
/// yet, and why that's expected to change before a second field would otherwise be added here.
/// </para>
/// </summary>
internal sealed record ConnectionHandshake(string? ExtensionLogDirectory, string? SourceGeneratorExecutionPreference)
{
    // Bump and branch on this if the payload shape ever needs to change; a client/daemon pair from this repo
    // are always built and shipped together, so version skew is not expected in practice, but failing loudly
    // on an unrecognized version is cheaper than silently misreading the frame that follows.
    private const byte ProtocolVersion = 1;

    public static readonly ConnectionHandshake Empty = new(ExtensionLogDirectory: null, SourceGeneratorExecutionPreference: null);

    /// <summary>
    /// Extracts the subset of a thin client's own <c>serverArguments</c> that a daemon connection's handshake
    /// carries, recognizing the two-token (<c>--flag value</c>) and both inline (<c>--flag=value</c>,
    /// <c>--flag:value</c>) forms System.CommandLine accepts on the daemon side for the same flags -- see
    /// https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax#option-argument-delimiters.
    /// </summary>
    public static ConnectionHandshake FromServerArguments(IReadOnlyList<string> serverArguments)
    {
        string? extensionLogDirectory = null;
        string? sourceGeneratorExecutionPreference = null;

        for (var i = 0; i < serverArguments.Count; i++)
        {
            if (TryReadOption(serverArguments, ref i, "--extensionLogDirectory", out var logDirectory))
            {
                // Resolve against *this* (the connecting client's) current directory before it ever leaves the
                // client. The daemon inherits its working directory from whichever client happened to launch
                // it, not from this connection -- so a relative value would otherwise be resolved (via
                // Directory.CreateDirectory and RunTestsHandler's test-log path) against the wrong base once it
                // reaches the daemon, writing this connection's logs underneath an unrelated directory.
                extensionLogDirectory = logDirectory is null ? null : Path.GetFullPath(logDirectory);
                continue;
            }

            if (TryReadOption(serverArguments, ref i, "--sourceGeneratorExecutionPreference", out var sourceGeneratorPreference))
            {
                // An empty value (only reachable via the inline "--sourceGeneratorExecutionPreference=" or
                // "--sourceGeneratorExecutionPreference:" forms -- the bare-flag form is already rejected below)
                // is not a valid SourceGeneratorExecutionPreference enum literal, so a cold daemon launch's
                // System.CommandLine parsing would reject it the same way it rejects "not-a-real-value".
                // WriteStringAsync/ReadStringAsync collapse an empty string to the same zero-length wire frame
                // as "field absent" (see their remarks), so without this check the empty value would silently
                // become "not specified" once it crosses the wire, falling back to Automatic instead of failing
                // consistently with a cold launch.
                if (string.IsNullOrEmpty(sourceGeneratorPreference))
                    throw new InvalidOperationException("'--sourceGeneratorExecutionPreference' requires a value.");

                sourceGeneratorExecutionPreference = sourceGeneratorPreference;
                continue;
            }
        }

        return new ConnectionHandshake(extensionLogDirectory, sourceGeneratorExecutionPreference);

        static bool TryReadOption(IReadOnlyList<string> args, ref int index, string optionName, out string? value)
        {
            var arg = args[index];

            if (arg.StartsWith(optionName + "=", StringComparison.Ordinal) ||
                arg.StartsWith(optionName + ":", StringComparison.Ordinal))
            {
                value = arg[(optionName.Length + 1)..];
                return true;
            }

            if (arg == optionName)
            {
                if (index + 1 >= args.Count)
                {
                    // A bare flag with no following value token. System.CommandLine requires a value for both
                    // of these options (ArgumentArity.ExactlyOne by default), so a cold daemon launch would
                    // reject this outright via LanguageServerCommandLine's own parsing. Silently treating it the
                    // same as "the option was never passed" here -- which is what returning false used to do,
                    // since the caller's loop just moves on to the next token -- would let the identical
                    // malformed invocation succeed when a compatible daemon happens to already be running
                    // instead of consistently failing the way a cold launch would. Throw here, on the
                    // connecting client, before this ever reaches the daemon.
                    throw new InvalidOperationException($"'{optionName}' requires a value.");
                }

                index++;
                value = args[index];
                return true;
            }

            value = null;
            return false;
        }
    }

    public async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new[] { ProtocolVersion }.AsMemory(), cancellationToken).ConfigureAwait(false);
        await WriteStringAsync(stream, ExtensionLogDirectory, cancellationToken).ConfigureAwait(false);
        await WriteStringAsync(stream, SourceGeneratorExecutionPreference, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ConnectionHandshake> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var versionBuffer = new byte[1];
        await stream.ReadExactlyAsync(versionBuffer, cancellationToken).ConfigureAwait(false);
        if (versionBuffer[0] != ProtocolVersion)
            throw new InvalidOperationException($"Unsupported connection handshake protocol version {versionBuffer[0]}.");

        var extensionLogDirectory = await ReadStringAsync(stream, cancellationToken).ConfigureAwait(false);
        var sourceGeneratorExecutionPreference = await ReadStringAsync(stream, cancellationToken).ConfigureAwait(false);
        return new ConnectionHandshake(extensionLogDirectory, sourceGeneratorExecutionPreference);
    }

    private static async Task WriteStringAsync(Stream stream, string? value, CancellationToken cancellationToken)
    {
        // Length 0 means "absent" on the read side: neither field is ever meaningfully an empty string (a
        // directory path or an enum name), so there is no ambiguity in collapsing null/empty to the same frame.
        var bytes = string.IsNullOrEmpty(value) ? [] : Encoding.UTF8.GetBytes(value);
        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, bytes.Length);
        await stream.WriteAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 0)
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    // Generous for a directory path or enum name, but bounds the allocation this does in response to a
    // length prefix read directly off the wire -- CurrentUserOnly restricts who can connect at all, but this
    // still shouldn't trust an arbitrary 4-byte value from the stream without some ceiling.
    private const int MaxFieldLength = 64 * 1024;

    private static async Task<string?> ReadStringAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        await stream.ReadExactlyAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length == 0)
            return null;

        if (length < 0 || length > MaxFieldLength)
            throw new InvalidOperationException($"Connection handshake field length {length} is out of the expected range.");

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }
}
