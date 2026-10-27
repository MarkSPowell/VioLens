namespace Violens.ProxyCore.Http;

/// <summary>A fully-read request (HTTP/1.1 or HTTP/2), with any chunked encoding removed from the body.</summary>
public sealed record CapturedRequest(
    string Method,
    string Target,
    string HttpVersion,
    IReadOnlyList<(string Name, string Value)> Headers,
    byte[] Body);

/// <summary>A fully-read response (HTTP/1.1 or HTTP/2), with any chunked encoding removed from the body.</summary>
public sealed record CapturedResponse(
    string HttpVersion,
    int StatusCode,
    string ReasonPhrase,
    IReadOnlyList<(string Name, string Value)> Headers,
    byte[] Body)
{
    /// <summary>
    /// Fields sent after the body (HTTP/2 trailers, which gRPC relies on), or
    /// null. HTTP/1.1 responses never carry them here.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)>? Trailers { get; init; }
}
