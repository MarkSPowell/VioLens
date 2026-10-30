# Fiddler Feature Inventory

Referenced from `IInspector.cs` and `InspectorRegistry.cs` as the place
that tiers Fiddler Classic's features against VioLens's own roadmap
(MVP / Beta / 1.0 / out of scope) — but, like the other docs in this
folder, it was never actually written down anywhere accessible. This is
a genuinely thin reconstruction: only two rows are grounded in an actual
code reference. Everything else below the line is scaffolding for you to
fill in, not a real inventory yet — treat the table structure as the
useful part of this file for now, not its contents.

## Grounded (directly referenced in code)

| Feature | Tier | Source |
|---|---|---|
| Custom inspectors (third-party inspector plugins) | Beta | `IInspector.cs`, `InspectorRegistry.cs` |
| Extension loading (Inspectors-folder assembly loading into isolated `AssemblyLoadContext`s) | Beta | `InspectorRegistry.cs` |

## Shipped today (inferred from what's actually implemented, not from a tiering decision)

These aren't "tiered" anywhere — they're just what exists right now, listed
here so the table has a real MVP column to compare against once the rest
of this doc is filled in.

| Feature | Status |
|---|---|
| HTTPS interception (proxy + per-install root CA + per-host leaf certs) | Shipped on Windows; also built for macOS (`MacOSCertificateTrust`, via the `security` CLI) but unverified against a real Mac -- see the Interception Certificate Design doc's "Platform status" section |
| Session capture and live list | Shipped |
| Breakpoints: break on all requests / break on all responses | Shipped (UI-exposed) |
| Breakpoints: URL-contains, method-equals, status-equals rules (Fiddler's `bpu`/`bpm`/`bps`) | Shipped (Tools > Also Break On) |
| SAZ export | Shipped |
| SAZ import | Shipped |
| Headers / Raw / Hex inspectors | Shipped |
| JSON / WebForms / ImageView inspectors | Shipped (JSON as indented text, not a tree) |
| Image bloat detection (ImageBloat extension) | Built in: Image Bloat column, row tint, ImageView breakdown; no in-page overlay |
| WebSocket message viewer | Shipped: WebSocket tab with each message (direction, time, type, size, data), live; fragmented messages joined, permessage-deflate decompressed (with and without context takeover); full message view with JSON indented and binary as hex; Find searches text messages; HAR `_webSocketMessages` export and import. Not yet: SAZ (Fiddler stores messages in its own per-session files), editing or breakpoints on messages |
| Compare Sessions (Fiddler Classic launched an external diff tool) | Shipped, built in: side by side, line by line with the differing part highlighted; bodies decompressed, JSON and forms one value per line, optional header-order-insensitive compare; F8/Shift+F8 between differences |
| TextWizard | Shipped: Base64 (incl. URL-safe), URL, HTML, hex, JavaScript escapes, JWT decode, JSON format, Unix time, hashes, SAML deflate/inflate; UTF-8, Latin-1 or UTF-16; live as you type, prefilled from selected text |
| Composer and Reissue (Replay) | Shipped: raw-request Composer tab and Edit > Replay (R); http:// and https://, sent through VioLens so the result is captured; Replay is byte-exact, and the Composer's body can be text, hex (exact bytes) or a file |
| QuickExec | Shipped: `?` `@` `=` `>` `<` selections, `select`, `allbut`/`keeponly`, `cls`, `tail`, `bpu`/`bpafter`/`bps`/`bpm`/`bpv`, `go`, `start`, `stop`, `dump`, `help`, plus VioLens's `save`, `open`, `filter` and `keep`; others go to FiddlerScript `OnExecAction`, then `IHandleExecAction` extensions. `violens-exec` sends commands from scripts, as Fiddler's ExecAction.exe did. Not built in: `bold`, `prefs`, `urlreplace`, `!dns` and other less common commands (a script's OnExecAction can add them) |
| Client certificates (mutual TLS) | Partly: a server asking for a client certificate is recorded (Timing tab, `x-client-certificate-requested` note, explanation on the 502), with the issuers it accepts, plus the server TLS version, cipher and certificate. Shipped: sending one client certificate from the personal store, to every server that asks or to chosen hosts (Tools > Connections; `x-client-certificate-sent` note). Not yet: Fiddler's ClientCertificate.cer file and `https-Client-Certificate` flag. Skipping decryption for chosen hosts is shipped (Tools > Connections, `x-no-decrypt` on the CONNECT session) |
| Upstream gateway (chain to the system proxy) | Shipped: the proxy set before Start, or one set under Tools > Connections; signs in to a proxy that asks (Negotiate/Kerberos, NTLM, Basic); no SOCKS yet |
| Automatic proxy configuration (PAC script, WPAD) | Shipped: the system's setup script or automatic detection, run per request with the standard PAC functions (Jint), falling back to the next proxy listed or the manual proxy; turned off while capturing and restored on Stop |
| Allow remote computers to connect, certificate download page | Shipped: Tools > Connections, and http://violens/ on the other device |
| HAR export | Shipped |
| Response decompression (gzip, deflate, zstd, chains) | Shipped |
| bzip2 / `compress` encodings | Deliberately not supported |
| SDCH | Recognized, not decoded |
| Session filtering (DevTools-style `method:`/`host:`/`status:` query grammar) | Shipped — new relative to Fiddler Classic, per tenet 3 |
| Session list column chooser (show/hide columns) | Shipped: right-click a header or Tools > Session List Columns; covers built-in, script and extension columns; visibility, order and widths remembered; Reset Columns |
| Find Sessions (Ctrl+F) | Shipped: text or regex, requests/responses, headers/bodies (decompressed), match case; highlights matches, F3 for next |
| Statistics and Timeline (per-phase timings) | Shipped, DevTools-style: waterfall column, Timing tab, request summary, Statistics tab; timings in SAZ SessionTimers and HAR |
| System proxy auto-registration with crash recovery | Shipped on Windows (WinINET) and macOS (`networksetup`, per network service). macOS has only been partly tested on a real Mac; the full hands-on test (including whether `networksetup` needs admin rights) is item 3 of the Project Plan's Road to 1.0 |
| FiddlerScript / rules execution | Shipped for `CustomRules.js`: run by Jint after a JScript.NET-to-JavaScript preprocessing pass, against live traffic (`OnBeforeRequest`, `OnBeforeResponse`, `OnExecAction` and the rest), loaded and reloaded from the FiddlerScript panel. Script attributes become Rules-menu options (`[RulesOption]`, `[RulesString]`, `[BindPref]`), context and Tools actions (`[ContextAction]`, `[ToolsAction]`) and session-list columns (`[BindUIColumn]`). The FiddlerScript Compatibility Design lists what's left out, such as nested Rules submenus. Not yet: `CustomRules.cs` (Roslyn) |
| Compiled .NET extension compatibility (`IFiddlerExtension`/`IAutoTamper`/`IAutoTamper2`/`IAutoTamper3`/`IHandleExecAction`/`Inspector2`/`ISessionImporter`/`ISessionExporter`) | Shipped at source level: an extension recompiled against `Violens.CompatShim` loads from the Extensions folder into its own `AssemblyLoadContext`, gated by `RequiredVersion`. Auto-tampering, inspectors, import/export (with a format picker) and exec actions are wired in, and the Extensions panel shows what loaded. Three real extensions (the NetLog importer, the Privacy Scanner and the CSP Rule Collector) are ported, tested in CI and offered by the installers; an extension with a WinForms UI gets a VioLens-only fork. Extensions that need WinForms types missing from .NET 5+ can run out of process in the optional legacy extension host. An already-compiled Fiddler Classic `.dll` doesn't load unmodified (tenet 5; see the .NET Extension Compatibility Design) |
| HAR import | Shipped |
| Plain HTTP (non-TLS) capture | Shipped: captured and forwarded like HTTPS (directly or through the upstream proxy); an unreachable server gets a 502 Bad Gateway session |
| Chromium Netlog import | Not built in; available through the ported NetLog importer extension, an optional extension in the installers |
| Traffic replay / Autorespond | Shipped — full Fiddler Classic AutoResponder syntax (`EXACT:`/`regex:`/`NOT:`/`METHOD:` matches; `*redir:`/`*delay:`/`*header:`/`*flag:`/`*reset`/`*drop`/`*CORSPreflightAllow`/`*exit`/`*bpu`/`*bpafter` actions; serve-file and fetch-URL actions), see `AutoResponderRules` |
| HTTP/2 | Shipped, on by default: browser↔VioLens and VioLens↔server over TLS, with fallback to HTTP/1.1, trailers, a Version column, SAZ/HAR and the Composer |
| Explicit TLS 1.3 handling | Not shipped |
| Headless / CLI mode | Out of scope for the MVP by design |

> **Needs your input:** the actual point of this document — going
> feature-by-feature through what Fiddler Classic offers (its menus,
> Inspectors, Rules, AutoResponder, the QuickExec command set, Composer,
> Statistics) and assigning each one a tier. That's knowledge only you
> have; I only know what's already been built or explicitly discussed in
> this conversation. Once this is filled in, the "Custom inspectors" and
> "Extension loading" rows above should move down into wherever they
> naturally sort in the fuller table, rather than staying a special
> two-row section.
