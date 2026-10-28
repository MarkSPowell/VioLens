# Embedding ProxyCore

`Violens.ProxyCore` is the engine inside VioLens. It listens as a proxy,
decrypts HTTPS, records every request and response, and lets your code look
at and change traffic as it goes through. There's no user interface in it,
so you can put your own on top: a debugger, a capture tool or a test
harness, in Windows Forms, WPF, Avalonia, a console app or a service.

If you're building a Fiddler-style tool, I wrote this guide with you in
mind. It walks through a small working app. The complete code is in
[`samples/EmbeddingSample`](../samples/EmbeddingSample), which builds with
the rest of the solution, so it always matches the real API.

- [What you get](#what-you-get)
- [Requirements](#requirements)
- [Installing](#installing)
- [A minimal app](#a-minimal-app)
- [Your app's identity](#your-apps-identity)
- [Logging](#logging)
- [The root certificate](#the-root-certificate)
- [Seeing traffic](#seeing-traffic)
- [Changing traffic: per-request hooks](#changing-traffic-per-request-hooks)
- [Breakpoints](#breakpoints)
- [AutoResponder](#autoresponder)
- [The system proxy and upstream proxies](#the-system-proxy-and-upstream-proxies)
- [Phones and other computers](#phones-and-other-computers)
- [Sending requests: Composer and Replay](#sending-requests-composer-and-replay)
- [Saving and opening captures](#saving-and-opening-captures)
- [Apps on .NET Framework](#apps-on-net-framework)
- [Versions and compatibility](#versions-and-compatibility)

## What you get

| Area | Types |
|---|---|
| Listening, HTTPS interception | `InterceptingProxyListener`, `CertificateAuthority`, `LeafCertificateProvider` |
| Captured traffic | `SessionStore`, `Session`, `CapturedRequest`, `CapturedResponse`, `SessionTimings`, `WebSocketLog` |
| Changing traffic | `IExtensionSessionHost` (per-request hooks), `IExtensionAutoTamperHost`, `IScriptRunner` |
| Pausing and editing | `BreakpointManager`, `PendingBreakpoint` |
| Answering without the server | `AutoResponderRules` |
| Networks | `SystemProxyController`, `UpstreamProxy`, `ProxyAutoConfig` (PAC and WPAD), proxy sign-in |
| Files | `SazReader`/`SazWriter`, `HarReader`/`HarWriter` |
| Helpers | `ContentDecoder` (gzip, deflate, Brotli, zstd), `HttpMessageText` (raw text), `SessionUrl`, `ComposerClient` |
| Settings | `PreferenceStore`, `IPreferenceStore` |

It speaks HTTP/1.1 to clients and servers, including WebSockets (messages
are recorded, permessage-deflate included), and HTTP/2 over TLS: clients
may pick it, and the listener then uses it with servers that offer it.
That's on by default; set `InterceptingProxyListener.Http2Enabled` to
false to keep every client on HTTP/1.1. An HTTP/2
session's `HttpVersion` is `HTTP/2`, and response trailers are in
`CapturedResponse.Trailers`.

For a long-running host, set `SessionStore.MaxSessions` to keep only the
newest sessions (evictions raise `SessionsRemoved`), and
`InterceptingProxyListener.SkipDecryptionHosts` to tunnel hosts that pin
their certificates without decrypting them. For servers that ask for a
client certificate (mutual TLS), set `ClientCertificate` (with its private
key), and optionally `ClientCertificateHosts` to limit which servers get
it (empty means every server that asks); a session's
`Timings.Tls.ClientCertificateSent` says what was sent.

## Requirements

- .NET 10. ProxyCore uses TLS and compression features that .NET Framework
  lacks. Windows Forms and WPF both run on .NET 10, so a desktop app can
  move across. If your app stays on .NET Framework, it uses the engine
  through a separate host process: see
  [Apps on .NET Framework](#apps-on-net-framework).
- Windows or macOS for the parts that touch the operating system (trusting
  the root certificate, setting the system proxy). Capturing traffic sent
  straight to the proxy works on Linux too.

## Installing

```
dotnet add package Violens.ProxyCore --prerelease
```

Versions match VioLens releases (0.2.0-preview.1 and so on). While the
version starts with 0, a new minor version may change the public API. See
[Versions and compatibility](#versions-and-compatibility).

## A minimal app

```csharp
using Violens.ProxyCore;
using Violens.ProxyCore.Certificates;
using Violens.ProxyCore.Proxy;
using Violens.ProxyCore.Sessions;
using Violens.ProxyCore.SystemProxy;

// Who we are: our own root certificate, data folder and proxy page.
ProxyEngine.Configure(new ProxyHostIdentity("Contoso Web Debugger"),
    log: entry => Console.WriteLine(entry));

// Put the user's proxy settings back if our last run crashed.
SystemProxyController.RecoverFromCrash();

var authority = new CertificateAuthority();           // creates and trusts the root
var sessions = new SessionStore();
sessions.SessionAdded += s =>
    Console.WriteLine($"#{s.Id} {s.Response.StatusCode} {SessionUrl.Of(s)}");

var listener = InterceptingProxyListener.StartOnAvailablePort(
    8888, new LeafCertificateProvider(authority.RootCertificate), sessions);

SystemProxyController.Enable(listener.Port);          // optional
Console.ReadLine();
listener.Stop();
SystemProxyController.Disable();
```

That's a working HTTPS-decrypting proxy. The rest of this guide goes
through each piece.

## Your app's identity

`ProxyEngine.Configure` tells ProxyCore whose app it's running in. Call it
once, at startup, before creating a `CertificateAuthority` or touching the
system proxy.

```csharp
ProxyEngine.Configure(new ProxyHostIdentity("Contoso Web Debugger") { ProductVersion = "1.0" });
```

From the product name, `ProxyHostIdentity` derives:

| Setting | For "Contoso Web Debugger" | Used for |
|---|---|---|
| `RootCertificateName` | `DO_NOT_TRUST_ContosoWebDebuggerRoot` | The root certificate's name; found again by it on later runs |
| `ResolveDataFolder()` | `%LOCALAPPDATA%\Contoso Web Debugger` (macOS: `~/Library/Application Support/...`) | The system-proxy crash-recovery backup, and `PreferenceStore.CreateDefault()` |
| `ProxyHostName` | `contosowebdebugger` | The proxy's own page, `http://contosowebdebugger/` |
| `RootCertificateFileName` | `ContosoWebDebuggerRoot.cer` | The certificate download on that page |
| `ProductName`, `ProductVersion` | as given | Error pages, messages, the HAR creator field |

Override any of them with an object initializer, for example to keep a root
certificate name your app already uses:

```csharp
new ProxyHostIdentity("Contoso Web Debugger") { RootCertificateName = "DO_NOT_TRUST_ContosoRoot" }
```

Without `Configure`, ProxyCore uses VioLens's own identity. Don't ship that:
your app and VioLens would share one root certificate and one crash-recovery
file, and each would think it owned them.

## Logging

ProxyCore is silent unless you give it somewhere to log:

```csharp
ProxyEngine.Configure(identity, log: entry => myLog.Write(entry.Category, entry.Message));
```

Each `ProxyLogEntry` has a `Time`, a `Category` and a `Message`. Categories
are `proxy` (connections and failures), `session` (a line per completed
request), `system-proxy` and `pac`. The handler runs on whatever thread
produced the message, often a connection's, so keep it thread-safe and
quick. If it throws, the message is dropped.

With `Microsoft.Extensions.Logging`:

```csharp
var logger = loggerFactory.CreateLogger("ProxyCore");
ProxyEngine.Configure(identity, log: e => logger.LogInformation("[{Category}] {Message}", e.Category, e.Message));
```

## The root certificate

`new CertificateAuthority()` finds your app's root in the current user's
personal certificate store, or creates one (an ECDSA P-256 key, valid for
five years, never exportable), then makes sure it's trusted:

- Windows: adding it to the current user's trusted roots shows Windows' own
  "Do you want to install this certificate?" prompt. If the user says No,
  the constructor throws.
- macOS: trusting it adds it to the login keychain without any prompt from
  the system, so **ask the user yourself first**. Pass
  `installToTrustStore: false` to get the certificate without touching
  trust, for example to show its details in your confirmation dialog.

`authority.Uninstall()` removes it from both stores, for a "Remove
certificate" menu command or your uninstaller. Create one
`CertificateAuthority` per run and reuse it. Creating the authority is the
step that can prompt; minting the per-site certificates
(`LeafCertificateProvider`) is cheap and cached.

## Seeing traffic

Every finished request and response goes into the `SessionStore` you passed
to the listener, as an immutable `Session`:

```csharp
sessions.SessionAdded += session =>
{
    // session.Id, session.Host, session.Scheme ("https" or "http")
    // session.Request / session.Response: method, target, headers, body bytes
    // session.Timings: DNS, connect, TLS, wait and download times
    // session.Flags: string flags set by your hooks (see below)
    // session.WebSocket: the messages, for an upgraded connection
};
```

Bodies are kept exactly as they came over the wire, so decode one before
reading it:

```csharp
var encoding = ContentDecoder.FindHeader(session.Response.Headers, "Content-Encoding");
if (ContentDecoder.TryDecode(session.Response.Body, encoding, out var bytes, out var error))
{
    var text = ContentDecoder.DecodeText(bytes, ContentDecoder.FindHeader(session.Response.Headers, "Content-Type"));
}
```

`HttpMessageText.Format(session.Request)` gives the raw text of a request or
response, the way a Raw inspector shows it.

`SessionAdded` is raised on the connection's thread. In Windows Forms, hand
sessions to the UI thread in batches, because a busy page can produce
hundreds of them a second.

```csharp
private readonly ConcurrentQueue<Session> _incoming = new();

sessions.SessionAdded += session => _incoming.Enqueue(session);

// A System.Windows.Forms.Timer, every 250 ms, on the UI thread:
private void OnTick(object? sender, EventArgs e)
{
    if (_incoming.IsEmpty) return;
    sessionList.BeginUpdate();
    while (_incoming.TryDequeue(out var session)) AddRow(session);
    sessionList.EndUpdate();
}
```

`SessionStore.Snapshot()` returns everything captured so far. `Remove` and
`Clear` delete sessions and raise `SessionsRemoved`.

## Changing traffic: per-request hooks

Use `IExtensionSessionHost` if your app has its own per-request object, the
way Fiddler has its `Session` (or you have an `Exchange`). ProxyCore calls
`BeginSession` once for each request. You return an `IExtensionSession`, and
ProxyCore calls its methods in order as the request goes out and the
response comes back. That object lives from the first call to the last, so
any state you keep on it (flags, timers and so on) carries through.

```csharp
public sealed class ExchangeHost(IExchangeRules rules) : IExtensionSessionHost
{
    public bool IsActive => true;

    public IExtensionSession BeginSession(int sessionOrdinal, string hostname, string scheme) =>
        new Hooks(new Exchange(sessionOrdinal, hostname, scheme), rules);
}
```

Pass it to the listener:
`StartOnAvailablePort(port, leafs, sessions, sessionHost: exchangeHost)`.

Here are the calls for one request, with their Fiddler equivalents:

| Order | `IExtensionSession` method | Fiddler equivalent | Can change |
|---|---|---|---|
| 1 | `PeekAtRequestHeaders(request)` | `OnPeekAtRequestHeaders` | Nothing: look only (make header edits in step 2) |
| 2 | `RequestBefore(request)` | `OnBeforeRequest`, `AutoTamperRequestBefore` | The request; or answer it locally |
| 3 | `RequestAfter(request)` | `AutoTamperRequestAfter` | Nothing (already sent) |
| 4 | `PeekAtResponseHeaders(request, response)` | `OnPeekAtResponseHeaders` | Nothing: look only (make header edits in step 5) |
| 5 | `ResponseBefore(request, response)` | `OnBeforeResponse`, `AutoTamperResponseBefore` | The response |
| 6 | `ResponseAfter(request, response)` | `AutoTamperResponseAfter` | Nothing (already returned) |
| end | `Flags` | `oSession["..."]` | Stored on the captured `Session` |

To answer a request without contacting the server (Fiddler's
`utilCreateResponseAndBypassServer`), return the response from step 2:

```csharp
public ExtensionRequestResult RequestBefore(CapturedRequest request)
{
    if (request.Target.StartsWith("/hello", StringComparison.Ordinal))
    {
        exchange["ui-comments"] = "Answered locally";
        return new ExtensionRequestResult(request, LocalResponse: TextResponse(200, "OK", "Hello"));
    }

    // oSession.oRequest["X-Debugged-By"] = "Contoso";
    request = request with { Headers = [.. request.Headers, ("X-Debugged-By", "Contoso")] };
    return new ExtensionRequestResult(request);
}
```

The request and response types are immutable records. Change them with
`with` and return the result. Each step gets the current version, which
something earlier (a script, a breakpoint edit) may have changed, so update
your own object from it instead of assuming nothing moved.

Things to know:

- Hooks run on connection threads, several at once, never on your UI
  thread. Keep them thread-safe and fast, because the request waits for
  them. Never block a hook waiting for the UI thread (a `Control.Invoke`,
  say), as that can deadlock. To let the user edit a request, use a
  [breakpoint](#breakpoints).
- `IsActive` has a cost. While it's true, every body is read in full before
  steps 2 and 5, so streamed responses arrive all at once. Return false when
  your app has no rules or extensions loaded.
- Don't let a hook throw. ProxyCore doesn't catch it, and the client's
  connection is dropped. Catch inside each hook, log, and carry on with the
  message unchanged, as the sample does.
- AutoResponder runs first. A request answered by an AutoResponder rule
  never reaches these hooks.

The full order for a request is: AutoResponder, `PeekAtRequestHeaders`, the
`IScriptRunner`'s `OnBeforeRequest`, `IExtensionAutoTamperHost` handlers,
`RequestBefore`, the request breakpoint, then sending. Responses mirror that.

There are two simpler hooks. `IExtensionAutoTamperHost` has the same points
without the per-request object, so each call stands alone. `IScriptRunner`
is for a scripting engine and has only `OnBeforeRequest` and
`OnBeforeResponse`. VioLens's FiddlerScript support (`Violens.Compatibility`)
implements it. You can pass all three to one listener.

The sample's [`Exchanges.cs`](../samples/EmbeddingSample/Exchanges.cs) has a
complete `Exchange` class with Fiddler-style flags, the hooks, and a rules
interface in the shape of CustomRules.js.

## Breakpoints

```csharp
var breakpoints = new BreakpointManager();
breakpoints.Rules.RequestUrlContains = "/api/";    // like QuickExec's "bpu /api/"
breakpoints.Rules.ResponseStatusCodeEquals = 500;  // like "bps 500"

breakpoints.BreakpointHit += pending =>
    uiContext.Post(_ => ShowBreakpointEditor(pending), null);
```

Each rule you set is a separate trigger, and any one of them matching pauses
the message. Pass `breakpointManager: breakpoints` to the listener. When a
rule matches, that request (or response) waits, and `BreakpointHit` gives
you a `PendingBreakpoint`:

- `RawText` is the message as text for an editor. `TryEdit(newText)` checks
  and applies an edit, and if it can't, `ValidationError` says what's wrong.
- `Resume()` lets it go on, edited or not. `Abort()` drops it.

Only that request waits; everything else keeps flowing. `BreakpointHit` is
raised on the connection's thread, which is why the example posts to the UI
thread.

## AutoResponder

`AutoResponderRules` answers matching requests without contacting the server,
using Fiddler's rule syntax:

```csharp
var autoResponder = new AutoResponderRules { IsEnabled = true };
autoResponder.Rules.Add(new AutoResponderRule { MatchPattern = "EXACT:https://example.com/app.js", Action = @"C:\fixes\app.js" });
autoResponder.Rules.Add(new AutoResponderRule { MatchPattern = "regex:(?i)/ads/", Action = "*drop" });
autoResponder.Rules.Add(new AutoResponderRule { MatchPattern = "/slow", Action = "*delay:2000" });
```

Patterns: a plain substring, `EXACT:`, `regex:`, `NOT:` and `METHOD:`.
Actions: a file, a URL to fetch instead, `*redir:`, `*delay:`, `*header:`,
`*flag:`, `*drop`, `*reset`, `*bpu`, `*bpafter`, `*CORSPreflightAllow` and
`*exit`. Pass `autoResponderRules: autoResponder` to the listener. You can
change the rules while it's running.

## The system proxy and upstream proxies

```csharp
SystemProxyController.RecoverFromCrash();                  // at startup, always
var upstream = SystemProxyController.DetectUpstream(listener.Port);
listener.Gateway = upstream.Proxy;                          // forward through the user's own proxy
SystemProxyController.Enable(listener.Port);
// ...
SystemProxyController.Disable();
```

Before changing anything, `Enable` saves the current settings to a backup
file in your data folder. `Disable` puts them back and deletes the backup.
If your app crashes in between, the backup survives and `RecoverFromCrash`
restores it at the next start. On Windows this sets the user's WinINET
proxy (what browsers and most apps use). On macOS it sets each network
service's web proxies.

For corporate networks:

- `listener.Gateway = UpstreamProxy.Parse("proxy.contoso.com:8080")` sends
  everything through a proxy, with its bypass list honored.
- `listener.AutoConfig = ProxyAutoConfig.FromScript(pacText)` (or
  `ProxyAutoConfig.Create(autoDetect: true, scriptUrl, fallback)` for WPAD)
  asks a PAC script which proxy to use for each connection.
- Proxies that ask for a sign-in get Windows' own (Negotiate or NTLM) by
  default. Set `listener.ProxyCredential` for a user name and password.

## Phones and other computers

Pass `allowRemoteClients: true` to the listener to accept connections from
other devices as well as this computer. On the other device, set the proxy
to this computer's address and port, then browse to
`http://contosowebdebugger/` (your `ProxyHostName`). That page offers your
root certificate, with steps for iPhone and Android.

## Sending requests: Composer and Replay

`ComposerClient` sends a request through your own listener, so the result is
captured like any other session:

```csharp
var edited = session.Request with { Target = SessionUrl.Of(session) };   // absolute URL
if (ComposerClient.TryResolve(edited, out var url, out var toSend, out var error))
{
    var response = await ComposerClient.SendAsync(
        listener.Port, leafProvider.IssuerCertificateDer, url!, toSend!, cancellationToken);
}
```

Bodies are sent byte for byte, so binary ones replay exactly.

## Saving and opening captures

```csharp
SazWriter.Write(@"C:\captures\today.saz", sessions.Snapshot());
var result = await SazReader.ImportAsync(@"C:\captures\old.saz", sessions);  // adds to the store
HarWriter.Write(@"C:\captures\today.har", sessions.Snapshot());
await HarReader.ImportAsync(@"C:\captures\from-browser.har", sessions);
```

SAZ files open in Fiddler Classic and VioLens, and Fiddler's SAZ files open
here. HAR files include timings and WebSocket messages. Session flags aren't
written to SAZ files yet.

## Apps on .NET Framework

ProxyCore needs .NET 10, so a .NET Framework app can't load it directly.
What it can do is run the engine in a separate process. **Violens.ProxyHost**
is a small .NET 10 program that hosts ProxyCore, and your app talks to it
through **Violens.ProxyHost.Client**, a .NET Standard 2.0 library (so .NET
Framework 4.6.2 and later) with no other dependencies.

| Part | What it is | Where to get it |
|---|---|---|
| `Violens.ProxyHost.Client` | The library your app references | NuGet |
| `Violens.ProxyHost.exe` | The host, self-contained (no .NET 10 install needed) | `Violens.ProxyHost-<version>-win-x64.zip` on each release; ship it in a folder beside your app |

```csharp
using Violens.ProxyHost.Client;

// Starts the host and connects to it over a private named pipe.
var proxy = await ProxyHostClient.LaunchAsync(
    Path.Combine(Application.StartupPath, @"ProxyHost\Violens.ProxyHost.exe"));

proxy.SessionCaptured += (s, e) => BeginInvoke(new Action(() => AddRow(e.Session)));
proxy.Log += (s, e) => Debug.WriteLine(e);

// Like OnBeforeRequest in CustomRules.js.
proxy.OnBeforeRequest = ctx =>
{
    ctx.Request.SetHeader("X-Debugged-By", "Contoso");
    if (ctx.Host == "example.com" && ctx.Request.Target.StartsWith("/hello"))
    {
        ctx.Flags["ui-comments"] = "Answered locally";
        ctx.RespondWith(ProxyResponse.Text(200, "OK", "Hello from the proxy"));
    }
};

var started = await proxy.StartAsync(
    new AppIdentity("Contoso Web Debugger"),
    new StartOptions { UseSystemProxy = true, HookRequests = true });

// ... later
await proxy.StopAsync();
proxy.Dispose();   // the host puts the system proxy back and exits
```

What the client gives you:

- **Capturing.** `StartAsync` takes `AppIdentity`, which has the same
  settings as `ProxyHostIdentity`, and creates and trusts the root
  certificate (the Windows prompt comes from the host process). It can
  become the system proxy, forwarding through the user's own proxy.
- **Sessions.** `SessionCaptured` delivers each finished request and
  response with its full URL and flags. Bodies are included unless you turn
  them off with `StartOptions.IncludeBodies` to save memory.
- **Hooks.** `OnBeforeRequest` and `OnBeforeResponse` see each request and
  response. They can change it in place (`SetHeader`, `RemoveHeader`,
  `SetBodyText`), set flags, or answer the request locally (`RespondWith`).
  Turn them on with `StartOptions.HookRequests` and `HookResponses`.
- **Breakpoints.** `SetBreakpointsAsync` sets the same rules as QuickExec's
  `bpu`, `bpafter`, `bps` and `bpm`. `BreakpointHit` gives you the paused
  message's raw text. Call `Resume(editedText)` or `Abort()` when the user
  is done.
- **Files.** `SaveSazAsync`, `LoadSazAsync` and `ClearSessionsAsync`.
- **The certificate.** `StartResult.RootCertificate` lets you show or
  export it, and `UninstallRootCertificateAsync` removes it.

Things to know:

- Hooks cross a process boundary. Each one is a round trip over the pipe,
  which is quick on one machine but not free, so only turn hooks on while
  there are rules to run. If a hook doesn't answer within
  `StartOptions.HookTimeout` (10 seconds by default), it's skipped, the
  message goes on unchanged, and the host logs why.
- Events arrive in order on the client's own reader thread. Hand them to the
  UI thread with `BeginInvoke`, never `Invoke`. Hooks run on thread-pool
  threads, several at once, so don't make them wait for the UI.
- The host stops capturing, puts the system proxy back and exits when you
  dispose the client, when the pipe breaks, or when your process ends, even
  if it crashes. If the host itself crashes while it's the system proxy,
  the next `StartAsync` restores the user's settings.
- Only your app can talk to the host. `LaunchAsync` gives it a random pipe
  name and a one-time token, and refuses a pipe that answers without that
  token.

The other route is to move your app itself to .NET 10. Windows Forms and
WPF are fully supported there. What usually holds a Fiddler-style app back
is everything else: JScript.NET (for FiddlerScript) doesn't exist on .NET
10, runtime compilation through CodeDOM isn't supported, and extensions
built for .NET Framework may not load. VioLens gets around this by
translating FiddlerScript to run on a JavaScript engine
(`Violens.Compatibility`) and running old extensions in a separate .NET
Framework process.

## Versions and compatibility

- The package version is the VioLens version it shipped with.
- In 0.x, a minor version (0.2 to 0.3) may change the public API, and a
  patch version (0.2.0 to 0.2.1) only fixes things. Each release's notes
  have a "ProxyCore changes" section.
- From 1.0, only a major version (1.x to 2.0) breaks anything.
- Pin a version and move up when you're ready. NuGet never upgrades on its
  own, and a published version never changes.
- Expect HTTP/2 to change how requests are represented before 1.0.

ProxyCore is MIT licensed, like the rest of VioLens. If you have questions
or hit a problem,
[open an issue](https://github.com/MarkSPowell/VioLens/issues).
