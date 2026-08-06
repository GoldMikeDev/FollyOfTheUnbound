// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.LanguageServer.Daemon;

/// <summary>
/// A one-byte, out-of-band signal the daemon writes to a connection's raw output stream immediately before
/// tearing down its JsonRpc connection, but only when exit was genuinely requested by the client (see
/// <c>AbstractLanguageServer.OnClientRequestedExitAsync</c>) -- not when the same teardown path is instead
/// reached because the connection broke unexpectedly.
/// <para>
/// Without this, a graceful close of the transport (clean EOF, no error) is indistinguishable from the
/// client's own <c>exit</c> notification actually having been processed: at the raw transport level there is
/// no way to tell "the peer called close() after finishing its own work" from "the peer's process died and the
/// kernel cleaned up its handle" -- both produce a normal EOF, not an error, to the reader. See
/// <c>roslyn-language-server</c>'s <c>LspRelay</c>, which reads this sentinel off the daemon-facing stream to
/// resolve that ambiguity, and GoldMikeDev/roslyn#10 for the full background on the three race windows this
/// closes.
/// </para>
/// <para>
/// This specific byte value cannot legitimately appear anywhere in a well-formed LSP byte stream: headers are
/// ASCII, and a compliant JSON serializer never emits a raw NUL byte in its UTF-8 output. So its presence as
/// the very last byte before EOF is an unambiguous signal, safe to strip rather than forward to the editor.
/// </para>
/// </summary>
internal static class CleanExitSentinel
{
    public const byte Value = 0;

    public static readonly byte[] Bytes = [Value];
}
