using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Violens.CompatShim;

/// <summary>
/// The subset of Fiddler's <c>Utilities</c> that the extensions in the
/// Extension Test Targets doc call, with signatures from Telerik's published
/// FiddlerCore API reference (or, for the Fiddler Classic-only
/// <see cref="ObtainOpenFilename(string, string)"/>, from the extensions'
/// own call sites). Grows as more extensions are ported.
/// </summary>
public static class Utilities
{
    /// <summary>A shared zero-length array.</summary>
    public static readonly byte[] emptyByteArray = [];

    /// <summary>True if the array is null or empty.</summary>
    public static bool IsNullOrEmpty(byte[]? bIn) => bIn is null || bIn.Length == 0;

    /// <summary>Expands GZIP data. On bad data, logs and returns an empty array.</summary>
    public static byte[] GzipExpand(byte[] compressedData) => GzipExpand(compressedData, bThrowErrors: false);

    /// <summary>Expands GZIP data; with <paramref name="bThrowErrors"/>, bad data throws instead of returning an empty array.</summary>
    public static byte[] GzipExpand(byte[] compressedData, bool bThrowErrors)
    {
        if (IsNullOrEmpty(compressedData))
        {
            return emptyByteArray;
        }

        try
        {
            using var input = new MemoryStream(compressedData);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);

            // Capped like VioLens's own decoding: a few KB of gzip can
            // expand to gigabytes.
            return Violens.ProxyCore.Http.ReadLimit.ReadAll(gzip, Violens.ProxyCore.Http.ContentDecoder.MaxDecodedBytes);
        }
        catch (Exception ex) when (!bThrowErrors && ex is InvalidDataException or IOException)
        {
            CompatShimHost.Log($"GzipExpand failed: {ex.Message}");
            return emptyByteArray;
        }
    }

    public static string ByteArrayToHexView(byte[] inArr, int iBytesPerLine) =>
        ByteArrayToHexView(inArr, 0, iBytesPerLine, int.MaxValue, bShowASCII: true);

    public static string ByteArrayToHexView(byte[] inArr, int iBytesPerLine, int iMaxByteCount) =>
        ByteArrayToHexView(inArr, 0, iBytesPerLine, iMaxByteCount, bShowASCII: true);

    public static string ByteArrayToHexView(byte[] inArr, int iBytesPerLine, int iMaxByteCount, bool bShowASCII) =>
        ByteArrayToHexView(inArr, 0, iBytesPerLine, iMaxByteCount, bShowASCII);

    /// <summary>
    /// A hex dump: <paramref name="iBytesPerLine"/> bytes per line as
    /// two-digit upper-case hex separated by spaces, optionally followed by
    /// the printable ASCII (anything else shown as <c>.</c>). Lines end in
    /// <c>\n</c>.
    /// </summary>
    public static string ByteArrayToHexView(byte[] inArr, int iStartAt, int iBytesPerLine, int iMaxByteCount, bool bShowASCII)
    {
        if (IsNullOrEmpty(inArr) || iBytesPerLine < 1 || iStartAt >= inArr.Length)
        {
            return string.Empty;
        }

        var start = Math.Max(0, iStartAt);
        var end = (int)Math.Min(inArr.Length, (long)start + Math.Max(0, iMaxByteCount));
        var sb = new StringBuilder();
        for (var lineStart = start; lineStart < end; lineStart += iBytesPerLine)
        {
            var lineEnd = Math.Min(end, lineStart + iBytesPerLine);
            for (var i = lineStart; i < lineStart + iBytesPerLine; i++)
            {
                sb.Append(i < lineEnd ? inArr[i].ToString("X2") + " " : "   ");
            }

            if (bShowASCII)
            {
                sb.Append(' ');
                for (var i = lineStart; i < lineEnd; i++)
                {
                    var b = inArr[i];
                    sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                }
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Asks the user to pick a file to open. <paramref name="sFilter"/> is a
    /// WinForms-style filter (<c>"Description|*.a;*.b|Other|*.c"</c>); on both
    /// Windows and macOS the desktop app turns it into its own
    /// cross-platform file picker (see <see cref="ParseFileDialogFilter"/>).
    /// Null if cancelled, or if the host has no UI.
    /// </summary>
    public static string? ObtainOpenFilename(string sDialogTitle, string sFilter)
    {
        var prompt = CompatShimHost.PromptForOpenFile;
        if (prompt is null)
        {
            CompatShimHost.Log($"An extension asked for a file (\"{sDialogTitle}\"), but this host has no file picker.");
            return null;
        }

        return prompt(sDialogTitle, sFilter);
    }

    /// <summary>
    /// Splits a WinForms-style dialog filter into (description, patterns)
    /// pairs: <c>"NetLog JSON (*.json)|*.json;*.json.gz"</c> →
    /// <c>("NetLog JSON (*.json)", ["*.json", "*.json.gz"])</c>. A trailing
    /// description with no pattern part is dropped.
    /// </summary>
    public static IReadOnlyList<(string Description, string[] Patterns)> ParseFileDialogFilter(string? sFilter)
    {
        var result = new List<(string, string[])>();
        if (string.IsNullOrWhiteSpace(sFilter))
        {
            return result;
        }

        var parts = sFilter.Split('|');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var patterns = parts[i + 1]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (patterns.Length > 0)
            {
                result.Add((parts[i].Trim(), patterns));
            }
        }

        return result;
    }

    /// <summary>Opens a URL in the user's default browser (Windows and macOS). False, after logging, if that fails.</summary>
    public static bool LaunchHyperlink(string sURL)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(sURL) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            CompatShimHost.Log($"Couldn't open {sURL}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// A readable description of a TLS ClientHello, given a stream positioned
    /// at a TLS record (5-byte record header, then the handshake message).
    /// See <see cref="TlsHelloDescriber"/> for the format. Never throws.
    /// </summary>
    public static string UNSTABLE_DescribeClientHello(MemoryStream msHello) => TlsHelloDescriber.Describe(msHello, expectedHandshakeType: 1);

    /// <summary>As <see cref="UNSTABLE_DescribeClientHello"/>, for a ServerHello.</summary>
    public static string UNSTABLE_DescribeServerHello(MemoryStream msHello) => TlsHelloDescriber.Describe(msHello, expectedHandshakeType: 2);

    /// <summary>Case-insensitive <see cref="string.Contains(string)"/>, as a Fiddler string extension.</summary>
    public static bool OICContains(this string inStr, string toMatch) =>
        inStr is not null && toMatch is not null && inStr.Contains(toMatch, StringComparison.OrdinalIgnoreCase);

    /// <summary>Case-insensitive <see cref="string.StartsWith(string)"/>.</summary>
    public static bool OICStartsWith(this string inStr, string toMatch) =>
        inStr is not null && toMatch is not null && inStr.StartsWith(toMatch, StringComparison.OrdinalIgnoreCase);

    /// <summary>Case-insensitive <see cref="string.EndsWith(string)"/>.</summary>
    public static bool OICEndsWith(this string inStr, string toMatch) =>
        inStr is not null && toMatch is not null && inStr.EndsWith(toMatch, StringComparison.OrdinalIgnoreCase);

    /// <summary>Case-insensitive equality.</summary>
    public static bool OICEquals(this string inStr, string toMatch) =>
        string.Equals(inStr, toMatch, StringComparison.OrdinalIgnoreCase);
}
