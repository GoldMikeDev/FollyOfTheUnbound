// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.LanguageServer.Daemon;

namespace Microsoft.CodeAnalysis.LanguageServer;

/// <summary>
/// A single transport connection between an editor (or thin client) and a language server: a pair of
/// input/output streams plus an optional resource to dispose when the server for this connection exits.
/// </summary>
/// <param name="Handshake">
/// The connecting client's own per-connection configuration (e.g. its <c>--extensionLogDirectory</c>), if any
/// was received -- only daemon-mode connections carry one (see
/// <see cref="NamedPipeDaemonConnectionSource.AcceptConnectionsAsync"/>); single-server mode (stdio / connect-out
/// pipe) leaves this <see langword="null"/> since that mode already has the connecting client's full
/// <see cref="ServerConfiguration"/> directly, with no separate connection to route it through.
/// </param>
internal readonly record struct LanguageServerConnection(Stream InputStream, Stream OutputStream, IDisposable? Resource = null, ConnectionHandshake? Handshake = null);
