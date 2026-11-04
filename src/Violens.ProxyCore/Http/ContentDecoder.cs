using System.IO.Compression;
using System.Text;
using ZstdSharp;

namespace Violens.ProxyCore.Http;

/// <summary>
/// Undoes Content-Encoding and decodes text, for the inspectors and HAR
/// export. Handles gzip, deflate (zlib or raw), br and zstd (ZstdSharp,
/// until .NET 11 has one), and stacked codings such as "gzip, br", undone in
/// reverse. Unknown codings pass through unchanged.
/// </summary>
public static class ContentDecoder
{
    /// <summary>Codings recognised but not decoded, with the reason shown to the user.</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownUnsupportedEncodings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bzip2"] = "bzip2 was never a registered HTTP Content-Encoding, and no mainstream browser or " +
                        "server sends it. Support is intentionally left out unless a real site turns up that " +
                        "needs it.",
            ["compress"] = "the original LZW-based Content-Encoding, obsolete since the 1990s and not sent by " +
                            "anything in current use.",
            ["sdch"] = "SDCH (Shared Dictionary Compression over HTTP) compressed a body against a per-origin " +
                       "dictionary negotiated separately; without having captured that exact dictionary, the " +
                       "body can't be decoded even in principle, so this isn't a gap that more codec support " +
                       "would close. Chrome removed SDCH in 2016 and it won't appear on the modern web, but the " +
                       "name is recognized here so a capture against something ancient still gets an " +
                       "explanation instead of garbage text.",
        };

    /// <summary>
    /// Undoes <paramref name="contentEncodingHeader"/> on <paramref name="body"/>.
    /// Never throws for bad input: returns false with a readable
    /// <paramref name="error"/> instead, and <paramref name="decoded"/> left
    /// as the original bytes.
    /// </summary>
    public static bool TryDecode(byte[] body, string? contentEncodingHeader, out byte[] decoded, out string? error) =>
        TryDecode(body, contentEncodingHeader, MaxDecodedBytes, out decoded, out error);

    /// <summary>The most bytes a body may decode to (see <see cref="TryDecode(byte[], string?, out byte[], out string?)"/>).</summary>
    internal const int MaxDecodedBytes = 256 * 1024 * 1024;

    internal static bool TryDecode(byte[] body, string? contentEncodingHeader, int maxDecodedBytes, out byte[] decoded, out string? error)
    {
        decoded = body;
        error = null;
        if (body.Length == 0 || string.IsNullOrWhiteSpace(contentEncodingHeader))
        {
            return true;
        }

        var tokens = contentEncodingHeader.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var current = body;
        for (var i = tokens.Length - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (string.Equals(token, "identity", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (KnownUnsupportedEncodings.TryGetValue(token, out var reason))
            {
                error = $"Body is {token}-compressed. {reason} See the Hex tab for the raw bytes.";
                return false;
            }

            if (!TryDecodeOne(current, token, maxDecodedBytes, out current, out var decodeError))
            {
                error = decodeError;
                return false;
            }
        }

        decoded = current;
        return true;
    }

    /// <summary>
    /// <see cref="TryDecode(byte[], string?, out byte[], out string?)"/> using
    /// the Content-Encoding found in <paramref name="headers"/>.
    /// </summary>
    public static bool TryDecode(
        byte[] body, IReadOnlyList<(string Name, string Value)> headers, out byte[] decoded, out string? error) =>
        TryDecode(body, FindHeader(headers, "Content-Encoding"), out decoded, out error);

    /// <summary>
    /// The body as text, using the charset from <paramref name="contentType"/>
    /// when it names one .NET knows, otherwise UTF-8 (which covers nearly all
    /// real traffic and degrades to replacement characters, never an
    /// exception). A UTF-8 byte order mark is dropped.
    /// </summary>
    public static string DecodeText(byte[] bytes, string? contentType)
    {
        var encoding = EncodingFor(contentType);
        var text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>The media type from a Content-Type value, lower-cased, without parameters ("" when absent).</summary>
    public static string MediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var semicolon = contentType.IndexOf(';');
        return (semicolon < 0 ? contentType : contentType[..semicolon]).Trim().ToLowerInvariant();
    }

    /// <summary>First header named <paramref name="name"/>, case-insensitively, or null.</summary>
    public static string? FindHeader(IReadOnlyList<(string Name, string Value)> headers, string name)
    {
        foreach (var (headerName, value) in headers)
        {
            if (string.Equals(headerName, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static Encoding EncodingFor(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return Encoding.UTF8;
        }

        foreach (var parameter in contentType.Split(';').Skip(1))
        {
            var equals = parameter.IndexOf('=');
            if (equals < 0 || !string.Equals(parameter[..equals].Trim(), "charset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var charset = parameter[(equals + 1)..].Trim().Trim('"');
            try
            {
                return Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                return Encoding.UTF8;
            }
        }

        return Encoding.UTF8;
    }

    private static bool TryDecodeOne(byte[] input, string token, int maxDecodedBytes, out byte[] output, out string? error)
    {
        error = null;
        try
        {
            output = token.ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => ReadAllFrom(new GZipStream(new MemoryStream(input), CompressionMode.Decompress), maxDecodedBytes),
                "deflate" => InflateDeflate(input, maxDecodedBytes),
                "br" => ReadAllFrom(new BrotliStream(new MemoryStream(input), CompressionMode.Decompress), maxDecodedBytes),
                "zstd" => ReadAllFrom(new DecompressionStream(new MemoryStream(input)), maxDecodedBytes),
                _ => input,
            };
            return true;
        }
        catch (DecodedTooLargeException)
        {
            output = input;
            error = $"The body decodes to larger than {maxDecodedBytes / (1024 * 1024)} MB as \"{token}\", so it isn't decoded. " +
                    "See the Hex tab for the raw bytes.";
            return false;
        }
        catch (Exception ex)
        {
            // ZstdSharp doesn't document which exceptions bad input throws.
            output = input;
            error = $"Couldn't decompress the body as \"{token}\": {ex.Message}";
            return false;
        }
    }

    private static byte[] InflateDeflate(byte[] input, int maxDecodedBytes)
    {
        try
        {
            return ReadAllFrom(new ZLibStream(new MemoryStream(input), CompressionMode.Decompress), maxDecodedBytes);
        }
        catch (InvalidDataException)
        {
            return ReadAllFrom(new DeflateStream(new MemoryStream(input), CompressionMode.Decompress), maxDecodedBytes);
        }
    }

    // Stops at maxDecodedBytes: a few kilobytes of compressed body can
    // claim gigabytes, and this runs unasked for every response (the
    // inspectors, search, the Image Bloat column).
    private static byte[] ReadAllFrom(Stream decompressor, int maxDecodedBytes)
    {
        using (decompressor)
        {
            using var destination = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (destination.Length + read > maxDecodedBytes)
                {
                    throw new DecodedTooLargeException();
                }

                destination.Write(buffer, 0, read);
            }

            return destination.ToArray();
        }
    }

    private sealed class DecodedTooLargeException : Exception;
}
