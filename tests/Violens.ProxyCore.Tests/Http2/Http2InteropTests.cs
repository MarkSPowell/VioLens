using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Violens.ProxyCore.Http2;
using Xunit;
using static Violens.ProxyCore.Tests.Http2.Http2TestClient;

namespace Violens.ProxyCore.Tests.Http2;

/// <summary>
/// <see cref="Http2Connection"/> against independent implementations: .NET's
/// HttpClient as the client of our server, and Kestrel as the server for our
/// client. Both over cleartext HTTP/2 with prior knowledge, so no TLS is involved.
/// </summary>
public class Http2InteropTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task HttpClientCanTalkToOurServer()
    {
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync);
        using var client = NewHttpClient();

        var payload = new byte[3 * 1024 * 1024];
        new Random(2).NextBytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/upload?x=1")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(payload),
        };
        request.Headers.Add("x-custom", "value");

        using var response = await client.SendAsync(request).WaitAsync(Wait);
        var body = await response.Content.ReadAsByteArrayAsync().WaitAsync(Wait);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal("/upload?x=1", response.Headers.GetValues("x-path").Single());
        Assert.Equal(payload, body);
        Assert.Equal(payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), response.TrailingHeaders.GetValues("x-length").Single());
        Assert.Empty(server.Errors);
    }

    [Fact]
    public async Task HttpClientCanSendManyRequestsAtOnceToOurServer()
    {
        await using var server = new Http2TestServer(Http2TestServer.EchoAsync, new Http2Settings { MaxConcurrentStreams = 16 });
        using var client = NewHttpClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/{i}")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new StringContent($"body {i}"),
            };
            using var response = await client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        })).WaitAsync(Wait);

        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"body {i}"), responses);
        Assert.Single(server.Connections);
        Assert.Empty(server.Errors);
    }

    [Fact]
    public async Task OurClientCanTalkToKestrel()
    {
        await using var kestrel = await StartKestrelAsync();
        var (client, socket) = await ConnectAsync(kestrel.Port);
        using var _ = socket;
        await using var __ = client;

        var payload = new byte[2 * 1024 * 1024];
        new Random(3).NextBytes(payload);
        var stream = await client.OpenStreamAsync(
            [.. Request("POST", "/echo", kestrel.Port), ("content-type", "application/octet-stream")], endStream: false, CancellationToken.None);
        var sending = stream.WriteDataAsync(payload, endStream: true, CancellationToken.None);
        var response = await stream.ReadHeadersAsync(CancellationToken.None).WaitAsync(Wait);
        var body = await stream.ReadAllDataAsync(CancellationToken.None).WaitAsync(Wait);
        await sending.WaitAsync(Wait);

        Assert.Contains((":status", "200"), response);
        Assert.Equal(payload, body);
        Assert.Contains(("x-length", payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)), stream.Trailers!);
    }

    [Fact]
    public async Task OurClientCanSendManyRequestsAtOnceToKestrel()
    {
        await using var kestrel = await StartKestrelAsync();
        var (client, socket) = await ConnectAsync(kestrel.Port);
        using var _ = socket;
        await using var __ = client;

        var bodies = await Task.WhenAll(Enumerable.Range(0, 300).Select(async i =>
        {
            var stream = await client.OpenStreamAsync(Request("GET", $"/hello/{i}", kestrel.Port), endStream: true, CancellationToken.None);
            await stream.ReadHeadersAsync(CancellationToken.None);
            return Text(await stream.ReadAllDataAsync(CancellationToken.None));
        })).WaitAsync(Wait);

        Assert.Equal(Enumerable.Range(0, 300).Select(i => $"hello {i}"), bodies);
    }

    private static HttpClient NewHttpClient() =>
        new(new SocketsHttpHandler { UseProxy = false, EnableMultipleHttp2Connections = false })
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

    private static async Task<KestrelServer> StartKestrelAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        var app = builder.Build();
        app.MapPost("/echo", async (HttpContext context) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            context.Response.ContentType = "application/octet-stream";
            await context.Response.Body.WriteAsync(body.ToArray());
            context.Response.AppendTrailer("x-length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        app.MapGet("/hello/{i}", (string i) => $"hello {i}");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new KestrelServer(app, new Uri(address).Port);
    }

    private sealed record KestrelServer(WebApplication App, int Port) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
