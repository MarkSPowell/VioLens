using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Violens.ProxyCore.AutoResponder;
using Violens.ProxyCore.Breakpoints;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests.Http2;

/// <summary>
/// The listener with HTTP/2 to the client: HttpClient through a CONNECT
/// tunnel, speaking HTTP/2 to VioLens, which goes on to a Kestrel origin over
/// HTTP/1.1 and TLS.
/// </summary>
public sealed class ListenerHttp2Tests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly SessionStore _store = new();
    private readonly List<Session> _sessions = [];
    private X509Certificate2 _root = null!;
    private WebApplication _origin = null!;
    private int _originPort;

    public async Task InitializeAsync()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        _root = CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
        _store.SessionAdded += session =>
        {
            lock (_sessions)
            {
                _sessions.Add(session);
            }
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // Both IPv4 and IPv6: "localhost" resolves to ::1 first, and on Windows a
        // refused loopback connection takes seconds to fail over to 127.0.0.1.
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.IPv6Any, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            listen.UseHttps(OriginCertificate());
        }));
        _origin = builder.Build();
        _origin.MapGet("/hello", () => "hello over http/1.1");
        _origin.MapPost("/echo", async (HttpContext context) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            context.Response.Headers["x-request-length"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["x-chunked-request"] = context.Request.Headers.TransferEncoding.ToString();
            await context.Response.Body.WriteAsync(body.ToArray());
        });
        _origin.MapGet("/stream", async (HttpContext context) =>
        {
            // No Content-Length, flushed in pieces: Kestrel sends it chunked.
            for (var i = 0; i < 5; i++)
            {
                await context.Response.WriteAsync($"piece {i};");
                await context.Response.Body.FlushAsync();
            }
        });
        _origin.MapGet("/cookies", (HttpContext context) => context.Request.Headers.Cookie.ToString());
        await _origin.StartAsync();
        _originPort = new Uri(_origin.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
    }

    public async Task DisposeAsync()
    {
        await _origin.StopAsync();
        await _origin.DisposeAsync();
        _root.Dispose();
    }

    [Fact]
    public async Task ABrowserCanUseHttp2WithTheProxy()
    {
        var listener = Start();
        try
        {
            using var client = NewClient(listener.Port);
            using var response = await client.GetAsync($"https://localhost:{_originPort}/hello").WaitAsync(Wait);

            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal("hello over http/1.1", await response.Content.ReadAsStringAsync());

            var session = await WaitForSessionAsync(s => s.Request.Target == "/hello");
            Assert.Equal("HTTP/2", session.Request.HttpVersion);
            Assert.Equal("GET", session.Request.Method);
            Assert.Equal("localhost", session.Host);
            Assert.Equal($"localhost:{_originPort}", Http.ContentDecoder.FindHeader(session.Request.Headers, "Host"));
            Assert.Equal("hello over http/1.1", Encoding.UTF8.GetString(session.Response.Body));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RequestBodiesAreRelayedWithAndWithoutALength()
    {
        var listener = Start();
        try
        {
            using var client = NewClient(listener.Port);
            var payload = new byte[2 * 1024 * 1024];
            new Random(5).NextBytes(payload);

            using var withLength = await client.PostAsync($"https://localhost:{_originPort}/echo", new ByteArrayContent(payload)).WaitAsync(Wait);
            Assert.Equal(payload, await withLength.Content.ReadAsByteArrayAsync());
            Assert.Equal(string.Empty, withLength.Headers.GetValues("x-chunked-request").Single());

            // No Content-Length on the HTTP/2 side: it goes to the server chunked.
            using var withoutLength = await client.PostAsync(
                $"https://localhost:{_originPort}/echo", new StreamContent(new UnknownLengthStream(payload))).WaitAsync(Wait);
            Assert.Equal(payload, await withoutLength.Content.ReadAsByteArrayAsync());
            Assert.Equal("chunked", withoutLength.Headers.GetValues("x-chunked-request").Single());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AChunkedResponseIsRelayedAsItArrives()
    {
        var listener = Start();
        try
        {
            using var client = NewClient(listener.Port);
            using var response = await client.GetAsync($"https://localhost:{_originPort}/stream").WaitAsync(Wait);

            Assert.Equal("piece 0;piece 1;piece 2;piece 3;piece 4;", await response.Content.ReadAsStringAsync());
            var session = await WaitForSessionAsync(s => s.Request.Target == "/stream");
            Assert.Equal("piece 0;piece 1;piece 2;piece 3;piece 4;", Encoding.UTF8.GetString(session.Response.Body));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ManyRequestsShareOneHttp2Connection()
    {
        var listener = Start();
        try
        {
            using var client = NewClient(listener.Port);
            var bodies = await Task.WhenAll(Enumerable.Range(0, 60).Select(async i =>
            {
                using var response = await client.PostAsync($"https://localhost:{_originPort}/echo", new StringContent($"request {i}"));
                return await response.Content.ReadAsStringAsync();
            })).WaitAsync(Wait);

            Assert.Equal(Enumerable.Range(0, 60).Select(i => $"request {i}"), bodies);
            await WaitForSessionCountAsync(60);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SplitCookiesReachTheServerAsOneHeader()
    {
        var listener = Start();
        try
        {
            using var client = NewClient(listener.Port);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:{_originPort}/cookies");
            request.Headers.Add("Cookie", ["a=1", "b=2"]);
            using var response = await client.SendAsync(request).WaitAsync(Wait);

            Assert.Equal("a=1; b=2", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task WithHttp2OffTheClientGetsHttp11()
    {
        var listener = Start(http2: false);
        try
        {
            using var client = NewClient(listener.Port, HttpVersionPolicy.RequestVersionOrLower);
            using var response = await client.GetAsync($"https://localhost:{_originPort}/hello").WaitAsync(Wait);

            Assert.Equal(HttpVersion.Version11, response.Version);
            Assert.Equal("hello over http/1.1", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AutoResponderAnswersHttp2Requests()
    {
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(new AutoResponderRule { MatchPattern = "/mocked", Action = "*redir:https://elsewhere.test/" });
        var listener = Start(autoResponderRules: rules);
        try
        {
            using var client = NewClient(listener.Port);
            using var response = await client.GetAsync($"https://localhost:{_originPort}/mocked").WaitAsync(Wait);

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal("https://elsewhere.test/", response.Headers.Location!.ToString());

            // VioLens answered, so the session shows HTTP/2 on both sides, not a server's HTTP/1.1.
            var session = await WaitForSessionAsync(s => s.Request.Target == "/mocked");
            Assert.Equal("HTTP/2", session.Response.HttpVersion);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task APausedStreamDoesntHoldUpTheOthers()
    {
        var breakpoints = new BreakpointManager();
        breakpoints.Rules.RequestUrlContains = "/hello?pause";
        var paused = new TaskCompletionSource<PendingBreakpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        breakpoints.BreakpointHit += pending => paused.TrySetResult(pending);
        var listener = Start(breakpointManager: breakpoints);
        try
        {
            using var client = NewClient(listener.Port);
            var pausedRequest = client.GetStringAsync($"https://localhost:{_originPort}/hello?pause");
            var pending = await paused.Task.WaitAsync(Wait);

            // The same HTTP/2 connection carries another request while the first is paused.
            Assert.Equal("hello over http/1.1", await client.GetStringAsync($"https://localhost:{_originPort}/hello").WaitAsync(Wait));
            Assert.False(pausedRequest.IsCompleted);

            pending.Resume();
            Assert.Equal("hello over http/1.1", await pausedRequest.WaitAsync(Wait));
        }
        finally
        {
            listener.Stop();
        }
    }

    private InterceptingProxyListener Start(
        bool http2 = true, AutoResponderRules? autoResponderRules = null, BreakpointManager? breakpointManager = null)
    {
        var listener = InterceptingProxyListener.StartOnAvailablePort(
            0, new LeafCertificateProvider(_root), _store, breakpointManager, autoResponderRules);
        listener.Http2Enabled = http2;
        listener.UpstreamCertificateValidation = (_, _, _, _) => true;
        return listener;
    }

    private static HttpClient NewClient(int proxyPort, HttpVersionPolicy policy = HttpVersionPolicy.RequestVersionExact) =>
        new(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        })
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = policy,
            Timeout = Wait,
        };

    private async Task<Session> WaitForSessionAsync(Func<Session, bool> match)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            lock (_sessions)
            {
                if (_sessions.FirstOrDefault(match) is { } session)
                {
                    return session;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "No matching session was recorded.");
            await Task.Delay(20);
        }
    }

    private async Task WaitForSessionCountAsync(int count)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            lock (_sessions)
            {
                if (_sessions.Count >= count)
                {
                    return;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "Not every session was recorded.");
            await Task.Delay(20);
        }
    }

    private static X509Certificate2 OriginCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Through PFX, so the key isn't ephemeral (Schannel won't use one that is).
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
    }

    /// <summary>A stream that won't say how long it is, so HttpClient sends no Content-Length.</summary>
    private sealed class UnknownLengthStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
