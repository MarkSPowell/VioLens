using System.Net.Sockets;
using System.Security.Authentication;
using Violens.ProxyCore.AutoResponder;
using Violens.ProxyCore.Breakpoints;
using Violens.ProxyCore.Extensions;
using Violens.ProxyCore.Http;
using Violens.ProxyCore.Scripting;
using Violens.ProxyCore.Sessions;
using Violens.ProxyCore.WebSockets;

namespace Violens.ProxyCore.Proxy;

/// <summary>What every exchange goes through, shared by all of a listener's connections.</summary>
internal sealed record ExchangeServices(
    SessionStore SessionStore,
    BreakpointManager BreakpointManager,
    AutoResponderRules AutoResponderRules,
    IScriptRunner? ScriptRunner,
    IExtensionAutoTamperHost? ExtensionHost,
    IExtensionSessionHost? SessionHost,
    InterceptingProxyListener.UpstreamWebProxy ProxyUrl,
    long MaxCapturedBodyBytes);

/// <summary>How an exchange ended, and what the connection does next.</summary>
internal enum ExchangeEnd
{
    /// <summary>Answered without the server (AutoResponder or an extension); the connection carries on.</summary>
    AnsweredLocally,

    /// <summary>The connection must close.</summary>
    CloseConnection,

    /// <summary>The server's response was passed on; see <see cref="ExchangeResult"/> for what the connection needs to decide next.</summary>
    Forwarded,
}

/// <summary>
/// The outcome of <see cref="ExchangePipeline.RunAsync"/>. For
/// <see cref="ExchangeEnd.Forwarded"/>, the request as sent, the response's
/// preamble and the response as passed on, which the connection uses to
/// decide whether it can carry another exchange.
/// </summary>
internal sealed record ExchangeResult(
    ExchangeEnd End,
    CapturedRequest? Request = null,
    CapturedResponse? ResponsePreamble = null,
    CapturedResponse? Response = null,
    bool ResponseHasNoBody = false,
    WebSocketLog? WebSocket = null)
{
    public static ExchangeResult AnsweredLocally { get; } = new(ExchangeEnd.AnsweredLocally);

    public static ExchangeResult CloseConnection { get; } = new(ExchangeEnd.CloseConnection);
}

