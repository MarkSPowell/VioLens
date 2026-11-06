# VioLens

VioLens is an open source tool for inspecting HTTP and HTTPS traffic,
built as a successor to Fiddler Web Debugger (Fiddler Classic). Progress
Software changed Fiddler Classic's license in 2026 to allow only
non-commercial use. VioLens is free for any use, under the MIT license.
It's an independent project, not affiliated with Progress or Telerik (see
[Relationship to Fiddler](#relationship-to-fiddler)).

VioLens was called CLeARINET until version 0.2. It isn't Eric Lawrence's
Clearinet, a separate project (see [Clearinet](#clearinet)). Settings,
rules and extensions from a CLeARINET install carry over the first time
VioLens runs (see the User Guide's "Upgrading from CLeARINET").

![VioLens capturing a request to example.com, then showing its headers, the Composer and the Timing tab](https://violens.dev/assets/demo.gif)

Downloads, the User Guide and more are at [violens.dev](https://violens.dev).
Questions and ideas are welcome on [Discord](https://discord.gg/NJfnGnn4PT);
bugs go in [issues](https://github.com/MarkSPowell/VioLens/issues).

## Status

1.0 release candidate. The features planned for 1.0 are in, and the proxy
engine's public API is frozen. What's left before 1.0 itself is hands-on
testing on macOS and on KDE Plasma (see
[Known limitations](#known-limitations)). Feedback and issues are welcome.

## What it does

**Capturing**
- Captures `http://` and HTTPS traffic through a local proxy. HTTPS is
  decrypted with a root certificate made for your install and trusted for
  your account, as Fiddler Classic does it. **Tools > Remove Root
  Certificate** (and the Windows uninstaller) takes it away again. Hosts
  that pin their certificates can be left encrypted (**Tools >
  Connections**).
- Runs on Windows, macOS and Linux (x86-64, as an AppImage or a
  `.tar.gz`).
- Becomes the system proxy while it runs: WinINET on Windows,
  `networksetup` on macOS, and GNOME's or KDE Plasma's settings on Linux.
  If a previous run crashed or was killed, the next one puts your proxy
  settings back.
- HTTP/2, on by default: browsers use it with VioLens for HTTPS sites, and
  VioLens uses it with sites that offer it, falling back to HTTP/1.1. A
  Version column shows what each side used, and gRPC's trailers come
  through.
- WebSocket messages, live, in their own tab, with compressed and
  fragmented messages put back together.
- Forwards through your network's proxy, including PAC scripts and
  automatic detection (WPAD), and signs in to it as you (Negotiate or
  NTLM) or with a user name and password.
- Phones and other computers can use it as their proxy (**Allow remote
  computers to connect**), with a page at `http://violens/` for installing
  the certificate on them.

**Looking at what was captured**
- A session list with live filtering by text, `method:`, `host:` and
  `status:`, and **Edit > Find Sessions** (Ctrl+F) for searching bodies.
  Right-click a column header to find in that column, highlight repeated
  values, or choose and arrange columns.
- Timing like browsers' developer tools: a waterfall column, a Timing tab,
  and a Statistics tab with totals, the slowest requests and easy wins.
- Inspector tabs: Headers, Raw, JSON, Tree (JSON, CBOR or MessagePack as a
  tree), Markdown, WebView (an HTML page's text, and a script-free preview
  in your browser), WebForms, ImageView (with zoom, and a breakdown of any
  image bloat), Hex, Cookies (with sizes, and warnings for cookies browsers
  will refuse) and Notes.
- Decompresses gzip, deflate, Brotli and zstd, alone or chained.
- Compare Sessions (Ctrl+W) shows two sessions side by side, with each
  difference highlighted.
- TextWizard (Ctrl+E) encodes and decodes Base64, URLs, HTML, hex and
  JavaScript escapes, and decodes JWTs, Unix times and SAML.

**Changing traffic**
- Breakpoints on requests, responses, or narrower conditions (URL, method,
  status code): edit the paused message, then resume or abort it.
- AutoResponder rules that answer matching requests without reaching the
  server: a file, a redirect, a delay, a dropped connection and more.
- The Composer, for writing or editing a request and sending it, and
  Replay (**R**) for sending a captured one again.

**Automating it**
- QuickExec (Alt+Q), Fiddler's command box, with suggestions as you type.
  `violens-exec` sends the same commands from scripts, like Fiddler's
  ExecAction.exe.
- FiddlerScript: a `CustomRules.js` script can hook requests and
  responses, and add Rules-menu options, buttons, context-menu actions and
  columns. See the FiddlerScript Compatibility Design.
- Extensions: a compiled `.dll` in the Extensions folder can add hooks,
  inspectors, and importers and exporters. Fiddler Classic extensions can
  be ported from source with few changes (`Violens.CompatShim`); the NetLog
  importer, the CSP Rule Collector and the Privacy Scanner are ported, and
  the installers include them as optional extensions. An optional legacy
  extension host on Windows runs extensions that need parts of WinForms
  .NET no longer has.

**Files and settings**
- Saves and opens SAZ files, saves and imports HAR, and imports the plain
  HTTP in packet captures (`.pcap`, `.pcapng`).
- Remembers its settings in a `preferences.json` that's the same on every
  platform. Administrators can lock settings or set defaults for everyone
  on a machine: with Group Policy or Intune on Windows, using the ADMX
  template each release includes, or with a `policy.json` file.
- **VioLens Capture**, a separate one-window app like Fiddler's
  FiddlerCap: someone who isn't technical clicks Start, shows the problem,
  clicks Stop and saves a `.saz` for you. On Windows it's a single `.exe`
  that runs without installing.

**Help > Documentation** opens the [User Guide](docs/User%20Guide.md), which
covers all of this.

## Getting started

You need the .NET 10 SDK.

```
dotnet restore VioLens.sln
dotnet build VioLens.sln
dotnet test VioLens.sln
dotnet run --project apps/Violens.DesktopUi
```

`dotnet run --project apps/Violens.Capture` runs VioLens Capture.

The first time you click Start, VioLens asks to trust a new root
certificate, named `DO_NOT_TRUST_ViolensRoot...` after Fiddler Classic's
convention. That's what lets it read HTTPS traffic on this computer.

- **Windows** shows its own prompt.
- **macOS** has no prompt for this, so VioLens asks first, then adds the
  certificate to your login keychain. Firefox doesn't read the keychain
  by default, so VioLens turns on its setting that does in each profile;
  restart Firefox if it was open.
- **Linux**: VioLens asks first, then adds it to your browsers'
  certificate databases with `certutil` (from `libnss3-tools` or
  `nss-tools`, which you may need to install).

Nothing you capture leaves your computer unless you send it somewhere.

## Documentation

- **Using the app:** the [User Guide](docs/User%20Guide.md), also under
  **Help > Documentation** in the app.
- **Building your own tool on the proxy engine:**
  [Embedding ProxyCore](docs/Embedding%20ProxyCore.md). The engine is on
  NuGet as `Violens.ProxyCore`, with the same version as each VioLens
  release.
- **Contributing, or curious how something works:** the rest of `docs/`
  has the design behind each part of the app, what was left out and why,
  and what's still to do.

## Project layout

- `docs/`: the [User Guide](docs/User%20Guide.md), and the design docs (the
  project plan, the certificate, preferences, FiddlerScript and .NET
  extension designs, the Fiddler feature inventory and breakpoint
  research notes).
- `src/Violens.ProxyCore`: the proxy engine, also on NuGet. The listener,
  HTTP parsing, sessions, breakpoints, AutoResponder, certificates, SAZ,
  HAR and packet capture files, the system proxy on each platform, and the
  preferences store. Only what other apps need is public; VioLens's own
  assemblies are listed in its `AssemblyInfo.cs`.
- `src/Violens.ProxyHost` and `src/Violens.ProxyHost.Client`: the engine
  for apps on .NET Framework, as a small .NET 10 host program and a .NET
  Standard 2.0 library that talks to it over a named pipe. See the
  Embedding guide's "Apps on .NET Framework".
- `src/Violens.AppCore`: VioLens's own logic outside the engine, such as
  Compare, TextWizard, QuickExec, the column layout, machine policy and
  VioLens Capture's `CaptureSession`.
- `src/Violens.Automation`: the pipe `violens-exec` uses.
- `src/Violens.Extensibility`: the inspector contract and the built-in
  inspectors, with their JSON tree, CBOR, MessagePack and Markdown readers.
- `src/Violens.Compatibility`: FiddlerScript (`FiddlerScriptRunner`),
  compiled extensions (`ExtensionHost`), and `Violens.CompatShim` for
  extensions ported from source. The two compatibility design docs
  describe what's built and what isn't.
- `apps/Violens.DesktopUi`: the Avalonia desktop app.
- `apps/Violens.Capture`: VioLens Capture.
- `samples/EmbeddingSample`: the Embedding guide's code as a console app,
  built with everything else so the guide stays accurate.
- `tools/Violens.Exec`: `violens-exec`, built with Native AOT and shipped
  beside the app.
- `tools/Violens.SampleExtension`: a small compiled extension that tests
  `ExtensionHost` against a real `.dll`.
- `tools/Violens.LegacyExtensionHost`: the optional legacy extension host,
  a `net48` process kept out of `VioLens.sln` so it can't affect the main
  build. See its README.
- `tools/Violens.DevHost`: a console host from early development.
- `tests/`: tests for the projects above, and `tests/ExtensionPorts`, which
  fetches real Fiddler Classic extensions at pinned commits, ports them and
  tests them (not in `VioLens.sln`; see its README).

## Known limitations

- **macOS has had less use than Windows.**
  - Certificates are trusted with the `security` tool in your login
    keychain, and the system proxy is set with `networksetup` for each
    network service.
  - CI checks the macOS build's signature and that it launches, but its
    tests don't run `security` or `networksetup` for real.
  - A full hands-on test is still to come: trust and removal, whether
    `networksetup` needs admin rights, recovery after a crash, and the
    notarized install.
  - Release `.dmg` files are signed and notarized when the repository's
    signing secrets are set. Other builds (forks, test builds) aren't, and
    macOS asks you to allow them once, in System Settings > Privacy &
    Security > Open Anyway.
- **Linux has been tested by hand on Ubuntu (GNOME), not yet on KDE
  Plasma.**
  - The certificate goes into the NSS databases Chrome and Chromium read
    (`~/.pki/nssdb`) and into each Firefox profile, including the snap and
    Flatpak ones, with `certutil`, which must be installed. Restart
    Firefox if it was open.
  - It doesn't go into the system store that curl and most command-line
    tools read, because that needs root access and differs between
    distributions. The User Guide's "Linux" section has the commands.
  - The system proxy is set in GNOME's and KDE Plasma's settings. On other
    desktops, and for programs that only read `http_proxy`, set it by hand.
  - The certificate's private key is a file in your home folder, which is
    where .NET keeps keys on Linux. See SECURITY.md.
  - CI tests the Linux build and checks that the packages launch, but
    doesn't run `certutil`, `gsettings` or `kwriteconfig` for real. On
    Wayland, VioLens runs through XWayland.
  - x86-64 only. There's no Snap or Flatpak, because their sandboxes would
    stop VioLens changing proxy settings and certificate databases.
- **HTTP/2 has been used for real only on Windows so far.** WebSockets stay
  on HTTP/1.1 connections (browsers arrange that), and there's no HTTP/3:
  browsers don't send it through a proxy, so they use HTTP/2.
- **Upstream proxies must be HTTP proxies.** SOCKS isn't supported (a PAC
  script's SOCKS entries are skipped), and proxy sign-in supports
  Negotiate, NTLM and Basic, not Digest. On macOS and Linux, automatic
  proxy detection looks in DNS (`wpad.<your domain>`), not DHCP.
- **Compiled Fiddler Classic extensions need rebuilding.** The main app
  loads extensions built against its own interfaces, which match
  Fiddler's at source level, so an existing `.dll` can't be dropped in.
  The optional legacy extension host runs unmodified extension source,
  recompiled against its own compatibility assembly, against live
  traffic.
- **One client certificate (mutual TLS) at a time,** sent to every server
  that asks or to the hosts you list. Fiddler Classic's
  `ClientCertificate.cer` file and `https-Client-Certificate` flag aren't
  supported.
- **No web page rendering in the app.** Avalonia has no browser control,
  so the WebView tab shows a page's text and opens a locked-down copy in
  your browser.
- **VioLens Capture is packaged for Windows only.** On macOS and Linux,
  run it from source.
- **No preferences editor (`about:config`), no headless mode, and no
  built-in NetLog import.** NetLog files open through the ported NetLog
  importer, one of the optional extensions.
- **Bodies are kept in memory.** Each is capped at 64 MB, and the **Keep**
  setting limits how many sessions are held, but nothing is spilled to
  disk.

[SECURITY.md](SECURITY.md) explains how the certificate, the listener and
the proxy host are protected, and what isn't covered yet.

## Relationship to Fiddler

VioLens is an independent open source project. It isn't affiliated
with, endorsed by or sponsored by Progress Software Corporation or
Telerik. Fiddler, Fiddler Classic, Fiddler Everywhere, FiddlerCore and
Telerik are trademarks of Progress Software Corporation. VioLens uses
those names only to say what it's compatible with, and never presents
itself, or anything it ships, as Fiddler.

VioLens is built from public documentation, the behavior anyone can
observe (such as how the real tool lays out `.saz` files), and its own
design, never from decompiled or leaked Fiddler source, and it contains no
Fiddler code. [CONTRIBUTING.md](CONTRIBUTING.md) has the clean-room policy
every contribution follows. The optional extensions in the installers are
separate works by their authors, under their own licenses (see
`THIRD-PARTY-NOTICES.txt` beside them).

VioLens is inspired by Eric Lawrence's public writing about Fiddler,
including his post [Fiddler in 2026](https://textslashplain.com/2026/08/05/fiddler-in-2026/),
and by his public [Clearinet](https://github.com/ericlaw1979/Clearinet)
repository. It isn't his project, and he hasn't contributed code to it.
It does build on some of his published open source work, under its own
licenses: the ported NetLog importer, the CSP Rule Collector fork, and
packet capture import, ported from Clearinet (see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

### Clearinet

Eric Lawrence is building his own free successor to Fiddler Classic,
[Clearinet](https://github.com/ericlaw1979/Clearinet), also under the MIT
license. It and VioLens are separate projects that set out differently:

- **Clearinet** is by the person who created Fiddler. Its main app is for
  Windows, built with WinForms to look and work like the Fiddler people
  already know, and to make porting Fiddler extensions and scripts easy.
  A cross-platform engine, Clearinet Core, is planned.
- **VioLens** runs on Windows, macOS and Linux, with an Avalonia
  interface that follows Fiddler's layout without copying its look. Its
  engine is already on NuGet as `Violens.ProxyCore`, and Fiddler
  extensions are ported from source through `Violens.CompatShim`.

VioLens started as CLeARINET because Eric's [Fiddler in 2026](https://textslashplain.com/2026/08/05/fiddler-in-2026/)
post read as a call to action, and this project set out to answer it.
It chose Avalonia to run on
every platform, while Eric chose to stay with WinForms so existing
Windows workflows keep working. With the two heading in different
directions, it was renamed VioLens in version 0.2. If you only need
Windows and want the closest thing to Fiddler Classic, Clearinet is worth
following too. Both are free for any use.

Thanks to:

- **EJ Suter-Powell** and **Jose Gonzales**, for testing VioLens on
  macOS.
- **Eric Lawrence**, who created Fiddler, for the inspiration and the
  groundwork: two decades of Fiddler itself, and his public writing about
  how it works and what he'd do differently, which shaped much of
  VioLens's design. (This doesn't mean he endorses VioLens; see
  [Relationship to Fiddler](#relationship-to-fiddler).)

## License

MIT. See [LICENSE](LICENSE).
