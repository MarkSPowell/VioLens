using System.Collections.Concurrent;
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
using Violens.ProxyCore.Breakpoints;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests.Http2;

/// <summary>
/// HTTP/2 all the way: HttpClient speaks HTTP/2 to VioLens, and VioLens to a
/// Kestrel origin that offers HTTP/2 over TLS. (The HTTP/1.1-only origin in
/// <see cref="ListenerHttp2Tests"/> covers falling back.)
/// </summary>
public sealed class ListenerHttp2UpstreamTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly SessionStore _store = new();
    private readonly List<Session> _sessions = [];
    private readonly ConcurrentDictionary<string, int> _originConnections = new();
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
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.IPv6Any, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(OriginCertificate());
        }));
        _origin = builder.Build();
        _origin.Use(async (context, next) =>
        {
            _originConnections.AddOrUpdate(context.Connection.Id, 1, (_, count) => count + 1);
            await next();
        });
        _origin.MapGet("/protocol", (HttpContext context) => context.Request.Protocol);
        _origin.MapPost("/echo", async (HttpContext context) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            await context.Response.Body.WriteAsync(body.ToArray());
        });
        _origin.MapPost("/trailers", async (HttpContext context) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            context.Response.DeclareTrailer("grpc-status");
            await context.Response.WriteAsync($"te: {context.Request.Headers.TE}");
            context.Response.AppendTrailer("grpc-status", "0");
            context.Response.AppendTrailer("x-request-length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
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
    public async Task RequestsGoOnToTheServerOverHttp2()
    {
        var listener = Start();
        try
        {
            using var client = NewClient(listener.Port);
            Assert.Equal("HTTP/2", await client.GetStringAsync($"https://localhost:{_originPort}/protocol").WaitAsync(Wait));

            var session = await WaitForSessionAsync(s => s.Request.Target == "/protocol");
            Assert.Equal("HTTP/2", session.Request.HttpVersion);
            Assert.Equal("HTTP/2", session.Response.HttpVersion);
            Assert.Equal(200, session.Response.StatusCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ManyRequestsShareOneServerConnection()
    {
        var listener = Start();
        try
        {
            using var client = NewClient(listener.Port);
            var bodies = await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
            {
                using var response = await client.PostAsync($"https://localhost:{_originPort}/echo", new StringContent($"request {i}"));
                return await response.Content.ReadAsStringAsync();
            })).WaitAsync(Wait);

            Assert.Equal(Enumerable.Range(0, 50).Select(i => $"request {i}"), bodies);
            Assert.Single(_originConnections);

            // One exchange opened the connection; the waterfall shows every other one reusing it.
            var sessions = await WaitForSessionCountAsync(50);
            Assert.Single(sessions, s => s.Timings?.ConnectionReused == false);
            Assert.Single(sessions.Select(s => s.Timings?.ConnectionId).Distinct());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task LargeBodiesTravelBothWays()
    {
        var listener = Start();
        try
        {
            using var client = NewClient(listener.Port);
            var payload = new byte[3 * 1024 * 1024];
            new Random(9).NextBytes(payload);

            using var response = await client.PostAsync($"https://localhost:{_originPort}/echo", new ByteArrayContent(payload)).WaitAsync(Wait);

            Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TrailersReachTheClientAndTheSession()
    {
        var listener = Start();
        try
        {
            using var client = NewClient(listener.Port);

            // A request built by hand doesn't get the client's default version.
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://localhost:{_originPort}/trailers")
            {
                Content = new StringContent("payload"),
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            request.Headers.TE.ParseAdd("trailers");
            using var response = await client.SendAsync(request).WaitAsync(Wait);

            Assert.Equal("te: trailers", await response.Content.ReadAsStringAsync());
            Assert.Equal("0", response.TrailingHeaders.GetValues("grpc-status").Single());
            Assert.Equal("7", response.TrailingHeaders.GetValues("x-request-length").Single());

            var session = await WaitForSessionAsync(s => s.Request.Target == "/trailers");
            Assert.Contains(("grpc-status", "0"), session.Response.Trailers!);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TrailersSurviveAResponseBreakpoint()
    {
        var breakpoints = new BreakpointManager();
        breakpoints.Rules.ResponseUrlContains = "/trailers";
        breakpoints.BreakpointHit += pending => pending.Resume();
        var listener = Start(breakpoints);
        try
        {
            using var client = NewClient(listener.Port);
            using var response = await client.PostAsync($"https://localhost:{_originPort}/trailers", new StringContent("12345")).WaitAsync(Wait);

            await response.Content.ReadAsStringAsync();
            Assert.Equal("0", response.TrailingHeaders.GetValues("grpc-status").Single());
            Assert.Equal("5", response.TrailingHeaders.GetValues("x-request-length").Single());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TheComposerSendsHttp2WhenTheRequestLineSaysSo()
    {
        var listener = Start();
        try
        {
            var response = await ComposeAsync(listener, "HTTP/2");

            Assert.Equal("HTTP/2", response.HttpVersion);
            Assert.Equal("te: trailers", Encoding.UTF8.GetString(response.Body));
            Assert.Contains(("grpc-status", "0"), response.Trailers!);

            var session = await WaitForSessionAsync(s => s.Request.Target == "/trailers");
            Assert.Equal("HTTP/2", session.Request.HttpVersion);
            Assert.Equal("HTTP/2", session.Response.HttpVersion);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Http2IsOnByDefault()
    {
        var listener = new InterceptingProxyListener(0, new LeafCertificateProvider(_root), new SessionStore());
        Assert.True(listener.Http2Enabled);
    }

    [Fact]
    public async Task TheComposerFallsBackToHttp11WhenHttp2IsOff()
    {
        var listener = Start();
        listener.Http2Enabled = false;
        try
        {
            var response = await ComposeAsync(listener, "HTTP/2");

            Assert.Equal("HTTP/1.1", response.HttpVersion);
            var session = await WaitForSessionAsync(s => s.Request.Target == "/protocol");
            Assert.Equal("HTTP/1.1", session.Request.HttpVersion);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<CapturedResponse> ComposeAsync(InterceptingProxyListener listener, string version)
    {
        // POST /trailers when HTTP/2 can carry the trailers, GET /protocol otherwise.
        var typed = listener.Http2Enabled
            ? new CapturedRequest("POST", $"https://localhost:{_originPort}/trailers", version, [("TE", "trailers")], Encoding.UTF8.GetBytes("payload"))
            : new CapturedRequest("GET", $"https://localhost:{_originPort}/protocol", version, [], []);
        Assert.True(ComposerClient.TryResolve(typed, out var url, out var toSend, out var error), error);
        return await ComposerClient.SendAsync(listener.Port, _root.Export(X509ContentType.Cert), url!, toSend!, CancellationToken.None).WaitAsync(Wait);
    }

    private InterceptingProxyListener Start(BreakpointManager? breakpointManager = null)
    {
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(_root), _store, breakpointManager);
        listener.Http2Enabled = true;
        listener.UpstreamCertificateValidation = (_, _, _, _) => true;
        return listener;
    }

    private static HttpClient NewClient(int proxyPort) =>
        new(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        })
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
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

    private async Task<List<Session>> WaitForSessionCountAsync(int count)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            lock (_sessions)
            {
                if (_sessions.Count >= count)
                {
                    return [.. _sessions];
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
}
