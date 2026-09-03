// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Roslyn.Utilities;

namespace Roslyn.LanguageServer.Protocol;

/// <summary>
/// A C# port of vscode-uri's URI class. Parses, encodes, decodes, and formats URIs
/// identically to vscode-uri.
///
/// <code>
///       foo://example.com:8042/over/there?name=ferret#nose
///       \_/   \______________/\_________/ \_________/ \__/
///        |           |            |            |        |
///     scheme     authority       path        query   fragment
///        |   _____________________|__
///       / \ /                        \
///       urn:example:animal:ferret:nose
/// </code>
/// </summary>
internal sealed class ParsedUri : IEquatable<ParsedUri>
{
    private sealed class Components
    {
        public string Scheme { get; }
        public string Authority { get; }
        public string Path { get; }
        public string Query { get; }
        public string RawQuery { get; }
        public string Fragment { get; }

        public Components(string scheme, string authority, string path, string query, string rawQuery, string fragment)
        {
            Scheme = scheme;
            Authority = authority;
            Path = path;
            Query = query;
            RawQuery = rawQuery;
            Fragment = fragment;
        }
    }

    private readonly struct FileComponentOffsets
    {
        public System.Range Authority { get; }
        public System.Range Path { get; }

        public FileComponentOffsets(System.Range authority, System.Range path)
        {
            Authority = authority;
            Path = path;
        }
    }

    private static readonly bool s_isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// The scheme component (e.g., "http", "file").
    /// </summary>
    public string Scheme => GetComponents().Scheme;

    /// <summary>
    /// The authority component (e.g., "www.example.com").
    /// </summary>
    public string Authority => GetComponents().Authority;

    /// <summary>
    /// The path component (e.g., "/some/path").
    /// </summary>
    public string Path => GetComponents().Path;

    /// <summary>
    /// The query component (e.g., "name=ferret").
    /// </summary>
    public string Query => GetComponents().Query;

    /// <summary>
    /// The query component as it appeared in the parsed URI, before percent-decoding.
    /// </summary>
    public string RawQuery => GetComponents().RawQuery;

    /// <summary>
    /// The fragment component (e.g., "nose").
    /// </summary>
    public string Fragment => GetComponents().Fragment;

    /// <summary>Set eagerly for parsed URIs and lazily for file URIs.</summary>
    private Components? _components;

    /// <summary>Set only for file URIs; otherwise <see cref="_components"/> must be set.</summary>
    private readonly FileComponentOffsets? _fileComponentOffsets;

    /// <summary>Set eagerly for file URIs and lazily for parsed URIs.</summary>
    private string? _formatted;

    /// <summary>Set when unencoded formatting is first requested.</summary>
    private string? _formattedWithoutEncoding;

    /// <summary>Set when <see cref="FsPath"/> is first requested.</summary>
    private string? _fsPath;

    /// <summary>
    /// The file system path derived from this URI. Computed and cached on first access.
    /// Handles UNC paths, normalizes windows drive letters to lower-case, and uses the
    /// platform specific path separator.
    /// </summary>
    public string FsPath
    {
        get
        {
            var fsPath = _fsPath;
            if (fsPath is not null)
            {
                return fsPath;
            }

            var components = GetComponents();
            fsPath = UriToFsPath(components.Scheme, components.Authority, components.Path);
            return Interlocked.CompareExchange(ref _fsPath, fsPath, null) ?? fsPath;
        }
    }

    private ParsedUri(Components components)
    {
        _components = components;
    }

    private ParsedUri(string formatted, FileComponentOffsets fileComponentOffsets)
    {
        _formatted = formatted;
        _fileComponentOffsets = fileComponentOffsets;
    }

    private Components GetComponents()
    {
        var components = _components;
        if (components is not null)
        {
            return components;
        }

        // The parsed constructor initializes components eagerly, so reaching here means the file constructor
        // initialized both the formatted URI and its component offsets.
        var offsets = _fileComponentOffsets;
        Contract.ThrowIfNull(offsets);
        var formatted = _formatted;
        Contract.ThrowIfNull(formatted);
        components = ParseFileComponents(formatted, offsets.Value);
        return Interlocked.CompareExchange(ref _components, components, null) ?? components;

        static Components ParseFileComponents(string formatted, FileComponentOffsets offsets)
        {
            var formattedSpan = formatted.AsSpan();
            var authority = PercentDecode(formattedSpan[offsets.Authority]);
            var path = PercentDecode(formattedSpan[offsets.Path]);
            return CreateComponents("file", authority, path, string.Empty, string.Empty, string.Empty, strict: false);
        }
    }

