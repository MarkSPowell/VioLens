using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Violens.ProxyCore.AutoResponder;
using Violens.ProxyCore.Breakpoints;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Extensions;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Scripting;
using Violens.ProxyCore.Sessions;
using Xunit;

namespace Violens.ProxyCore.Tests;

/// <summary>
/// What happens to an exchange on its way through the listener: FiddlerScript,
/// extensions, breakpoints and AutoResponder, and when the client connection
/// ends. Plain HTTP, so no TLS is involved.
/// </summary>
public class ListenerHookTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AScriptEditsTheRequestBeforeItsSentAndTheResponseBeforeItsReturned()
    {
        using var root = NewRoot();
        await using var origin = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 6\r\n\r\norigin");
        var (store, recorded) = RecordingStore();
        var script = new RecordingScript();
        var listener = InterceptingProxyListener.StartOnAvailablePort(
            0, new LeafCertificateProvider(root), store, scriptRunner: script);
        try
        {
            var response = await SendPlainAsync(listener.Port, Get(origin.Port));

            var head = await origin.Received.Task.WaitAsync(Wait);
            Assert.Contains("X-Script: request\r\n", head);
            Assert.Equal("edited by script", Encoding.ASCII.GetString(response.Body));

            var session = await recorded.Task.WaitAsync(Wait);
            Assert.Equal("request", ContentDecoder.FindHeader(session.Request.Headers, "X-Script"));
            Assert.Equal("edited by script", Encoding.ASCII.GetString(session.Response.Body));
            Assert.Equal("127.0.0.1", script.Hostname);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ExtensionHooksRunAfterTheScriptAndSeeItsEdits()
    {
        using var root = NewRoot();
        await using var origin = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 6\r\n\r\norigin");
        var (store, recorded) = RecordingStore();
        var calls = new List<string>();
        var script = new RecordingScript(calls);
        var extensions = new RecordingExtensions(calls);
        var listener = InterceptingProxyListener.StartOnAvailablePort(
            0, new LeafCertificateProvider(root), store, scriptRunner: script, extensionHost: extensions);
        try
        {
            var response = await SendPlainAsync(listener.Port, Get(origin.Port));
            var head = await origin.Received.Task.WaitAsync(Wait);
            await recorded.Task.WaitAsync(Wait);

            Assert.Contains("X-Script: request\r\n", head);
            Assert.Contains("X-Extension: saw-script\r\n", head);
            Assert.Equal("edited by script, then extension", Encoding.ASCII.GetString(response.Body));
            lock (calls)
            {
                Assert.Equal(
                    ["script request", "extension request before", "extension request after", "script response", "extension response before", "extension response after"],
                    calls);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ARequestBreakpointsEditReachesTheServer()
    {
        using var root = NewRoot();
        await using var origin = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
        var (store, recorded) = RecordingStore();
        var breakpoints = new BreakpointManager();
        breakpoints.Rules.RequestUrlContains = "/pause";
        breakpoints.BreakpointHit += pending =>
        {
            Assert.True(pending.TryEdit(pending.RawText.Replace("\r\n\r\n", "\r\nX-Edited: yes\r\n\r\n", StringComparison.Ordinal)));
            pending.Resume();
        };
        var listener = InterceptingProxyListener.StartOnAvailablePort(
            0, new LeafCertificateProvider(root), store, breakpointManager: breakpoints);
        try
        {
            var response = await SendPlainAsync(listener.Port, Get(origin.Port, "/pause"));
            var head = await origin.Received.Task.WaitAsync(Wait);
            var session = await recorded.Task.WaitAsync(Wait);

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("X-Edited: yes", head);
            Assert.Equal("yes", ContentDecoder.FindHeader(session.Request.Headers, "X-Edited"));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AutoResponderResetEndsTheConnectionAndRecordsIt()
    {
        using var root = NewRoot();
        var (store, recorded) = RecordingStore();
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(new AutoResponderRule { MatchPattern = "reset.test", Action = "*reset" });
        var listener = InterceptingProxyListener.StartOnAvailablePort(
            0, new LeafCertificateProvider(root), store, autoResponderRules: rules);
        try
        {
            using var timeout = new CancellationTokenSource(Wait);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET http://reset.test/ HTTP/1.1\r\nHost: reset.test\r\n\r\n"), timeout.Token);

            Assert.True(await EndsWithoutAResponseAsync(stream, timeout.Token));

            var session = await recorded.Task.WaitAsync(Wait);
            Assert.Equal(0, session.Response.StatusCode);
            Assert.Equal("Connection Reset (AutoResponder *reset)", session.Response.ReasonPhrase);
            Assert.True(session.Timings?.AnsweredLocally);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AutoResponderAnswersAndTheConnectionCarriesOn()
    {
        using var root = NewRoot();
        await using var origin = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 6\r\n\r\norigin");
        var rules = new AutoResponderRules { IsEnabled = true };
        rules.Rules.Add(new AutoResponderRule { MatchPattern = "mocked.test", Action = "*redir:http://elsewhere.test/" });
        var listener = InterceptingProxyListener.StartOnAvailablePort(
            0, new LeafCertificateProvider(root), new SessionStore(), autoResponderRules: rules);
        try
        {
            using var timeout = new CancellationTokenSource(Wait);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();

            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET http://mocked.test/ HTTP/1.1\r\nHost: mocked.test\r\n\r\n"), timeout.Token);
            var redirect = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
            Assert.Equal(302, redirect!.StatusCode);

            await stream.WriteAsync(Encoding.ASCII.GetBytes(Get(origin.Port)), timeout.Token);
            var forwarded = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
            Assert.Equal("origin", Encoding.ASCII.GetString(forwarded!.Body));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AResponseAskingToCloseEndsTheClientConnection()
    {
        using var root = NewRoot();
        await using var origin = UpstreamProxyTests.FakeProxy.Start("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        var listener = InterceptingProxyListener.StartOnAvailablePort(0, new LeafCertificateProvider(root), new SessionStore());
        try
        {
            using var timeout = new CancellationTokenSource(Wait);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(Get(origin.Port)), timeout.Token);

            var response = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
            Assert.Equal("ok", Encoding.ASCII.GetString(response!.Body));
            Assert.True(await EndsWithoutAResponseAsync(stream, timeout.Token));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string Get(int port, string path = "/") =>
        $"GET http://127.0.0.1:{port}{path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n";

    private static X509Certificate2 NewRoot()
    {
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        return CertificateAuthority.GenerateRoot(notBefore, notBefore.AddDays(1));
    }

    private static (SessionStore Store, TaskCompletionSource<Session> Recorded) RecordingStore()
    {
        var store = new SessionStore();
        var recorded = new TaskCompletionSource<Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.SessionAdded += session => recorded.TrySetResult(session);
        return (store, recorded);
    }

    private static async Task<CapturedResponse> SendPlainAsync(int port, string request)
    {
        using var timeout = new CancellationTokenSource(Wait);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        var response = await Http1MessageReader.ReadResponseAsync(stream, isResponseToHeadRequest: false, timeout.Token);
        Assert.NotNull(response);
        return response!;
    }

    /// <summary>True when the listener closes or resets the connection before sending anything.</summary>
    private static async Task<bool> EndsWithoutAResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.ReadAsync(new byte[1], cancellationToken) == 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static CapturedResponse WithBody(CapturedResponse response, string body)
    {
        var bytes = Encoding.ASCII.GetBytes(body);
        var headers = response.Headers
            .Where(h => !string.Equals(h.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            .Append(("Content-Length", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        return response with { Headers = headers, Body = bytes };
    }

    private sealed class RecordingScript(List<string>? calls = null) : IScriptRunner
    {
        public string? Hostname { get; private set; }

        public bool HasOnBeforeRequest => true;

        public bool HasOnBeforeResponse => true;

        public ScriptRequestResult RunOnBeforeRequest(int sessionOrdinal, string hostname, CapturedRequest request)
        {
            Record("script request");
            Hostname = hostname;
            return new ScriptRequestResult(request with { Headers = [.. request.Headers, ("X-Script", "request")] });
        }

        public ScriptResponseResult RunOnBeforeResponse(int sessionOrdinal, string hostname, CapturedRequest request, CapturedResponse response)
        {
            Record("script response");
            return new ScriptResponseResult(WithBody(response, "edited by script"));
        }

        private void Record(string call)
        {
            if (calls is not null)
            {
                lock (calls)
                {
                    calls.Add(call);
                }
            }
        }
    }

    private sealed class RecordingExtensions(List<string> calls) : IExtensionAutoTamperHost
    {
        public bool HasAnyRequestBeforeHandlers => true;

        public bool HasAnyResponseBeforeHandlers => true;

        public CapturedRequest RunRequestBefore(int sessionOrdinal, string hostname, CapturedRequest request)
        {
            Record("extension request before");
            var sawScript = ContentDecoder.FindHeader(request.Headers, "X-Script") is not null;
            return request with { Headers = [.. request.Headers, ("X-Extension", sawScript ? "saw-script" : "no-script")] };
        }

        public void RunRequestAfter(int sessionOrdinal, string hostname, CapturedRequest request) => Record("extension request after");

        public CapturedResponse RunResponseBefore(int sessionOrdinal, string hostname, CapturedRequest request, CapturedResponse response)
        {
            Record("extension response before");
            return WithBody(response, Encoding.ASCII.GetString(response.Body) + ", then extension");
        }

        public void RunResponseAfter(int sessionOrdinal, string hostname, CapturedRequest request, CapturedResponse response) =>
            Record("extension response after");

        private void Record(string call)
        {
            lock (calls)
            {
                calls.Add(call);
            }
        }
    }
}
