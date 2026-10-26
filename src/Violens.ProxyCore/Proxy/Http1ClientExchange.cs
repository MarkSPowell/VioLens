using System.Net.Sockets;
using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Proxy;

/// <summary>The client's side of an HTTP/1.1 exchange: one request at a time on <paramref name="stream"/>.</summary>
internal sealed class Http1ClientExchange(TcpClient connection, Stream stream) : IClientExchange
{
    public Stream Stream => stream;

    public Task<byte[]> ReadRequestBodyAsync(CapturedRequest preamble, CancellationToken cancellationToken) =>
        Http1MessageReader.ReadBodyAsync(stream, preamble.Headers, cancellationToken);

    public Task WriteResponseAsync(CapturedResponse response, CancellationToken cancellationToken) =>
        HttpMessageWriter.WriteResponseAsync(stream, response, cancellationToken);

    public Task WriteResponsePreambleAsync(CapturedResponse response, CancellationToken cancellationToken) =>
        HttpMessageWriter.WriteResponsePreambleAsync(stream, response, cancellationToken);

    public void Reset()
    {
        // A TCP RST needs a zero linger time.
        connection.Client.LingerState = new LingerOption(true, 0);
        connection.Close();
    }
}