    private static Components CreateComponents(string scheme, string authority, string path, string query, string rawQuery, string fragment, bool strict)
    {
        scheme = SchemeFix(scheme, strict);
        path = ReferenceResolution(scheme, path);
        var components = new Components(scheme, authority, path, query, rawQuery, fragment);
        ValidateUri(components, strict);
        return components;
    }

    /// <summary>
    /// Creates a new URI from a string, e.g. <c>http://www.example.com/some/path</c>,
    /// <c>file:///usr/home</c>, or <c>scheme:with/path</c>.
    /// </summary>
    public static ParsedUri Parse(string value, bool strict = false)
        => new(ParseComponents(value, strict));

    private static Components ParseComponents(string value, bool strict)
    {
        var span = value.AsSpan();
        var position = 0;
        var schemeLength = 0;

        // Scheme: "https" in "https://example.com/path".
        while (position < span.Length)
        {
            var ch = span[position];
            if (ch == ':')
            {
                if (position > 0)
                {
                    schemeLength = position;
                    position++;
                }

                break;
            }

            if (ch is '/' or '?' or '#')
            {
                break;
            }

            position++;
        }

        if (schemeLength == 0)
        {
            position = 0;
        }

        var authorityStart = 0;
        var authorityLength = 0;

        // Authority: "example.com" in "https://example.com/path".
        if (position + 1 < span.Length && span[position] == '/' && span[position + 1] == '/')
        {
            position += 2;
            authorityStart = position;
            while (position < span.Length && span[position] is not ('/' or '?' or '#'))
            {
                position++;
            }

            authorityLength = position - authorityStart;
        }

        // Path: "/path/to/file" in "https://example.com/path/to/file?query".
        var pathStart = position;
        while (position < span.Length && span[position] is not ('?' or '#'))
        {
            position++;
        }

        var pathLength = position - pathStart;
        var queryStart = 0;
        var queryLength = 0;

        // Query: "name=value" in "https://example.com/path?name=value#section".
        if (position < span.Length && span[position] == '?')
        {
            position++;
            queryStart = position;
            while (position < span.Length && span[position] != '#')
            {
                position++;
            }

            queryLength = position - queryStart;
        }

        var fragmentStart = 0;
        var fragmentLength = 0;

        // Fragment: "section" in "https://example.com/path#section".
        if (position < span.Length && span[position] == '#')
        {
            position++;
            fragmentStart = position;
            while (position < span.Length && span[position] != '\n')
            {
                position++;
            }

            fragmentLength = position - fragmentStart;
        }

        var scheme = schemeLength == 0 ? string.Empty : value.Substring(0, schemeLength);
        var authority = authorityLength == 0 ? string.Empty : PercentDecode(span.Slice(authorityStart, authorityLength));
        var path = PercentDecode(pathLength == 0 ? string.Empty : span.Slice(pathStart, pathLength));
        var rawQuery = queryLength == 0 ? string.Empty : value.Substring(queryStart, queryLength);
        var query = PercentDecode(rawQuery);
        var fragment = fragmentLength == 0 ? string.Empty : PercentDecode(span.Slice(fragmentStart, fragmentLength));
        return CreateComponents(scheme, authority, path, query, rawQuery, fragment, strict);
    }

    /// <summary>
    /// Creates a new URI from a file system path, e.g. <c>c:\my\files</c>,
    /// <c>/usr/home</c>, or <c>\\server\share\some\path</c>.
    /// The canonical URI string is created immediately; URI components are parsed on first access.
    /// </summary>
    public static ParsedUri File(string path)
    {
        var authority = string.Empty;

        // Normalize to forward slashes on Windows. On other systems, backslashes
        // are valid file-name characters.
        if (s_isWindows)
        {
            path = path.Replace('\\', '/');
        }

        // Check for authority as used in UNC shares, or use the path as given.
        if (path.Length >= 2 && path[0] == '/' && path[1] == '/')
        {
            var index = path.IndexOf('/', 2);
            if (index == -1)
            {
                authority = path.Substring(2);
                path = "/";
            }
            else
            {
                authority = path.Substring(2, index - 2);
                path = path.Substring(index);
            }
        }

        var formatted = FormatFilePath(authority, path, out var offsets);
        return new ParsedUri(formatted, offsets);
    }

    /// <summary>
    /// Returns true when this URI uses the <c>file</c> scheme.
    /// </summary>
    public bool IsFile => IsFileScheme(Scheme);

