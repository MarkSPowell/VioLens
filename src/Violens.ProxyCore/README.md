# Violens.ProxyCore

The HTTP(S) debugging proxy engine behind [VioLens](https://violens.dev), for
building your own debugger, capture tool or test harness in .NET.

- Decrypts HTTPS with a root certificate it creates and trusts for the
  current user (Windows, macOS, and Linux browsers through `certutil`).
- Records every request and response, WebSocket messages included, with
  DevTools-style timings.
- Per-request hooks in the shape of Fiddler's (`OnBeforeRequest`,
  `AutoTamperResponseBefore`, session flags), breakpoints and AutoResponder
  rules.
- Becomes the system proxy and restores it afterwards, even after a crash;
  forwards through upstream proxies, PAC scripts and proxy sign-in.
- Reads and writes SAZ and HAR files.

```csharp
ProxyEngine.Configure(new ProxyHostIdentity("My Debugger"));
var authority = new CertificateAuthority();
var sessions = new SessionStore();
sessions.SessionAdded += s => Console.WriteLine($"#{s.Id} {s.Response.StatusCode} {SessionUrl.Of(s)}");
var listener = InterceptingProxyListener.StartOnAvailablePort(
    8888, new LeafCertificateProvider(authority.RootCertificate), sessions);
```

Requires .NET 10. Read the
[Embedding ProxyCore guide](https://github.com/MarkSPowell/VioLens/blob/main/docs/Embedding%20ProxyCore.md)
for the whole API, with a working sample. MIT licensed.
