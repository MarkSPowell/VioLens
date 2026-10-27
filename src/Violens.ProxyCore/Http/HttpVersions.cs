namespace Violens.ProxyCore.Http;

/// <summary>
/// The version strings a captured message can carry. Sessions use
/// <c>HTTP/2</c>, as RFC 9113 writes it; files written for other tools use
/// the spelling those tools expect.
/// </summary>
public static class HttpVersions
{
    public const string Http11 = "HTTP/1.1";

    public const string Http2 = "HTTP/2";

    /// <summary>True for <c>HTTP/2</c> and <c>HTTP/2.0</c>, in any case.</summary>
    public static bool IsHttp2(string? version) =>
        string.Equals(version, "HTTP/2", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(version, "HTTP/2.0", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>HTTP/2.0</c> read from a file as <c>HTTP/2</c>; anything else unchanged.</summary>
    public static string Normalize(string version) => IsHttp2(version) ? Http2 : version;

    /// <summary>
    /// The version in a SAZ file's start lines. Tools that read SAZ files
    /// expect HTTP/major.minor, so HTTP/2 is written <c>HTTP/2.0</c>.
    /// </summary>
    public static string ForSaz(string version) => IsHttp2(version) ? "HTTP/2.0" : version;

    /// <summary>The version in a HAR file, written <c>http/2.0</c> for HTTP/2 as Chrome writes it.</summary>
    public static string ForHar(string version) => IsHttp2(version) ? "http/2.0" : version;

    /// <summary>
    /// Both sides of an exchange in one line: <c>HTTP/2</c> when the client
    /// and server used the same version, <c>HTTP/2 → HTTP/1.1</c> when they
    /// didn't. <paramref name="serverVersion"/> is null when no response came.
    /// </summary>
    public static string Describe(string clientVersion, string? serverVersion) =>
        string.IsNullOrEmpty(serverVersion) || string.Equals(clientVersion, serverVersion, StringComparison.OrdinalIgnoreCase)
            ? clientVersion
            : $"{clientVersion} → {serverVersion}";
}
