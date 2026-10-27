using System.Globalization;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Http2;

namespace Violens.ProxyCore.Proxy;

/// <summary>
/// One exchange with the server as a stream on a shared HTTP/2 connection.
/// The connection was opened (and timed) by whichever exchange negotiated
/// it; every other exchange on it shows as a reused connection.
/// </summary>
internal sealed class Http2Upstream(
    Http2Connection connection, UpstreamConnection opened, string targetHost, int targetPort, bool openedForThisExchange) : IUpstream
{
    private Http2Stream? _stream;
    private bool _responseComplete;

    public string TargetHost => targetHost;

    public int TargetPort => targetPort;

    public SessionTimingRecorder? Timing { get; set; }

    public bool ClientCertificateRequested => opened.TlsProbe?.ClientCertificateRequested == true;

    // HTTP/2 never switches protocols with 101.
    public Stream Stream => throw new NotSupportedException("An HTTP/2 stream has no connection of its own to relay.");

    public IReadOnlyList<(string Name, string Value)>? ResponseTrailers => _stream?.Trailers;

    public void RouteFrom(CapturedRequest request)
    {
    }

    public Task ConnectAsync(string target, CancellationToken cancellationToken)
    {
        if (!openedForThisExchange && Timing is not null)
        {
            Timing.ConnectionReused = true;
            Timing.ConnectionId = opened.ConnectionId;
            Timing.Proxy = opened.Route?.ToString();
            Timing.TlsProbe = opened.TlsProbe;
        }

        return Task.CompletedTask;
    }

    public async Task SendRequestAsync(CapturedRequest request, CancellationToken cancellationToken)
    {
        // The body may have been edited, so its length is the body's.
        long? contentLength = request.Body.Length > 0 || ContentDecoder.FindHeader(request.Headers, "Content-Length") is not null
            ? request.Body.Length
            : null;
        _stream = await OpenAsync(Http2Messages.ToRequestBlock(request, Authority, contentLength), endStream: request.Body.Length == 0, cancellationToken);
        if (request.Body.Length > 0)
        {
            await _stream.WriteDataAsync(request.Body, endStream: true, cancellationToken);
        }
    }

    public async Task<Http1MessageReader.RelayedBody> SendRequestRelayingBodyAsync(
        CapturedRequest preamble, IClientExchange client, long maxCapturedBodyBytes, CancellationToken cancellationToken)
    {
        var body = (IClientBodyStream)client;
        _stream = await OpenAsync(Http2Messages.ToRequestBlock(preamble, Authority), endStream: !body.RequestHasBody, cancellationToken);
        if (!body.RequestHasBody)
        {
            return Http1MessageReader.RelayedBody.Empty;
        }

        var captured = new MemoryStream();
        long length = 0;
        var buffer = new byte[16384];
        int read;
        while ((read = await body.ReadRequestBodyAsync(buffer, cancellationToken)) > 0)
        {
            await _stream.WriteDataAsync(buffer.AsMemory(0, read), endStream: false, cancellationToken);
            Capture(captured, buffer, read, maxCapturedBodyBytes);
            length += read;
        }

        await _stream.WriteDataAsync(ReadOnlyMemory<byte>.Empty, endStream: true, cancellationToken);
        return new Http1MessageReader.RelayedBody(captured.ToArray(), length);
    }

    public async Task<CapturedResponse?> ReadResponsePreambleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var block = await _stream!.ReadHeadersAsync(cancellationToken);
            _responseComplete = _stream.HeadersEndedStream;
            return Http2Messages.ToResponse(block);
        }
        catch (Http2StreamClosedException ex)
        {
            ProxyEngine.Log("proxy", $"{targetHost}: {ex.Message}");
            return null;
        }
    }

    // A proxy sign-in happens when the tunnel is opened, never per stream.
    public Task<CapturedResponse?> SignInIfAskedAsync(CapturedRequest request, CapturedResponse response, CancellationToken cancellationToken) =>
        Task.FromResult<CapturedResponse?>(response);

    public async Task<byte[]> ReadResponseBodyAsync(CapturedResponse preamble, CancellationToken cancellationToken)
    {
        var body = await _stream!.ReadAllDataAsync(cancellationToken);
        _responseComplete = true;
        return body;
    }

    public async Task<Http1MessageReader.RelayedBody> RelayResponseBodyAsync(
        CapturedResponse preamble, IClientExchange client, long maxCapturedBodyBytes, CancellationToken cancellationToken)
    {
        var destination = (IClientBodyStream)client;
        var captured = new MemoryStream();
        long length = 0;
        var buffer = new byte[16384];
        int read;
        while ((read = await _stream!.ReadDataAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteResponseBodyAsync(buffer.AsMemory(0, read), cancellationToken);
            Capture(captured, buffer, read, maxCapturedBodyBytes);
            length += read;
        }

        _responseComplete = true;
        return new Http1MessageReader.RelayedBody(captured.ToArray(), length);
    }

    /// <summary>A stream given up part-way (the client went away, an error) is reset, so the server stops sending.</summary>
    public void Dispose()
    {
        if (_stream is not null && (!_responseComplete || !_stream.IsLocalSideEnded))
        {
            _ = _stream.ResetAsync(Http2ErrorCode.Cancel);
        }
    }

    private string Authority => targetPort == 443 ? targetHost : $"{targetHost}:{targetPort.ToString(CultureInfo.InvariantCulture)}";

    private async Task<Http2Stream> OpenAsync(List<(string Name, string Value)> headers, bool endStream, CancellationToken cancellationToken)
    {
        try
        {
            return await connection.OpenStreamAsync(headers, endStream, cancellationToken);
        }
        catch (Http2StreamClosedException ex)
        {
            // The connection closed or the server sent GOAWAY: an I/O failure, as far as the exchange goes.
            throw new IOException($"The HTTP/2 connection to {targetHost} couldn't take another request: {ex.Message}", ex);
        }
    }

    private static void Capture(MemoryStream captured, byte[] buffer, int count, long limit)
    {
        var room = limit - captured.Length;
        if (room > 0)
        {
            captured.Write(buffer, 0, (int)Math.Min(room, count));
        }
    }
}