/// <summary>
/// One request and its response, from the moment the request's headers have
/// arrived: AutoResponder, FiddlerScript, both kinds of extension, breakpoints,
/// forwarding and recording the session. Protocol framing is left to the two
/// sides: <see cref="IClientExchange"/> and <see cref="IUpstream"/>.
///
/// Per request, before it's forwarded: FiddlerScript's OnBeforeRequest,
/// then VioLens extensions' AutoTamperRequestBefore, then ported
/// extensions', then the request breakpoint. Fiddler runs the script before
/// breakpoints; the extension order is assumed. Responses follow the same
/// order. Requests AutoResponder answers skip all of these. The After hooks
/// run once each message is sent, whether buffered or relayed. Bodies are
/// buffered only when something will act on them; otherwise they're relayed
/// as they arrive.
/// </summary>
internal sealed class ExchangePipeline(ExchangeServices services)
{
    /// <param name="requestPreamble">The request line and headers, in the stored shape: a path-only target plus a Host header.</param>
    /// <param name="url">The request's full URL, for AutoResponder and the log.</param>
    /// <param name="scheme"><c>https</c> or <c>http</c>.</param>
    /// <param name="startedAt">When the request arrived.</param>
    /// <param name="timing">This exchange's timings; <paramref name="upstream"/> records connecting into it too.</param>
    public async Task<ExchangeResult> RunAsync(
        IClientExchange client,
        IUpstream upstream,
        CapturedRequest requestPreamble,
        string url,
        string scheme,
        DateTimeOffset startedAt,
        SessionTimingRecorder timing,
        CancellationToken cancellationToken)
    {
        var sessionStore = services.SessionStore;
        var breakpointManager = services.BreakpointManager;
        var autoResponderRules = services.AutoResponderRules;
        var scriptRunner = services.ScriptRunner;
        var extensionHost = services.ExtensionHost;
        var sessionHost = services.SessionHost;

        // AutoResponder only looks at the method and URL, so it can decide from
        // the preamble.
        var outcome = autoResponderRules.AnyActive
            ? autoResponderRules.Evaluate(requestPreamble.Method, url)
            : AutoResponderOutcome.PassThrough;

        if (outcome.DelayMilliseconds > 0)
        {
            // Applies whether the request is answered locally or forwarded.
            await Task.Delay(outcome.DelayMilliseconds, cancellationToken);
        }

        var isAnsweredLocally = outcome.FinalActionKind is
            AutoResponderActionKind.ServeFile or AutoResponderActionKind.ProxyUrl or AutoResponderActionKind.Redirect or
            AutoResponderActionKind.Reset or AutoResponderActionKind.Drop or AutoResponderActionKind.CorsPreflightAllow;

        if (isAnsweredLocally)
        {
            // Answered locally, so there's nothing to relay.
            var localRequestBody = await client.ReadRequestBodyAsync(requestPreamble, cancellationToken);
            var localRequest = requestPreamble with { Body = localRequestBody };

            if (outcome.FinalActionKind == AutoResponderActionKind.Reset)
            {
                InterceptingProxyListener.RecordAutoRespondedSession(
                    sessionStore, upstream.TargetHost, scheme, startedAt, localRequest, "Connection Reset (AutoResponder *reset)", timing.Build(answeredLocally: true));
                client.Reset();
                return ExchangeResult.CloseConnection;
            }

            if (outcome.FinalActionKind == AutoResponderActionKind.Drop)
            {
                InterceptingProxyListener.RecordAutoRespondedSession(
                    sessionStore, upstream.TargetHost, scheme, startedAt, localRequest, "Connection Dropped (AutoResponder *drop)", timing.Build(answeredLocally: true));
                // No response; the connection just closes.
                return ExchangeResult.CloseConnection;
            }

            var localResponse = InVersionOf(
                localRequest, await InterceptingProxyListener.BuildMockResponseAsync(outcome, localRequest, services.ProxyUrl, cancellationToken));
            await client.WriteResponseAsync(localResponse, cancellationToken);
            timing.Done();
            var localSession = sessionStore.Add(
                upstream.TargetHost, startedAt, localRequest, localResponse, flags: null, scheme, timing.Build(answeredLocally: true));
            ProxyEngine.Log("session",
                $"#{localSession.Id} {localResponse.StatusCode} {localRequest.Method} {url} " +
                $"(AutoResponder: {outcome.FinalActionKind})");
            return ExchangeResult.AnsweredLocally;
        }

        // Not answered by AutoResponder. The server connection waits until no
        // ported extension answers the request either.
        var outgoingPreamble = outcome.HeadersToSet.Count > 0
            ? requestPreamble with { Headers = InterceptingProxyListener.MergeHeaders(requestPreamble.Headers, outcome.HeadersToSet) }
            : requestPreamble;

        // Ported extensions get one session object for the whole request (see
        // IExtensionSessionHost).
        var extensionSession = sessionHost is { IsActive: true }
            ? sessionHost.BeginSession(sessionStore.PeekNextId(), upstream.TargetHost, scheme)
            : null;
        extensionSession?.PeekAtRequestHeaders(outgoingPreamble);

        // Breakpoint rules only look at the method and target, so this is
        // decided before reading the body: buffer it only if something will
        // act on it, otherwise relay it.
        var mustBufferRequestForScript = scriptRunner?.HasOnBeforeRequest ?? false;
        var mustBufferRequestForExtensions = extensionHost?.HasAnyRequestBeforeHandlers ?? false;
        CapturedRequest request;
        CapturedResponse? extensionLocalResponse = null;
        Http1MessageReader.RelayedBody? relayedRequestBody = null;
        Http1MessageReader.RelayedBody? relayedResponseBody = null;
        if (breakpointManager.WouldBreakBeforeRequest(outgoingPreamble) || outcome.ForceBreakpointBeforeRequest ||
            mustBufferRequestForScript || mustBufferRequestForExtensions || extensionSession is not null)
        {
            var requestBody = await client.ReadRequestBodyAsync(outgoingPreamble, cancellationToken);
            request = outgoingPreamble with { Body = requestBody };

            if (mustBufferRequestForScript)
            {
                var peekedSessionId = sessionStore.PeekNextId();
                request = scriptRunner!.RunOnBeforeRequest(peekedSessionId, upstream.TargetHost, request).Request;
            }

            if (mustBufferRequestForExtensions)
            {
                var peekedSessionId = sessionStore.PeekNextId();
                request = extensionHost!.RunRequestBefore(peekedSessionId, upstream.TargetHost, request);
            }

            // Ported extensions may answer the request themselves, in which case it
            // never reaches the server or a breakpoint.
            if (extensionSession is not null)
            {
                var result = extensionSession.RequestBefore(request);
                request = result.Request;
                extensionLocalResponse = result.LocalResponse;
            }

            if (extensionLocalResponse is null)
            {
                // May pause here for editing.
                var requestPauseStart = SessionTimingRecorder.Now();
                request = await breakpointManager.ApplyRequestBreakpointAsync(upstream.TargetHost, request, cancellationToken);
                timing.AddPause(requestPauseStart, SessionTimingRecorder.Now());
                upstream.RouteFrom(request);
                if (!await TryConnectAsync(client, upstream, request, scheme, startedAt, timing, cancellationToken))
                {
                    return ExchangeResult.CloseConnection;
                }

                timing.SendStarted();
                await upstream.SendRequestAsync(request, cancellationToken);
                timing.SendDone();
            }
        }
        else
        {
            upstream.RouteFrom(outgoingPreamble);
            if (!await TryConnectAsync(client, upstream, outgoingPreamble, scheme, startedAt, timing, cancellationToken))
            {
                return ExchangeResult.CloseConnection;
            }

            timing.SendStarted();
            var requestBody = await upstream.SendRequestRelayingBodyAsync(
                outgoingPreamble, client, services.MaxCapturedBodyBytes, cancellationToken);
            timing.SendDone();
            request = outgoingPreamble with { Body = requestBody.Captured };
            relayedRequestBody = requestBody;
        }

        if (extensionLocalResponse is not null)
        {
            // An extension answered (utilCreateResponseAndBypassServer). Nothing was
            // sent, so the request-after hooks don't run; the response hooks do.
            var localResponse = InVersionOf(request, extensionSession!.ResponseBefore(request, extensionLocalResponse));
            await client.WriteResponseAsync(localResponse, cancellationToken);
            extensionSession.ResponseAfter(request, localResponse);

            timing.Done();
            var answeredSession = sessionStore.Add(
                upstream.TargetHost, startedAt, request, localResponse, extensionSession.Flags, scheme, timing.Build(answeredLocally: true));
            ProxyEngine.Log("session",
                $"#{answeredSession.Id} {localResponse.StatusCode} {request.Method} {url} (answered by an extension)");
            return ExchangeResult.AnsweredLocally;
        }

        extensionHost?.RunRequestAfter(sessionStore.PeekNextId(), upstream.TargetHost, request);
        extensionSession?.RequestAfter(request);

        var isHeadRequest = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
        var responsePreamble = await upstream.ReadResponsePreambleAsync(cancellationToken);
        timing.FirstByte();

        // Interim responses (100 Continue, 102 Processing, 103 Early
        // Hints) come before the real one, on the same request. Pass
        // each on and keep reading: treating one as the answer would
        // leave the real response unread while the client waits for
        // it. 101 is final -- the connection switches protocols.
        while (responsePreamble is { StatusCode: >= 100 and < 200 and not 101 } interim)
        {
            await client.WriteInterimResponseAsync(interim, cancellationToken);
            responsePreamble = await upstream.ReadResponsePreambleAsync(cancellationToken);
        }

        if (responsePreamble is not null)
        {
            responsePreamble = await upstream.SignInIfAskedAsync(request, responsePreamble, cancellationToken);
        }

        if (responsePreamble is null)
        {
            ProxyEngine.Log("proxy",
                $"{upstream.TargetHost}: upstream closed the connection before answering {request.Method} {request.Target}.");
            return ExchangeResult.CloseConnection;
        }

        var responseHasNoBody = Http1MessageReader.ResponseHasNoBody(responsePreamble.StatusCode, isHeadRequest);
        extensionSession?.PeekAtResponseHeaders(request, responsePreamble);

        // Same fork as the request side above, for OnBeforeResponse.
        var mustBufferResponseForScript = scriptRunner?.HasOnBeforeResponse ?? false;
        var mustBufferResponseForExtensions = extensionHost?.HasAnyResponseBeforeHandlers ?? false;
        CapturedResponse response;
        if (breakpointManager.WouldBreakBeforeResponse(request, responsePreamble) || outcome.ForceBreakpointAfterResponse ||
            mustBufferResponseForScript || mustBufferResponseForExtensions || extensionSession is not null)
        {
            var responseBody = responseHasNoBody
                ? Array.Empty<byte>()
                : await upstream.ReadResponseBodyAsync(responsePreamble, cancellationToken);
            var trailers = upstream.ResponseTrailers;
            response = responsePreamble with { Body = responseBody, Trailers = trailers };

            if (mustBufferResponseForScript)
            {
                var peekedSessionId = sessionStore.PeekNextId();
                response = scriptRunner!.RunOnBeforeResponse(peekedSessionId, upstream.TargetHost, request, response).Response;
            }

            if (mustBufferResponseForExtensions)
            {
                var peekedSessionId = sessionStore.PeekNextId();
                response = extensionHost!.RunResponseBefore(peekedSessionId, upstream.TargetHost, request, response);
            }

            // Ported extensions' AutoTamperResponseBefore, on the
            // same session object as their request hooks.
            if (extensionSession is not null)
            {
                response = extensionSession.ResponseBefore(request, response);
            }

            var responsePauseStart = SessionTimingRecorder.Now();
            response = await breakpointManager.ApplyResponseBreakpointAsync(
                upstream.TargetHost, request, response, cancellationToken);
            timing.AddPause(responsePauseStart, SessionTimingRecorder.Now());

            // Scripts, extensions and breakpoint edits rebuild the response
            // without knowing about trailers; they're kept.
            response = response.Trailers is null && trailers is not null ? response with { Trailers = trailers } : response;
            await client.WriteResponseAsync(response, cancellationToken);
        }
        else
        {
            // Relayed live, so a streamed response reaches the client as it's
            // generated.
            await client.WriteResponsePreambleAsync(responsePreamble, cancellationToken);
            var responseBody = responseHasNoBody
                ? Http1MessageReader.RelayedBody.Empty
                : await upstream.RelayResponseBodyAsync(responsePreamble, client, services.MaxCapturedBodyBytes, cancellationToken);
            var trailers = upstream.ResponseTrailers;
            await client.EndResponseAsync(trailers, cancellationToken);
            response = responsePreamble with { Body = responseBody.Captured, Trailers = trailers };
            relayedResponseBody = responseBody;
        }

        extensionHost?.RunResponseAfter(sessionStore.PeekNextId(), upstream.TargetHost, request, response);
        extensionSession?.ResponseAfter(request, response);

        timing.Done();
        var timings = timing.Build();

        // A WebSocket handshake: the session gets a log its messages
        // are added to as they pass.
        var webSocket = response.StatusCode == 101 && InterceptingProxyListener.IsWebSocketUpgrade(response.Headers)
            ? new WebSocketLog(DateTimeOffset.Now, ContentDecoder.FindHeader(response.Headers, "Sec-WebSocket-Extensions"))
            : null;
        var flags = InterceptingProxyListener.WithTruncationFlags(
            InterceptingProxyListener.WithTlsFlags(extensionSession?.Flags, timings), relayedRequestBody, relayedResponseBody);
        var session = sessionStore.Add(
            upstream.TargetHost, startedAt, request, response, flags, scheme, timings, webSocket);
        ProxyEngine.Log("session",
            $"#{session.Id} {response.StatusCode} {request.Method} {scheme}://{upstream.TargetHost}{request.Target} " +
            $"({request.Body.Length} B req, {response.Body.Length} B resp)");

        return new ExchangeResult(ExchangeEnd.Forwarded, request, responsePreamble, response, responseHasNoBody, webSocket);
    }