    /// <summary>
    /// Creates a string representation for this URI. Calling <see cref="Parse"/> with the
    /// result creates a URI that is equal to this URI.
    /// </summary>
    /// <param name="skipEncoding">Do not encode the result.</param>
    public string ToString(bool skipEncoding)
    {
        if (!skipEncoding)
        {
            return ToString();
        }

        var formatted = _formattedWithoutEncoding;
        if (formatted is not null)
        {
            return formatted;
        }

        formatted = AsFormatted(GetComponents(), skipEncoding: true);
        return Interlocked.CompareExchange(ref _formattedWithoutEncoding, formatted, null) ?? formatted;
    }

    /// <summary>
    /// Returns the encoded string representation (equivalent to <c>ToString(false)</c>).
    /// </summary>
    public override string ToString()
    {
        var formatted = _formatted;
        if (formatted is not null)
        {
            return formatted;
        }

        formatted = AsFormatted(GetComponents(), skipEncoding: false);
        return Interlocked.CompareExchange(ref _formatted, formatted, null) ?? formatted;
    }

    #region Equality

    public bool Equals(ParsedUri? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        // Schemes are always case-insensitive per RFC 3986 Section 3.1.
        if (!string.Equals(Scheme, other.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // For file URIs with UNC paths or DOS drive letter paths, compare case-insensitively.
        // This matches System.Uri's behavior (IsUncOrDosPath flag). Unix-style file paths
        // (e.g., file:///usr/home) remain case-sensitive.
        var comparison = IsUncOrDosPath || other.IsUncOrDosPath
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return AuthorityEquals(Authority, other.Authority, comparison == StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path, other.Path, comparison)
            && string.Equals(Query, other.Query, comparison)
            && string.Equals(Fragment, other.Fragment, comparison);
    }

    public override bool Equals(object? obj)
        => obj is ParsedUri other && Equals(other);

    public override int GetHashCode()
    {
        // Scheme is always case-insensitive. Other components are case-insensitive only for UNC/DOS paths.
        var compareComponentsIgnoreCase = IsUncOrDosPath;
        var componentComparer = compareComponentsIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var schemeHash = StringComparer.OrdinalIgnoreCase.GetHashCode(Scheme);
        var authorityHash = GetAuthorityHashCode(Authority, compareComponentsIgnoreCase);
        var pathHash = componentComparer.GetHashCode(Path);
        var queryHash = componentComparer.GetHashCode(Query);
        var fragmentHash = componentComparer.GetHashCode(Fragment);

#if NET
        return HashCode.Combine(schemeHash, authorityHash, pathHash, queryHash, fragmentHash);
#else
        return Hash.Combine(schemeHash,
        Hash.Combine(authorityHash,
        Hash.Combine(pathHash,
        Hash.Combine(queryHash, fragmentHash))));
#endif
    }

    public static bool operator ==(ParsedUri? left, ParsedUri? right)
        => ReferenceEquals(left, right) || (left is not null && left.Equals(right));

    public static bool operator !=(ParsedUri? left, ParsedUri? right)
        => !(left == right);

    /// <summary>
    /// Compares userinfo case-sensitively and host/port case-insensitively, matching RFC 3986 and vscode-uri formatting.
    /// </summary>
    private static bool AuthorityEquals(string left, string right, bool compareAllIgnoreCase)
    {
        if (compareAllIgnoreCase)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        var leftSeparator = left.IndexOf('@');
        var rightSeparator = right.IndexOf('@');
        if (leftSeparator != rightSeparator)
        {
            return false;
        }

        if (leftSeparator < 0)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Compare(left, 0, right, 0, leftSeparator, StringComparison.Ordinal) != 0)
        {
            return false;
        }

        var leftHostLength = left.Length - leftSeparator - 1;
        var rightHostLength = right.Length - rightSeparator - 1;
        return leftHostLength == rightHostLength
            && string.Compare(left, leftSeparator + 1, right, rightSeparator + 1, leftHostLength, StringComparison.OrdinalIgnoreCase) == 0;
    }

    /// <summary>
    /// Hashes authority components using the same casing rules as <see cref="AuthorityEquals"/>.
    /// </summary>
    private static int GetAuthorityHashCode(string authority, bool compareAllIgnoreCase)
    {
        if (compareAllIgnoreCase)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(authority);
        }

        var separator = authority.IndexOf('@');
        if (separator < 0)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(authority);
        }

#if NET
        var userInfoHash = string.GetHashCode(authority.AsSpan(0, separator), StringComparison.Ordinal);
        var hostHash = string.GetHashCode(authority.AsSpan(separator + 1), StringComparison.OrdinalIgnoreCase);
#else
        var userInfoHash = StringComparer.Ordinal.GetHashCode(authority.Substring(0, separator));
        var hostHash = StringComparer.OrdinalIgnoreCase.GetHashCode(authority.Substring(separator + 1));
#endif
        return Hash.Combine(userInfoHash, hostHash);
    }

    #endregion

    #region Validation helpers

    private static void ValidateUri(Components uri, bool strict)
    {
        // SchemeFix supplies the file scheme for non-strict parsing, so only strict parsing can preserve an empty scheme.
        if (uri.Scheme.Length == 0)
        {
            Contract.ThrowIfFalse(strict);
            throw new UriFormatException(
                $"[UriError]: Scheme is missing: {{scheme: \"\", authority: \"{uri.Authority}\", path: \"{uri.Path}\", query: \"{uri.Query}\", fragment: \"{uri.Fragment}\"}}");
        }

        // scheme, https://tools.ietf.org/html/rfc3986#section-3.1
        if (!IsValidScheme(uri.Scheme))
        {
            throw new UriFormatException("[UriError]: Scheme contains illegal characters.");
        }

        // path, http://tools.ietf.org/html/rfc3986#section-3.3
        if (uri.Path.Length > 0)
        {
            if (uri.Authority.Length > 0)
            {
                // Component parsing guarantees that a nonempty path following an authority begins with a slash.
                Contract.ThrowIfFalse(uri.Path[0] == '/');
            }
            else
            {
                if (uri.Path.Length >= 2 && uri.Path[0] == '/' && uri.Path[1] == '/')
                {
                    throw new UriFormatException(
                        "[UriError]: If a URI does not contain an authority component, then the path cannot begin with two slash characters (\"//\")");
                }
            }
        }
    }

    internal static bool IsValidScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.Length == 0 || !IsValidSchemeCharacter(scheme[0]))
        {
            return false;
        }

        for (var i = 1; i < scheme.Length; i++)
        {
            var ch = scheme[i];
            if (!IsValidSchemeCharacter(ch)
                && ch is not ('+' or '.' or '-'))
            {
                return false;
            }
        }

        return true;

        static bool IsValidSchemeCharacter(char ch)
            => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_';
    }

    private static string SchemeFix(string scheme, bool strict)
    {
        if (scheme.Length == 0 && !strict)
        {
            return "file";
        }

        return scheme.ToLowerInvariant();
    }

    /// <summary>
    /// Implements a bit of https://tools.ietf.org/html/rfc3986#section-5
    /// </summary>
    private static string ReferenceResolution(string scheme, string path)
    {
        switch (scheme)
        {
            case "https":
            case "http":
            case "file":
                if (path.Length == 0)
                {
                    path = "/";
                }
                else if (path[0] != '/')
                {
                    path = "/" + path;
                }

                break;
        }

        return path;
    }

    #endregion

    #region Encoding

    private delegate string Encoder(string uriComponent, bool isPath, bool isAuthority);

    /// <summary>
    /// Returns predefined escapes for common reserved ASCII characters:
    /// https://tools.ietf.org/html/rfc3986#section-2.2. This avoids the UTF-8 conversion and temporary
    /// allocations required by the general encoding path; other disallowed characters use that fallback.
    /// </summary>
    private static string? GetEncodeTableEntry(char ch)
    {
        return ch switch
        {
            // gen-delims
            ':' => "%3A",
            '/' => "%2F",
            '?' => "%3F",
            '#' => "%23",
            '[' => "%5B",
            ']' => "%5D",
            '@' => "%40",
            // sub-delims
            '!' => "%21",
            '$' => "%24",
            '&' => "%26",
            '\'' => "%27",
            '(' => "%28",
            ')' => "%29",
            '*' => "%2A",
            '+' => "%2B",
            ',' => "%2C",
            ';' => "%3B",
            '=' => "%3D",
            ' ' => "%20",
            _ => null,
        };
    }

    /// <summary>
    /// Produces canonical URI component encoding for <see cref="ToString()"/>. Characters permitted by the
    /// component type are preserved, while reserved, disallowed ASCII, and non-ASCII characters are percent-encoded.
    /// Returns the original string without allocating when every character is already permitted.
    /// </summary>
    private static string EncodeURIComponentCanonical(string uriComponent, bool isPath, bool isAuthority)
    {
        var pos = 0;
        while (pos < uriComponent.Length && IsEncodingAllowed(uriComponent[pos], isPath, isAuthority))
        {
            pos++;
        }

        if (pos == uriComponent.Length)
        {
            return uriComponent;
        }

        var result = new StringBuilder(uriComponent, 0, pos, uriComponent.Length * 2);
        AppendEncoded(result, uriComponent, pos, isPath, isAuthority);
        return result.ToString();
    }

    /// <summary>
    /// Appends <paramref name="value"/> beginning at <paramref name="start"/>, preserving characters permitted
    /// by the component type and percent-encoding everything else. Used by <see cref="EncodeURIComponentCanonical"/>
    /// after its unchanged prefix and by file formatting after drive-letter normalization.
    /// </summary>
    private static void AppendEncoded(StringBuilder result, string value, int start, bool isPath, bool isAuthority)
    {
        for (var pos = start; pos < value.Length; pos++)
        {
            var ch = value[pos];
            if (IsEncodingAllowed(ch, isPath, isAuthority))
            {
                result.Append(ch);
                continue;
            }

            var escaped = GetEncodeTableEntry(ch);
            if (escaped is not null)
            {
                result.Append(escaped);
                continue;
            }

            // Allowed URI characters were appended unchanged above, and reserved characters with predefined escapes
            // were handled by the table. Anything remaining, including disallowed ASCII and non-ASCII characters,
            // must therefore be UTF-8 percent-encoded. Encode consecutive characters together so surrogate pairs
            // such as 😀 produce one valid UTF-8 sequence instead of replacement characters.
            var nativeEnd = pos + 1;
            while (nativeEnd < value.Length
                && !IsEncodingAllowed(value[nativeEnd], isPath, isAuthority)
                && GetEncodeTableEntry(value[nativeEnd]) is null)
            {
                nativeEnd++;
            }

            result.Append(PercentEncodeString(value.Substring(pos, nativeEnd - pos)));
            pos = nativeEnd - 1;
        }

        static string PercentEncodeString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var sb = new StringBuilder(bytes.Length * 3);
            foreach (var b in bytes)
            {
                sb.Append('%');
                sb.Append(b.ToString("X2"));
            }

            return sb.ToString();
        }
    }

    private static bool IsEncodingAllowed(char ch, bool isPath, bool isAuthority)
        // RFC 3986 sections 2.3, 3.2, and 3.3 define the unreserved characters and
        // the delimiters that may remain unescaped in authority and path components.
        // https://www.rfc-editor.org/rfc/rfc3986.html#section-2.3
        => (ch >= 'a' && ch <= 'z')
            || (ch >= 'A' && ch <= 'Z')
            || (ch >= '0' && ch <= '9')
            || ch == '-'
            || ch == '.'
            || ch == '_'
            || ch == '~'
            || (isPath && ch == '/')
            || (isAuthority && ch == '[')
            || (isAuthority && ch == ']')
            || (isAuthority && ch == ':');

    /// <summary>
    /// Performs the minimal encoding required by <see cref="ToString(bool)"/> when encoding is skipped.
    /// All characters are preserved except <c>?</c> and <c>#</c>, which must remain escaped so they cannot
    /// introduce query or fragment delimiters.
    /// </summary>
    private static string EncodeURIComponentMinimal(string path, bool isPath, bool isAuthority)
    {
        StringBuilder? res = null;
        for (var pos = 0; pos < path.Length; pos++)
        {
            var code = path[pos];
            if (code == '#' || code == '?')
            {
                if (res == null)
                {
                    res = new StringBuilder(path, 0, pos, path.Length + 6);
                }

                res.Append(GetEncodeTableEntry(code));
            }
            else
            {
                res?.Append(code);
            }
        }

        return res != null ? res.ToString() : path;
    }

    #endregion

    #region Decoding

    private static string PercentDecode(string value)
    {
        var span = value.AsSpan();
        if (!TryFindEncodedAsHex(span, startIndex: 0, out var matchStart, out var matchLength))
        {
            return value;
        }

        return PercentDecode(span, matchStart, matchLength);
    }

    private static string PercentDecode(ReadOnlySpan<char> value)
    {
        if (!TryFindEncodedAsHex(value, startIndex: 0, out var matchStart, out var matchLength))
        {
            return value.ToString();
        }

        return PercentDecode(value, matchStart, matchLength);
    }

    private static string PercentDecode(ReadOnlySpan<char> value, int matchStart, int matchLength)
    {
        var result = new StringBuilder(value.Length);
        var position = 0;
        do
        {
            Append(result, value.Slice(position, matchStart - position));
            DecodeURIComponentGraceful(value.Slice(matchStart, matchLength), result);
            position = matchStart + matchLength;
        }
        while (TryFindEncodedAsHex(value, position, out matchStart, out matchLength));

        Append(result, value[position..]);
        return result.ToString();
    }

    private static void Append(StringBuilder builder, ReadOnlySpan<char> value)
    {
#if NET
        builder.Append(value);
#else
        for (var i = 0; i < value.Length; i++)
        {
            builder.Append(value[i]);
        }
#endif
    }

    private static bool TryFindEncodedAsHex(
        ReadOnlySpan<char> value,
        int startIndex,
        out int matchStart,
        out int matchLength)
    {
        for (var i = startIndex; i <= value.Length - 3; i++)
        {
            if (value[i] == '%'
                && IsAsciiLetterOrDigit(value[i + 1])
                && IsAsciiLetterOrDigit(value[i + 2]))
            {
                var end = i + 3;
                // Keep adjacent encoded bytes in one match so multi-byte UTF-8 sequences are decoded together.
                // For example, %E2%82%AC is the three-byte encoding of €; decoding each %XX separately would fail.
                while (end <= value.Length - 3
                    && value[end] == '%'
                    && IsAsciiLetterOrDigit(value[end + 1])
                    && IsAsciiLetterOrDigit(value[end + 2]))
                {
                    end += 3;
                }

                matchStart = i;
                matchLength = end - i;
                return true;
            }
        }

        matchStart = 0;
        matchLength = 0;
        return false;
    }

    private static bool IsAsciiLetterOrDigit(char ch)
        => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');

    /// <summary>
    /// <paramref name="value"/> is always a contiguous run of %XX triplets (see <see cref="TryFindEncodedAsHex"/>),
    /// representing one or more UTF-8 code units. Decodes it as a whole first, since a contiguous run may be one
    /// multi-byte UTF-8 sequence. If that fails, preserves the first %XX literally and retries the remainder so
    /// any valid suffix still decodes. This intentionally matches vscode-uri: because it peels from the front
    /// without locating the invalid byte, a failure near the end can leave valid preceding escapes encoded (for
    /// example, %41%A0 remains %41%A0).
    /// </summary>
    private static void DecodeURIComponentGraceful(ReadOnlySpan<char> value, StringBuilder result)
    {
        // TryFindEncodedAsHex only guarantees the two digits of each %XX triplet are ASCII letters or digits, not
        // that they're valid hex (e.g. %GG matches its shape check but isn't a real escape) -- hexValid tracks
        // which triplets actually decoded to a byte; bytes[i] is meaningless where hexValid[i] is false.
        var byteCount = value.Length / 3;
        var bytes = new byte[byteCount];
        var hexValid = new bool[byteCount];
        for (var i = 0; i < byteCount; i++)
        {
            var hi = HexToInt(value[i * 3 + 1]);
            var lo = HexToInt(value[i * 3 + 2]);
            hexValid[i] = hi >= 0 && lo >= 0;
            if (hexValid[i])
            {
                bytes[i] = (byte)((hi << 4) | lo);
            }
        }

        // validFrom[i] is whether the run of hex-valid bytes starting at i decodes, as a whole, as well-formed
        // UTF-8 up to the next invalid-hex triplet (or the end); runEndAt[i] is where that run ends when it does.
        // A hex-invalid triplet is never part of a byte sequence -- like the original char-by-char loop hitting
        // a non-hex-digit, it's a hard boundary an earlier run's validity can't reach across, and on its own it
        // trivially "decodes" as the empty run. Computed right-to-left in one pass (each position only needs the
        // next sequence's length plus the already-computed result for the position right after it) so the "does
        // the *whole* remaining run decode" check the loop below needs at every candidate start is an O(1) lookup
        // instead of a fresh decode attempt -- the naive retry-the-whole-remainder-per-peeled-triplet approach is
        // quadratic in the number of triplets.
        var validFrom = new bool[byteCount + 1];
        var runEndAt = new int[byteCount + 1];
        validFrom[byteCount] = true;
        runEndAt[byteCount] = byteCount;
        for (var i = byteCount - 1; i >= 0; i--)
        {
            if (!hexValid[i])
            {
                validFrom[i] = true;
                runEndAt[i] = i;
                continue;
            }

            var seqLength = GetUtf8SequenceLength(bytes, hexValid, i, byteCount);
            validFrom[i] = seqLength > 0 && validFrom[i + seqLength];
            if (validFrom[i])
            {
                runEndAt[i] = runEndAt[i + seqLength];
            }
        }

        var bytePos = 0;
        while (bytePos < byteCount)
        {
            if (!hexValid[bytePos])
            {
                // Matches the original per-character loop hitting a non-hex-digit: never attempted as a byte.
                Append(result, value.Slice(bytePos * 3, 3));
                bytePos++;
            }
            else if (validFrom[bytePos])
            {
                var runEnd = runEndAt[bytePos];
                result.Append(s_strictUtf8.GetString(bytes, bytePos, runEnd - bytePos));
                bytePos = runEnd;
            }
            else
            {
                Append(result, value.Slice(bytePos * 3, 3));
                bytePos++;
            }
        }
    }

    /// <summary>
    /// Returns the length (1-4) of the well-formed UTF-8 sequence starting at <paramref name="index"/> (itself
    /// already confirmed valid by the caller), matching <see cref="s_strictUtf8"/>'s strict acceptance rules
    /// (rejects overlong encodings, surrogate code points, and code points beyond U+10FFFF), or 0 if
    /// <paramref name="index"/> does not start a well-formed sequence (including a sequence truncated by
    /// <paramref name="length"/> or one whose continuation bytes came from an invalid-hex triplet).
    /// </summary>
    private static int GetUtf8SequenceLength(byte[] bytes, bool[] hexValid, int index, int length)
    {
        var b0 = bytes[index];
        if (b0 <= 0x7F)
        {
            return 1;
        }

        if ((b0 & 0xE0) == 0xC0)
        {
            return b0 >= 0xC2 && HasContinuations(1) ? 2 : 0;
        }

        if ((b0 & 0xF0) == 0xE0)
        {
            if (!HasContinuations(2))
            {
                return 0;
            }

            var codePoint = ((b0 & 0x0F) << 12) | ((bytes[index + 1] & 0x3F) << 6) | (bytes[index + 2] & 0x3F);
            return codePoint is >= 0x800 and (< 0xD800 or > 0xDFFF) ? 3 : 0;
        }

        if ((b0 & 0xF8) == 0xF0)
        {
            if (!HasContinuations(3))
            {
                return 0;
            }

            var codePoint = ((b0 & 0x07) << 18) | ((bytes[index + 1] & 0x3F) << 12) | ((bytes[index + 2] & 0x3F) << 6) | (bytes[index + 3] & 0x3F);
            return codePoint is >= 0x10000 and <= 0x10FFFF ? 4 : 0;
        }

        return 0;

        bool HasContinuations(int count)
        {
            if (index + count >= length)
            {
                return false;
            }

            for (var i = 1; i <= count; i++)
            {
                if (!hexValid[index + i] || (bytes[index + i] & 0xC0) != 0x80)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static int HexToInt(char ch)
    {
        // TryFindEncodedAsHex only guarantees ASCII letters or digits here, not valid hex digits (e.g. 'G'..'Z').
        Contract.ThrowIfFalse(IsAsciiLetterOrDigit(ch));
        if (ch <= '9') return ch - '0';
        if (ch <= 'F') return ch - 'A' + 10;
        if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
        return -1;
    }

    private static readonly Encoding s_strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    #endregion

    #region Formatting

    private static string FormatFilePath(string authority, string path, out FileComponentOffsets offsets)
    {
        // Start the canonical URI and append an encoded UNC authority, if present:
        // "//server/share/file" becomes "file://server".
        var result = new StringBuilder("file://", 7 + authority.Length + path.Length + 8);
        var authorityStart = result.Length;
        AppendFormattedAuthority(result, authority, EncodeURIComponentCanonical);
        var authorityLength = result.Length - authorityStart;

        var pathStart = result.Length;
        if (path.Length == 0)
        {
            // File URIs always have an absolute path; an empty path becomes "file:///".
            result.Append('/');
        }
        else
        {
            // Make relative-looking drive paths absolute: "C:/file" becomes "/C:/file".
            var addLeadingSlash = path[0] != '/';
            if (addLeadingSlash)
            {
                result.Append('/');
            }

            // Locate a drive letter in either "C:/file" or "/C:/file".
            var driveLetterIndex = path.Length >= 3 && path[0] == '/' && path[2] == ':'
                ? 1
                : path.Length >= 2 && path[1] == ':'
                    ? 0
                    : -1;

            if (driveLetterIndex >= 0 && path[driveLetterIndex] is >= 'A' and <= 'Z')
            {
                // Match vscode-uri by lower-casing drive letters while encoding the rest.
                if (driveLetterIndex == 1)
                {
                    result.Append('/');
                }

                result.Append(char.ToLowerInvariant(path[driveLetterIndex]));
                AppendEncoded(result, path, driveLetterIndex + 1, isPath: true, isAuthority: false);
            }
            else
            {
                // Preserve path separators and percent-encode characters such as spaces.
                AppendEncoded(result, path, start: 0, isPath: true, isAuthority: false);
            }
        }

        // Save canonical component ranges so Authority and Path can be materialized lazily.
        offsets = new FileComponentOffsets(authorityStart..(authorityStart + authorityLength), pathStart..result.Length);
        return result.ToString();
    }

    private static void AppendFormattedAuthority(StringBuilder result, string authority, Encoder encoder)
    {
        if (authority.Length == 0)
        {
            return;
        }

        var index = authority.IndexOf('@');
        if (index != -1)
        {
            // <user>@<auth>
            var userinfo = authority.Substring(0, index);
            authority = authority.Substring(index + 1);
            index = userinfo.LastIndexOf(':');
            if (index == -1)
            {
                result.Append(encoder(userinfo, false, false));
            }
            else
            {
                // <user>:<pass>@<auth>
                result.Append(encoder(userinfo.Substring(0, index), false, false));
                result.Append(':');
                result.Append(encoder(userinfo.Substring(index + 1), false, true));
            }

            result.Append('@');
        }

        authority = authority.ToLowerInvariant();
        index = authority.LastIndexOf(':');
        if (index == -1)
        {
            result.Append(encoder(authority, false, true));
        }
        else
        {
            // <auth>:<port>
            result.Append(encoder(authority.Substring(0, index), false, true));
            result.Append(authority.Substring(index));
        }
    }

    /// <summary>
    /// Compute fsPath for the given URI components.
    /// </summary>
    private static string UriToFsPath(string scheme, string authority, string path)
    {
        string value;
        if (authority.Length > 0 && path.Length > 1 && IsFileScheme(scheme))
        {
            // unc path: file://shares/c$/far/boo
            value = "//" + authority + path;
        }
        else if (
            path.Length >= 3
            && path[0] == '/'
            && IsLetter(path[1])
            && path[2] == ':')
        {
            // windows drive letter: file:///c:/far/boo
            value = char.ToLowerInvariant(path[1]) + path.Substring(2);
        }
        else
        {
            // other path
            value = path;
        }

        if (s_isWindows)
        {
            value = value.Replace('/', '\\');
        }

        return value;
    }

    internal static bool IsLetter(char ch)
        => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    internal static bool IsFileScheme(string scheme)
        => string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if this is a file URI with a UNC host or DOS drive letter path.
    /// Matches the behavior of System.Uri's internal IsUncOrDosPath flag, which determines
    /// whether path comparison should be case-insensitive.
    /// </summary>
    internal bool IsUncOrDosPath
        => IsFile
        && (Authority.Length > 0 || (Path.Length >= 3 && Path[0] == '/' && IsLetter(Path[1]) && Path[2] == ':'));

    /// <summary>
    /// Create the external version of a URI.
    /// </summary>
    private static string AsFormatted(Components components, bool skipEncoding)
    {
        Encoder encoder = skipEncoding
            ? EncodeURIComponentMinimal
            : EncodeURIComponentCanonical;

        var res = new StringBuilder();
        var scheme = components.Scheme;
        var authority = components.Authority;
        var path = components.Path;
        var query = components.Query;
        var fragment = components.Fragment;

        if (scheme.Length > 0)
        {
            res.Append(scheme);
            res.Append(':');
        }

        if (authority.Length > 0 || IsFileScheme(scheme))
        {
            res.Append("//");
        }

        AppendFormattedAuthority(res, authority, encoder);

        if (path.Length > 0)
        {
            // lower-case windows drive letters in /C:/fff or C:/fff
            if (path.Length >= 3 && path[0] == '/' && path[2] == ':')
            {
                var code = path[1];
                if (code >= 'A' && code <= 'Z')
                {
                    path = "/" + char.ToLowerInvariant(code) + ":" + path.Substring(3);
                }
            }
            else if (path.Length >= 2 && path[1] == ':')
            {
                var code = path[0];
                if (code >= 'A' && code <= 'Z')
                {
                    path = char.ToLowerInvariant(code) + ":" + path.Substring(2);
                }
            }

            // encode the rest of the path
            res.Append(encoder(path, true, false));
        }

        if (query.Length > 0)
        {
            res.Append('?');
            res.Append(encoder(query, false, false));
        }

        if (fragment.Length > 0)
        {
            res.Append('#');
            if (!skipEncoding)
            {
                res.Append(EncodeURIComponentCanonical(fragment, false, false));
            }
            else
            {
                // The fragment is the final component, so after its '#' delimiter no character can introduce
                // another URI component. The minimally encoded form can therefore preserve it verbatim.
                res.Append(fragment);
            }
        }

        return res.ToString();
    }

    #endregion
}
