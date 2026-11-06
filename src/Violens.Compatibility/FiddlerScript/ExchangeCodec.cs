using System.IO.Compression;

namespace Violens.Compatibility.FiddlerScript;

/// <summary>
/// Backs <see cref="Exchange.utilDecodeResponse"/>: gzip, deflate (zlib or
/// raw) and br. zstd throws <see cref="NotSupportedException"/>, to avoid
/// adding ZstdSharp to this assembly.
/// </summary>
internal static class ExchangeCodec
{
    public static void DecodeInPlace(ExchangeResponse response)
    {
        var contentEncoding = response.headers["Content-Encoding"];
        if (string.IsNullOrWhiteSpace(contentEncoding))
        {
            return;
        }

        var tokens = contentEncoding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var current = response.Body;
        for (var i = tokens.Length - 1; i >= 0; i--)
        {
            current = DecodeOne(current, tokens[i]);
        }

        response.Body = current;
        response.headers.Remove("Content-Encoding");
    }

    private static byte[] DecodeOne(byte[] input, string token) => token.ToLowerInvariant() switch
    {
        "identity" => input,
        "gzip" or "x-gzip" => ReadAllFrom(new GZipStream(new MemoryStream(input), CompressionMode.Decompress)),
        "deflate" => InflateDeflate(input),
        "br" => ReadAllFrom(new BrotliStream(new MemoryStream(input), CompressionMode.Decompress)),
        "zstd" => throw new NotSupportedException(
            "utilDecodeResponse() doesn't support zstd yet -- see ExchangeCodec's own remarks."),
        _ => input,
    };

    private static byte[] InflateDeflate(byte[] input)
    {
        try
        {
            return ReadAllFrom(new ZLibStream(new MemoryStream(input), CompressionMode.Decompress));
        }
        catch (InvalidDataException)
        {
            return ReadAllFrom(new DeflateStream(new MemoryStream(input), CompressionMode.Decompress));
        }
    }

    private static byte[] ReadAllFrom(Stream decompressor)
    {
        // Capped like VioLens's own decoding; past the cap this throws
        // an IOException, as corrupt data does.
        using (decompressor)
        {
            return Violens.ProxyCore.Http.ReadLimit.ReadAll(decompressor, Violens.ProxyCore.Http.ContentDecoder.MaxDecodedBytes);
        }
    }
}
