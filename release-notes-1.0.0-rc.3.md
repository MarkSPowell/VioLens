VioLens is a free, MIT-licensed tool for inspecting HTTP and HTTPS traffic, built as a successor to Fiddler Classic. It runs on Windows, macOS and Linux. It's an independent project, not affiliated with Progress or Telerik.

This is the third 1.0 release candidate. The features planned for 1.0 are in, and the proxy engine's public API is frozen. Please report anything that goes wrong in [issues](https://github.com/MarkSPowell/VioLens/issues) or on [Discord](https://discord.gg/NJfnGnn4PT), and security problems privately (see [SECURITY.md](https://github.com/MarkSPowell/VioLens/blob/main/SECURITY.md)).

## Downloads

| Platform | File | Notes |
|---|---|---|
| Windows x64 | `VioLens-Setup-1.0.0-rc.3.exe` | Installer, signed. SmartScreen may still warn while the publisher is new: choose **More info > Run anyway**. |
| Windows x64 | `VioLensCapture-1.0.0-rc.3-win-x64.exe` | VioLens Capture: a single `.exe` someone non-technical can run, without installing, to record a `.saz` for you. |
| macOS (Apple silicon) | `VioLens-1.0.0-rc.3-macos-arm64.dmg` | Signed with a Developer ID and notarized. |
| Linux x86-64 | `VioLens-1.0.0-rc.3-x86_64.AppImage` | `chmod +x` it and run it. Trusting the certificate needs `certutil` (`libnss3-tools` or `nss-tools`). |
| Linux x86-64 | `VioLens-1.0.0-rc.3-linux-x64.tar.gz` | The same app as a plain archive. |
| Windows x64 | `Violens.ProxyHost-1.0.0-rc.3-win-x64.zip` | Only for developers embedding the engine in a .NET Framework app. |

The engine is on NuGet as [`Violens.ProxyCore`](https://www.nuget.org/packages/Violens.ProxyCore) and, for .NET Framework apps, [`Violens.ProxyHost.Client`](https://www.nuget.org/packages/Violens.ProxyHost.Client).

Check the Linux files against `SHA256SUMS-linux.txt` with `sha256sum -c SHA256SUMS-linux.txt`.

The first time you click Start, VioLens asks to trust a root certificate named `DO_NOT_TRUST_ViolensRoot...`. That's what lets it read HTTPS on your computer. **Tools > Remove Root Certificate** takes it away again.

## What's in it

- HTTP and HTTPS capture through a local proxy, including HTTP/2, WebSockets and gRPC trailers, and forwarding through your network's proxy (PAC, WPAD, Negotiate and NTLM).
- Inspectors for headers, raw, JSON, CBOR, MessagePack, Markdown, forms, images, hex and cookies. A timing waterfall, statistics, Compare Sessions and TextWizard.
- Breakpoints, AutoResponder rules, the Composer and Replay.
- QuickExec and `violens-exec`, FiddlerScript (`CustomRules.js`), and compiled extensions. Fiddler Classic extensions can be ported from source with few changes.
- Opens and saves SAZ, saves and imports HAR, and imports HTTP from `.pcap` and `.pcapng` files.

The [User Guide](https://github.com/MarkSPowell/VioLens/blob/main/docs/User%20Guide.md) covers all of it.

## Changes since 1.0.0-rc.2

Mostly security fixes, from two reviews before 1.0. [SECURITY.md](https://github.com/MarkSPowell/VioLens/blob/main/SECURITY.md) describes how VioLens protects you now.

- **Your identity stays yours.** VioLens signs in to your network's proxy as you, or sends your client certificate, only for programs running under your own account (on Windows and Linux), and never for other computers using it as their proxy.
- **Asks before sending a proxy password in plain text,** to a proxy that only offers Basic sign-in. **Tools > Connections** also warns while a client certificate would go to every server that asks.
- **Other computers can reach less.** With **Allow remote computers to connect** on, they can't reach this computer's localhost services or cloud metadata addresses, including through DNS rebinding.
- **Safer with hostile input.** Limits on headers, bodies, decompression, WebSocket messages, PAC scripts, SAZ imports and images, and HTTP/2 Rapid Reset protection. The browser preview removes redirects more thoroughly.
- **The legacy extension host** (optional, Windows) talks to VioLens over a pipe only your account can open.
- **Firefox on macOS** now trusts VioLens's root, so HTTPS sites in Firefox don't show a warning.

## Known limitations

- macOS hasn't had a full hands-on test yet. CI checks the build's signature and that it launches, but certificate trust and removal, the system proxy and crash recovery haven't been tested end to end on a real Mac.
- On Linux the certificate goes into Chrome's and Firefox's certificate databases, not the system store curl reads. The system proxy is set on GNOME and KDE Plasma only, and only GNOME (on Ubuntu) has been tested by hand. x86-64 only.
- HTTP/2 has had real use on Windows. There's no HTTP/3 (browsers don't send it through a proxy) and no SOCKS upstream proxies.
- Compiled Fiddler Classic `.dll` extensions need rebuilding from source.
- Bodies are kept in memory.

The full list is in the README's [Known limitations](https://github.com/MarkSPowell/VioLens#known-limitations).