    /// <summary>
    /// Opens the server connection, or answers 502 Bad Gateway, records it,
    /// and returns false.
    /// </summary>
    private async Task<bool> TryConnectAsync(
        IClientExchange client,
        IUpstream upstream,
        CapturedRequest outgoing,
        string scheme,
        DateTimeOffset startedAt,
        SessionTimingRecorder timing,
        CancellationToken cancellationToken)
    {
        try
        {
            await upstream.ConnectAsync(outgoing.Target, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
        {
            var failure = InVersionOf(outgoing, InterceptingProxyListener.BadGateway(
                upstream.TargetHost,
                upstream.TargetPort,
                ex,
                upstream.ClientCertificateRequested
                    ? $"The server asked for a client certificate (mutual TLS), and {ProxyEngine.Identity.ProductName} doesn't send one yet, so the server may have refused the connection for that reason."
                    : null));
            await client.WriteResponseAsync(failure, cancellationToken);
            timing.Done();
            var failedTimings = timing.Build();
            var failed = services.SessionStore.Add(
                upstream.TargetHost, startedAt, outgoing, failure, InterceptingProxyListener.WithTlsFlags(null, failedTimings), scheme, failedTimings);
            ProxyEngine.Log("session",
                $"#{failed.Id} 502 {outgoing.Method} {scheme}://{upstream.TargetHost}{outgoing.Target} -- {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// A response VioLens made itself, labelled with the HTTP/2 client's
    /// version, so the session doesn't look as if a server answered over
    /// HTTP/1.1. HTTP/1.x clients get it unchanged.
    /// </summary>
    private static CapturedResponse InVersionOf(CapturedRequest request, CapturedResponse response) =>
        HttpVersions.IsHttp2(request.HttpVersion) ? response with { HttpVersion = HttpVersions.Http2 } : response;
}
