using System.IO.Pipes;
using Violens.LegacyExtensionHost.Bridge;
using Violens.ProxyCore.Extensions;
using Violens.ProxyCore.Http;

namespace Violens.Compatibility.Extensions;

/// <summary>
/// Runs legacy extensions' <c>IAutoTamper</c> hooks in the separate
/// <c>Violens.LegacyExtensionHost.exe</c> over a named pipe. Never required:
/// <see cref="Probe"/> checks once per Start, with a short timeout, and if
/// the host isn't there every hook passes traffic through unchanged. One
/// pipe connection per call, which keeps concurrent connections from
/// interleaving and is cheap on the same machine.
/// </summary>
public sealed class LegacyExtensionHostBridgeClient : IExtensionAutoTamperHost
{
    private readonly Action<string> _log;
    private readonly bool _reachable;

    private LegacyExtensionHostBridgeClient(bool reachable, Action<string> log)
    {
        _reachable = reachable;
        _log = log;
    }

    /// <summary>
    /// Asks a running legacy host how many <c>IAutoTamper</c> extensions it has.
    /// Never throws; if it's unreachable or has none, every hook passes through.
    /// </summary>
    public static LegacyExtensionHostBridgeClient Probe(Action<string>? log = null)
    {
        log ??= _ => { };

        var response = Exchange(new BridgeRequestMessage { Kind = BridgeMessageKind.Capabilities }, log);
        if (response is { Ok: true, AutoTamperCount: > 0 })
        {
            log($"[LegacyExtensionHost] Session bridge reachable -- {response.AutoTamperCount} AutoTamper extension(s) loaded there.");
            return new LegacyExtensionHostBridgeClient(reachable: true, log);
        }

        if (response is { Ok: true })
        {
            log("[LegacyExtensionHost] Session bridge reachable, but no AutoTamper extensions are loaded there -- nothing to bridge this run.");
        }
        // A null/failed response already logged its own reason inside
        // Exchange (connect timeout, or the pipe wasn't there at all) --
        // nothing more to say here.

        return new LegacyExtensionHostBridgeClient(reachable: false, log);
    }

    /// <summary>
    /// Whether anything is listening on the pipe, whatever it has loaded. The
    /// launcher uses this, since a host with no AutoTampers is still running
    /// and mustn't be started twice.
    /// </summary>
    public static bool Ping(Action<string>? log = null)
    {
        log ??= _ => { };
        var response = Exchange(new BridgeRequestMessage { Kind = BridgeMessageKind.Capabilities }, log);
        return response is { Ok: true };
    }

    /// <inheritdoc/>
    public bool HasAnyRequestBeforeHandlers => _reachable;

    /// <inheritdoc/>
    public bool HasAnyResponseBeforeHandlers => _reachable;

    /// <inheritdoc/>
    public CapturedRequest RunRequestBefore(int sessionOrdinal, string hostname, CapturedRequest request)
    {
        if (!_reachable)
        {
            return request;
        }

        var message = new BridgeRequestMessage
        {
            Kind = BridgeMessageKind.RequestBefore,
            SessionOrdinal = sessionOrdinal,
            Hostname = hostname,
            Request = ToWire(request),
        };

        var response = Exchange(message, _log);
        return response is { Ok: true, Request: not null } ? FromWire(response.Request) : request;
    }

    /// <inheritdoc/>
    public void RunRequestAfter(int sessionOrdinal, string hostname, CapturedRequest request)
    {
        if (!_reachable)
        {
            return;
        }

        var message = new BridgeRequestMessage
        {
            Kind = BridgeMessageKind.RequestAfter,
            SessionOrdinal = sessionOrdinal,
            Hostname = hostname,
            Request = ToWire(request),
        };

        // The request has already been sent; nothing to read back.
        Exchange(message, _log);
    }

    /// <inheritdoc/>
    public CapturedResponse RunResponseBefore(int sessionOrdinal, string hostname, CapturedRequest request, CapturedResponse response)
    {
        if (!_reachable)
        {
            return response;
        }

        var message = new BridgeRequestMessage
        {
            Kind = BridgeMessageKind.ResponseBefore,
            SessionOrdinal = sessionOrdinal,
            Hostname = hostname,
            Request = ToWire(request),
            Response = ToWire(response),
        };

        var result = Exchange(message, _log);
        return result is { Ok: true, Response: not null } ? FromWire(result.Response) : response;
    }

    /// <inheritdoc/>
    public void RunResponseAfter(int sessionOrdinal, string hostname, CapturedRequest request, CapturedResponse response)
    {
        if (!_reachable)
        {
            return;
        }

        var message = new BridgeRequestMessage
        {
            Kind = BridgeMessageKind.ResponseAfter,
            SessionOrdinal = sessionOrdinal,
            Hostname = hostname,
            Request = ToWire(request),
            Response = ToWire(response),
        };

        Exchange(message, _log);
    }

    /// <summary>
    /// One round trip: connect and exchange, each with a timeout. Never throws:
    /// failures are logged and return null, so traffic passes through
    /// unchanged.
    /// </summary>
    private static BridgeResponseMessage? Exchange(BridgeRequestMessage message, Action<string> log)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            // CurrentUserOnly: refuse a pipe of this name made by another
            // account, which would otherwise receive every decrypted session.
            pipe = new NamedPipeClientStream(".", SessionBridgeProtocol.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(SessionBridgeProtocol.ConnectTimeoutMilliseconds);

            var localPipe = pipe;
            var exchangeTask = Task.Run(() =>
            {
                SessionBridgeProtocol.WriteMessage(localPipe, message);
                return SessionBridgeProtocol.ReadMessage<BridgeResponseMessage>(localPipe);
            });

            if (!exchangeTask.Wait(SessionBridgeProtocol.RoundTripTimeoutMilliseconds))
            {
                // Disposing the pipe is the only way to unblock a synchronous
                // Read/Write.
                localPipe.Dispose();
                throw new TimeoutException($"Session bridge round trip for {message.Kind} did not complete within {SessionBridgeProtocol.RoundTripTimeoutMilliseconds}ms.");
            }

            return exchangeTask.Result;
        }
        catch (Exception ex)
        {
            log($"[LegacyExtensionHost] Session bridge call ({message.Kind}) failed -- falling back to the unmodified request/response: {ex.Message}");
            return null;
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    private static WireRequest ToWire(CapturedRequest request) => new()
    {
        Method = request.Method,
        Target = request.Target,
        HttpVersion = request.HttpVersion,
        Headers = request.Headers.Select(h => new WireHeader { Name = h.Name, Value = h.Value }).ToList(),
        Body = request.Body,
    };

    private static CapturedRequest FromWire(WireRequest wire) => new(
        wire.Method,
        wire.Target,
        wire.HttpVersion,
        wire.Headers.Select(h => (h.Name, h.Value)).ToList(),
        wire.Body);

    private static WireResponse ToWire(CapturedResponse response) => new()
    {
        HttpVersion = response.HttpVersion,
        StatusCode = response.StatusCode,
        ReasonPhrase = response.ReasonPhrase,
        Headers = response.Headers.Select(h => new WireHeader { Name = h.Name, Value = h.Value }).ToList(),
        Body = response.Body,
    };

    private static CapturedResponse FromWire(WireResponse wire) => new(
        wire.HttpVersion,
        wire.StatusCode,
        wire.ReasonPhrase,
        wire.Headers.Select(h => (h.Name, h.Value)).ToList(),
        wire.Body);
}
