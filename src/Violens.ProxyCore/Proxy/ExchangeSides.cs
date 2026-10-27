using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// The client's side of one exchange, as <see cref="ExchangePipeline"/> uses
/// it: <see cref="Http1ClientExchange"/> for HTTP/1.1, <see cref="Http2ClientExchange"/>
/// for one HTTP/2 stream.
/// </summary>
internal interface IClientExchange
{
    Task<byte[]> ReadRequestBodyAsync(CapturedRequest preamble, CancellationToken cancellationToken);

    Task WriteResponseAsync(CapturedResponse response, CancellationToken cancellationToken);

    /// <summary>An interim (1xx) response; the final one follows.</summary>
    Task WriteInterimResponseAsync(CapturedResponse response, CancellationToken cancellationToken);

    /// <summary>The status line and headers of a response whose body follows by relay; <see cref="EndResponseAsync"/> ends it.</summary>
    Task WriteResponsePreambleAsync(CapturedResponse response, CancellationToken cancellationToken);

    /// <summary>
    /// Ends a response begun with <see cref="WriteResponsePreambleAsync"/>,
    /// once its body (if any) is relayed, with its trailers where the client's
    /// protocol can carry them.
    /// </summary>
    Task EndResponseAsync(IReadOnlyList<(string Name, string Value)>? trailers, CancellationToken cancellationToken);

    /// <summary>Ends the exchange abruptly (AutoResponder's <c>*reset</c>).</summary>
    void Reset();
}

/// <summary>
/// A client whose bodies aren't in HTTP/1.1 framing (an HTTP/2 stream):
/// what <see cref="IUpstream"/> reads its request body from and writes its
/// response body to, a piece at a time, when relaying.
/// </summary>
internal interface IClientBodyStream
{
    /// <summary>False when the request's headers ended it, so there's no body to relay.</summary>
    bool RequestHasBody { get; }

    /// <summary>Request body bytes, as they arrive; 0 at the end.</summary>
    ValueTask<int> ReadRequestBodyAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    Task WriteResponseBodyAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

/// <summary>
/// The connection to the server, or to the network's proxy, that exchanges
/// go out on. For HTTP/1.1 that's <see cref="Http1Upstream"/>.
/// </summary>
internal interface IUpstream : IDisposable
{
    string TargetHost { get; }

    int TargetPort { get; }

    /// <summary>Where connecting is timed, for the exchange in progress.</summary>
    SessionTimingRecorder? Timing { get; set; }

    /// <summary>Whether the server asked for a client certificate during the TLS handshake.</summary>
    bool ClientCertificateRequested { get; }

    /// <summary>The open connection, for relaying switched protocols.</summary>
    Stream Stream { get; }

    /// <summary>For plain HTTP, sends requests to the server their Host header names, which may have been edited.</summary>
    void RouteFrom(CapturedRequest request);

    /// <summary>
    /// Opens the connection, or keeps the open one. Throws
    /// <see cref="System.Net.Sockets.SocketException"/>, <see cref="IOException"/>
    /// or <see cref="System.Security.Authentication.AuthenticationException"/>
    /// when the server can't be reached.
    /// </summary>
    Task ConnectAsync(string target, CancellationToken cancellationToken);

    Task SendRequestAsync(CapturedRequest request, CancellationToken cancellationToken);

    /// <summary>Sends the request line and headers, then relays the body from <paramref name="client"/> as it arrives.</summary>
    Task<Http1MessageReader.RelayedBody> SendRequestRelayingBodyAsync(
        CapturedRequest preamble, IClientExchange client, long maxCapturedBodyBytes, CancellationToken cancellationToken);

    /// <summary>The next status line and headers; null if the connection closed first.</summary>
    Task<CapturedResponse?> ReadResponsePreambleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// When the network's proxy answered 407, signs in and sends the request
    /// again. Returns the response to pass on, or null if the proxy closed
    /// the connection.
    /// </summary>
    Task<CapturedResponse?> SignInIfAskedAsync(CapturedRequest request, CapturedResponse response, CancellationToken cancellationToken);

    Task<byte[]> ReadResponseBodyAsync(CapturedResponse preamble, CancellationToken cancellationToken);

    /// <summary>The trailers that ended the response body just read or relayed; null if there were none.</summary>
    IReadOnlyList<(string Name, string Value)>? ResponseTrailers { get; }

    /// <summary>Relays the response body to <paramref name="client"/> as it arrives.</summary>
    Task<Http1MessageReader.RelayedBody> RelayResponseBodyAsync(
        CapturedResponse preamble, IClientExchange client, long maxCapturedBodyBytes, CancellationToken cancellationToken);
}
