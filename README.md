# VioLens

An open source HTTP(S) inspection tool for developers — a spiritual
successor to Fiddler Web Debugger (Fiddler Classic), whose licence
Progress Software changed in 2026 to allow non-commercial use only.
VioLens is free for any use, under the MIT licence. It's an independent
project, not affiliated with Progress or Telerik (see
[Relationship to Fiddler](#relationship-to-fiddler)).

VioLens was called CLeARINET until version 0.2. Settings, rules and
extensions from a CLeARINET install carry over the first time VioLens
runs (see the User Guide's "Upgrading from CLeARINET").

![VioLens capturing a request to example.com, then showing its headers, the Composer and the Timing tab](https://violens.dev/assets/demo.gif)

Downloads, the User Guide and more are at [violens.dev](https://violens.dev). Questions
and ideas are welcome on [Discord](https://discord.gg/NJfnGnn4PT); bugs go in
[issues](https://github.com/MarkSPowell/VioLens/issues).

## Status

Early, working MVP. Not yet 1.0 — expect rough edges, and see
[Known limitations](#known-limitations) below for what's deliberately not
here yet. Feedback and issues are welcome.

## What it does today

- Captures plain `http://` and HTTPS traffic through a local proxy,
  decrypting HTTPS with a per-install root certificate installed into the
  current user's trusted root store — the same mechanism Fiddler Classic
  uses. **Tools > Remove Root Certificate** (and the Windows uninstaller)
  removes it again. Hosts that pin their certificates can be left
  undecrypted (**Tools > Connections**).
- Long captures: a **Keep** setting holds only the newest sessions, and
  **Help > Copy Diagnostic Info** gathers what a bug report needs.
- Registers itself as the system proxy while running — WinINET on
  Windows, per network service via `networksetup` on macOS — and recovers
  cleanly if a previous run didn't shut down properly (crash, kill,
  unclean shutdown).
- A session list with live filtering: free-text search across the URL
  and headers (never body content — see the filter grammar's own remarks
  on why), plus `method:`, `host:`, and `status:` query tokens (exact,
  class like `4xx`, comparison, and range forms). Edit > Find Sessions
  (Ctrl+F) searches bodies too, decompressed, and highlights matches.
- Timing like browsers' developer tools: a waterfall column with each
  session's phases (DNS, connection, TLS, waiting, download), a Timing tab
  for the selected session, a request summary, and a Statistics tab
  (totals, by type, status and host, slowest requests, easy wins). Timings
  are kept in SAZ and HAR files, and read from browsers' HAR exports.
- HTTP/2 (on by default; **Tools > Connections** turns it off): browsers use
  HTTP/2 with VioLens for HTTPS sites, and VioLens uses it with sites that
  offer it, falling back to HTTP/1.1 for those that don't. Requests share
  one connection per site, trailers (gRPC's `grpc-status`) come through,
  and a Version column shows which version each side used. The Composer
  can send HTTP/2 too.
- A WebSocket tab showing each message sent and received on a WebSocket
  connection, live, with compressed and fragmented messages put back
  together; kept in HAR files.
- QuickExec, Fiddler's command box under the session list (Alt+Q):
  `?text`, `@host`, `=404`, `>40k`, `select`, `allbut`, `cls`, `tail`, the
  `bpu`/`bpafter`/`bps`/`bpm` breakpoints and `go`; other commands go to
  FiddlerScript's `OnExecAction` and extensions.
- Compare Sessions (Ctrl+W): two sessions' requests and responses side by
  side, with each difference highlighted, bodies decompressed and JSON
  formatted so changes line up.
- TextWizard (Ctrl+E): Base64, URL, HTML, hex and JavaScript encoding and
  decoding, JWT decoding, JSON formatting, Unix times, hashes, and SAML's
  deflate+Base64.
- A column chooser: right-click a column header to show or hide session-list
  columns (Waterfall, Image Bloat, script and extension columns...). Which
  columns are shown, their order and their widths are remembered.
- Request/response inspector tabs: Headers, Raw (decoded text, with
  automatic decompression), JSON (pretty-printed), WebForms (query string
  and form fields, including multipart uploads), ImageView (the picture,
  its format and pixel size, and any image bloat), Hex, Cookies (each cookie sent or set, with
  its attributes, and a warning for ones browsers will refuse) and Notes
  (flags an importer or extension attached to a session).
- Image bloat detection: an Image Bloat column showing how much of each
  PNG, JPEG, GIF or WebP is metadata, comments or other bytes that don't
  change how it looks, with heavily bloated images highlighted.
- A Composer tab for writing or editing a request and sending it, and
  Replay (**R**) to send a captured request again. Both go through
  VioLens itself, so the result is captured like any other traffic.
- Forwarding through the network's own proxy: VioLens picks up the
  proxy the computer used before it started, including an automatic
  proxy configuration script (PAC) or "Automatically detect settings"
  (WPAD), or uses one you set under **Tools > Connections**. A proxy
  that asks for a sign-in gets one: as you (Windows sign-in, Negotiate or
  NTLM), or with a user name and password entered there.
- Capturing phones and other computers: "Allow remote computers to
  connect", and a page at `http://violens/` for installing VioLens's
  certificate on the other device.
- Rows in the session list can be coloured, bold, italic, struck through
  or hidden by an extension or importer (Fiddler's `ui-backcolor` and
  related session flags), and sessions can be removed from the list
  (**Edit > Remove Selected Session / Remove All Sessions**).
- Automatic response decompression covering gzip, deflate (both the
  common zlib-wrapped form and the older raw form some servers still
  send), zstd, and chains of those applied together. bzip2 and the
  historical `compress` encoding are deliberately not supported — neither
  ever saw real use as a web encoding. SDCH is recognized in headers but
  not decoded, since that needs a dictionary exchange this build doesn't
  track. Content compressed with Zopfli needs no special handling — it's
  a slower, better gzip encoder that still produces standard gzip output.
- Fiddler-Classic-style breakpoints: break on all requests and/or all
  responses, or on narrower conditions (URL contains, method, status
  code); inspect and edit a paused message's raw text, then Resume or
  Abort. The breakpoints panel only takes up space once it's actually
  needed.
- AutoResponder: an ordered list of match/action rules (substring,
  wildcard, `EXACT:`/`regex:`/`NOT:`/`METHOD:` matches; redirect, delay,
  header, reset, drop, serve-a-file, and other actions) that can answer a
  matching request without it ever reaching the real server.
- FiddlerScript compatibility: load a `CustomRules.js`-shaped script and
  have it hook `OnBeforeRequest`/`OnBeforeResponse`, declare Rules-menu
  options and string choices, add its own buttons and right-click context
  actions, and add its own columns to the session grid — see the
  FiddlerScript Compatibility Design doc for the full scope and cuts.
- Compiled .NET extension compatibility: drop a compiled extension `.dll`
  (the successor to Fiddler Classic's own extension model) into
  VioLens's Extensions folder and it's loaded automatically at
  startup — AutoTamper hooks, request/response inspectors, and session
  import/export are all supported; see the .NET Extension Compatibility
  Design doc.
- A Fiddler-shaped compatibility layer (`Violens.CompatShim`) for
  porting real Fiddler Classic extensions from their source, on both
  Windows and macOS. Ported extensions can import sessions, work on live
  traffic (one session object per request, and answering requests
  themselves), and add their own menus, tabs, session-list columns and
  row colours. Working so far:
  - **NetLog importer** (Eric Lawrence's): builds with only its
    `using Fiddler;` lines removed.
  - **CSP Rule Collector**: a VioLens-only fork,
    [CSP-CLeARINET-Extension](https://github.com/MarkSPowell/CSP-CLeARINET-Extension),
    with its tab rewritten in Avalonia.
  - **Privacy Scanner** (cookies/P3P): ported as a test of the UI hooks;
    cookie viewing itself is built in (the Cookies tab).

  All three come with the installers as optional extensions, each with
  its own licence: the Windows installer offers them as ticked boxes
  (Privacy Scanner off by default), and the macOS `.dmg` has an
  **Optional Extensions** folder to copy them from. See the User Guide.

  CI builds and tests all three on both platforms. See
  `tests/ExtensionPorts/README.md` and the Extension Test Targets doc.
- Optional legacy extension host: a separate, opt-in tool
  (`tools/Violens.LegacyExtensionHost`) that runs real, unmodified-source
  Fiddler Classic extensions against real proxied traffic, bridged to the
  main app over a local IPC connection, for extensions that reach into UI
  surface removed from modern .NET — not part of the main app's own build,
  but available as an unchecked-by-default optional component in the
  Windows installer; see that tool's own README and the .NET Extension
  Compatibility Design doc.
- A **Tools** menu of checkable toggles that show or hide the
  FiddlerScript, Extensions, breakpoint-condition, and AutoResponder
  panels on the main screen, so only what you're actually using takes up
  space.
- Export captured sessions to a `.saz` (Session Archive Zip) file, or
  import a previously saved one back in; export and import HTTP Archive
  (`.har`) files, the format browsers' developer tools use.
- An automatic or manually-specified listening port (defaults to Auto).
- Settings that persist between runs (port choice, Tools-menu panels,
  filter text and more), stored the same way on Windows and macOS in
  `preferences.json` under `%LOCALAPPDATA%\VioLens\` or
  `~/Library/Application Support/VioLens/`. Unknown keys are kept, a
  corrupt file is set aside rather than overwritten, and two running
  copies don't clobber each other's changes. See the Preferences Design
  doc.
- **Help > Documentation** in the running app opens the
  [User Guide](docs/User%20Guide.md) — see [Documentation](#documentation)
  below.

## Getting started

Requires the .NET 10 SDK.

```
dotnet restore VioLens.sln
dotnet build VioLens.sln
dotnet test VioLens.sln
dotnet run --project apps/Violens.DesktopUi
```

The first time you click Start, you'll be asked to trust a new local root
certificate (named `DO_NOT_TRUST_ViolensRoot...`, following Fiddler
Classic's own naming convention for the same purpose) — this is what lets
VioLens see inside HTTPS traffic on this machine. On Windows that's the
OS's own native prompt; on macOS, where the `security` CLI VioLens uses
has no equivalent OS-level prompt, it's a confirmation dialog VioLens
shows itself before ever touching your login keychain. Nothing captured
ever leaves the device on its own.

## Documentation

- **Using the app?** Start with the [User Guide](docs/User%20Guide.md) —
  starting/stopping the proxy, the session grid and filtering, breakpoints,
  AutoResponder, FiddlerScript, extensions, and SAZ import/export. It's
  also one click away from inside the running app, via **Help >
  Documentation**.
- **Building your own tool on the proxy engine?** Read
  [Embedding ProxyCore](docs/Embedding%20ProxyCore.md). The engine is
  published on NuGet as `Violens.ProxyCore`, with the same version as each
  VioLens release.
- **Contributing or curious how something works?** The rest of `docs/` is
  written for that: design rationale, scope cuts, and open follow-ups for
  each area of the app (see [Project layout](#project-layout) below for
  what's there).

## Project layout

- `docs/` — the [User Guide](docs/User%20Guide.md) for using the app, plus
  design docs: the project
  plan and tenets, the interception certificate design, the preferences
  design, the FiddlerScript
  compatibility design, the .NET extension compatibility design, the
  Fiddler feature/tier inventory, and breakpoints research notes.
- `src/Violens.ProxyCore` — the proxy engine, also published as a NuGet
  package for other apps: the listener, HTTP message parsing, sessions,
  breakpoints, AutoResponder, certificates, SAZ and HAR, system-proxy
  registration on both Windows and macOS, and the preferences store. Its
  public types are what other apps build on, so anything else is
  `internal` (VioLens's own assemblies are listed in its `AssemblyInfo.cs`).
- `src/Violens.ProxyHost` and `src/Violens.ProxyHost.Client` — the engine
  for apps on .NET Framework: a small .NET 10 program that hosts
  ProxyCore, and a .NET Standard 2.0 library an app uses to run it and
  talk to it over a named pipe (sessions, hooks, breakpoints, SAZ). See
  the Embedding ProxyCore guide's "Apps on .NET Framework".
- `src/Violens.AppCore` — VioLens's own app logic that isn't part of the
  engine: Compare, TextWizard's conversions, QuickExec, the session-list
  column layout, and the carry-over from the app's former name.
- `src/Violens.Extensibility` — the inspector contract and the built-in
  inspectors (Headers, Raw, Hex, Cookies, Notes).
- `src/Violens.Compatibility` — FiddlerScript compatibility
  (`FiddlerScriptRunner`, the `Exchange`/`AppObject` shim, the directive
  scanner behind the Rules menu/Script Actions/custom columns) and
  compiled .NET extension compatibility (`ExtensionHost` and the
  `IFiddlerExtension`-family interfaces), and the Fiddler-shaped
  `Violens.CompatShim` layer for extensions ported from source — see both
  design docs above for what's built and what's still scoped out.
- `apps/Violens.DesktopUi` — the Avalonia desktop UI.
- `samples/EmbeddingSample` — the code from the Embedding ProxyCore guide,
  as a console app that builds with everything else (using only
  ProxyCore's public API), so the guide can't drift from the real thing.
- `tools/Violens.DevHost` — a console host used during early
  development of the proxy core.
- `tools/Violens.SampleExtension` — a small, separately-compiled .NET
  extension used to validate `ExtensionHost` against a real `.dll` rather
  than only in-process fakes; see its own README for what it demonstrates
  and how to build it.
- `tools/Violens.LegacyExtensionHost` — the optional, separate legacy
  extension host: a `net48` process that can run real, unmodified-source
  Fiddler Classic extensions, bridged to the main app's own proxied
  traffic. Deliberately kept out of `VioLens.sln`/the main app's build
  entirely, so nothing about it can affect either — see its own README and
  the .NET Extension Compatibility Design doc.
- `tests/` — unit tests for the projects above, plus `tests/ExtensionPorts`:
  real Fiddler Classic extensions fetched at pinned commits, ported, and
  tested (not part of `VioLens.sln`; see its README).

## Known limitations

Listed here on purpose, rather than left for someone to discover the hard
way:

- **macOS has had less real-world use than Windows.**
  - Certificate trust uses the `security` tool, into your login keychain,
    after VioLens's own confirmation dialog, since macOS doesn't prompt.
  - The system proxy is set with `networksetup`, for each network service.
  - CI checks the macOS build's signature and that it launches, but its
    tests don't run `security` or `networksetup` for real.
  - A full hands-on test is still to come: trust and removal, whether
    `networksetup` needs admin rights, recovery after a crash, and the
    notarized install. See the Interception Certificate Design's
    "Platform status".
  - Release builds of the `.dmg` are Developer ID signed and notarized
    when the repository's signing secrets are set. Builds without them
    (forks, test builds) are ad-hoc signed, and macOS asks you to allow
    them once, in System Settings > Privacy & Security > Open Anyway.
- **No Preferences/`about:config` editor yet.** Settings, the window's
  size and position, and AutoResponder rules are remembered between runs,
  but changed only through the app's own controls. Breakpoints and the
  AutoResponder's on/off switch deliberately always start off. See the
  Preferences Design doc.
- **No built-in Chromium NetLog import.** NetLog import works through
  Eric Lawrence's ported NetLog importer extension (see above), an
  optional extension in the installers.
- **HTTP/2 has had real-world use on Windows only so far.** WebSockets
  stay on HTTP/1.1 connections (browsers arrange that themselves), and
  HTTP/3 isn't supported: browsers don't send it through a proxy, so they
  use HTTP/2 instead.
- **Upstream proxies: plain HTTP proxies only.** A SOCKS proxy can't be
  used yet (a PAC script's SOCKS entries are skipped). Proxy sign-in
  supports Negotiate (Kerberos), NTLM and Basic, not Digest. Automatic proxy detection on macOS looks only in
  DNS (`wpad.<your domain>`), not DHCP.
- **The main app's own compiled-extension support is source-level, not
  binary.** An already-compiled Fiddler Classic extension `.dll` can't be
  dropped straight into VioLens's own Extensions folder as-is; a new
  extension has to be built against VioLens's own (source-level
  compatible) interfaces. Real binary compatibility does exist, just not
  here — see `tools/Violens.LegacyExtensionHost`, the separate, optional
  legacy extension host above, which runs real unmodified-*source*
  extensions (recompiled against its own compat assembly) against real
  proxied traffic; it's not part of the main app's own build, but the
  Windows installer can include it as an unchecked-by-default optional
  component.
- **One client certificate (mutual TLS) at a time.** Chosen under
  **Tools > Connections**, it's sent to every server that asks, or only to
  the hosts you list. Fiddler Classic's
  `ClientCertificate.cer` file and the `https-Client-Certificate` script
  flag aren't supported.
- **No headless/CLI mode.** Left out of this milestone by design, not by
  oversight.
- **Bodies are kept in memory.** Each is capped at 64 MB, and the **Keep**
  setting limits how many sessions are held, but large bodies aren't
  spilled to disk.

How the root certificate, the listener and the proxy host are protected,
and what isn't covered yet, is in [SECURITY.md](SECURITY.md).

## Relationship to Fiddler

VioLens is an independent open source project. It isn't affiliated
with, endorsed by or sponsored by Progress Software Corporation or
Telerik. Fiddler, Fiddler Classic, Fiddler Everywhere, FiddlerCore and
Telerik are trademarks of Progress Software Corporation; VioLens uses
those names only to describe what it's compatible with, and never
presents itself, or anything it ships, as Fiddler.

VioLens is built independently: from publicly available documentation,
publicly observable behavior (such as the structure of `.saz` files
produced by the real tool), and original design decisions — never from
decompiled or leaked Fiddler source, and with no code from Fiddler. See
[CONTRIBUTING.md](CONTRIBUTING.md) for the full clean-room policy that
governs every contribution here. The optional extensions included with
the installers are separate works by their authors, under their own
licences (see `THIRD-PARTY-NOTICES.txt` alongside them).

VioLens is inspired by Eric Lawrence's public writing about Fiddler,
including his post [Fiddler in 2026](https://textslashplain.com/2026/08/05/fiddler-in-2026/),
and by his public [Clearinet](https://github.com/ericlaw1979/Clearinet)
repository. It isn't his project, and he hasn't contributed code to it.
It does build on some of his published open source work, under its own
licences: the ported NetLog importer and the CSP Rule Collector fork
(see above).

## Credits

Special thanks to:

- **EJ Suter-Powell** and **Jose Gonzales**, for testing VioLens on
  macOS.
- **Eric Lawrence**, who created Fiddler, for the inspiration and for
  laying the groundwork: two decades of Fiddler itself, and his
  generous public writing about how it works and what he'd do
  differently, which shaped much of VioLens's design. (Thanks here
  doesn't mean he endorses VioLens; see
  [Relationship to Fiddler](#relationship-to-fiddler).)

## License

MIT — see [LICENSE](LICENSE).
