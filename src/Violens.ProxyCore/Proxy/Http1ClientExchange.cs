using System.Net.Sockets;
using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Proxy;

/// <summary>The client's side of an HTTP/1.1 exchange: one request at a time on <paramref name="stream"/>.</summary>
internal sealed class Http1ClientExchange(TcpClient connection, Stream stream) : IClientExchange
{
    /// <summary>The connection, for relaying bodies with their framing intact.</summary>
    public Stream Stream => stream;

    public Task<byte[]> ReadRequestBodyAsync(CapturedRequest preamble, CancellationToken cancellationToken) =>
        Http1MessageReader.ReadBodyAsync(stream, preamble.Headers, cancellationToken);

    public Task WriteResponseAsync(CapturedResponse response, CancellationToken cancellationToken) =>
        HttpMessageWriter.WriteResponseAsync(stream, response, cancellationToken);

    public Task WriteInterimResponseAsync(CapturedResponse response, CancellationToken cancellationToken) =>
        HttpMessageWriter.WriteResponsePreambleAsync(stream, response, cancellationToken);

    public Task WriteResponsePreambleAsync(CapturedResponse response, CancellationToken cancellationToken) =>
        HttpMessageWriter.WriteResponsePreambleAsync(stream, response, cancellationToken);

    // The relayed body carries its own framing, so there's nothing to add.
    // Trailers have no way through the framing that's already gone out.
    public Task EndResponseAsync(IReadOnlyList<(string Name, string Value)>? trailers, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public void Reset()
    {
        // A TCP RST needs a zero linger time.
        connection.Client.LingerState = new LingerOption(true, 0);
        connection.Close();
    }
}
