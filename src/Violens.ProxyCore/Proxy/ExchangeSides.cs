using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// The client's side of one exchange, as <see cref="ExchangePipeline"/> uses
/// it. For HTTP/1.1 that's <see cref="Http1ClientExchange"/>.
/// </summary>
internal interface IClientExchange
{
    /// <summary>The connection, for relaying bodies and switched protocols.</summary>
    Stream Stream { get; }

    Task<byte[]> ReadRequestBodyAsync(CapturedRequest preamble, CancellationToken cancellationToken);

    Task WriteResponseAsync(CapturedResponse response, CancellationToken cancellationToken);

    /// <summary>The status line and headers only: an interim response, or one whose body follows by relay.</summary>
    Task WriteResponsePreambleAsync(CapturedResponse response, CancellationToken cancellationToken);

    /// <summary>Ends the connection with a TCP reset (AutoResponder's <c>*reset</c>).</summary>
    void Reset();
}

/// <summary>
/// The connection to the server, or to the network's proxy, that one client
/// connection's exchanges go out on. For HTTP/1.1 that's <see cref="Http1Upstream"/>.
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

    /// <summary>Sends the request line and headers, then relays the body from <paramref name="bodySource"/> as it arrives.</summary>
    Task<Http1MessageReader.RelayedBody> SendRequestRelayingBodyAsync(
        CapturedRequest preamble, Stream bodySource, long maxCapturedBodyBytes, CancellationToken cancellationToken);

    /// <summary>The next status line and headers; null if the connection closed first.</summary>
    Task<CapturedResponse?> ReadResponsePreambleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// When the network's proxy answered 407, signs in and sends the request
    /// again. Returns the response to pass on, or null if the proxy closed
    /// the connection.
    /// </summary>
    Task<CapturedResponse?> SignInIfAskedAsync(CapturedRequest request, CapturedResponse response, CancellationToken cancellationToken);

    Task<byte[]> ReadResponseBodyAsync(CapturedResponse preamble, CancellationToken cancellationToken);

    Task<Http1MessageReader.RelayedBody> RelayResponseBodyAsync(
        CapturedResponse preamble, Stream destination, long maxCapturedBodyBytes, CancellationToken cancellationToken);
}
