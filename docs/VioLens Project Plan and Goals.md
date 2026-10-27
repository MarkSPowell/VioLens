# VioLens Project Plan and Goals

## Motivation

VioLens is a successor to Fiddler Web Debugger (Fiddler Classic). Fiddler
Classic went to Telerik in 2012 and Progress Software in 2014, and its
development slowed after its original author left in 2016. In August 2026
Progress limited Fiddler Classic to non-commercial use, pointing commercial
users at Fiddler Everywhere (see Progress's
[commercial-use page](https://www.telerik.com/fiddler/fiddler-classic/commercial-use)).
VioLens is a free, open source tool for the same workflows, under a licence
that can't later be withdrawn (see `CONTRIBUTING.md`).

## Tenets

1. **Be as API/usage-compatible with Fiddler Classic as possible.** Session
   state names, breakpoint commands (`bpu`/`bpm`/`bps`), inspector tab names
   and AutoResponder's match/action syntax all follow Fiddler Classic's. The
   near-term goal is that an existing Fiddler Classic *workflow or extension*
   ports with minimal effort, not just that the UI feels familiar.
2. **Catch up with the web since 2016.** For example zstd decompression,
   dropping bzip2, and knowing about SDCH.
3. **Add what users need**, rather than cloning feature for feature. The
   session filter uses DevTools-style tokens (`method:`, `host:`, `status:`)
   instead of QuickExec's sigils.
4. **Build a reusable proxy core.** `Violens.ProxyCore` and
   `Violens.Extensibility` have no UI-toolkit dependency, so other hosts can
   embed them (see the Embedding ProxyCore guide).
5. **Don't get sued.** VioLens never presents itself as Fiddler. No shipped
   assembly, namespace or file is named "Fiddler"; the word appears only to
   describe compatibility. No Fiddler code is used, and third-party code is
   used only under its licence. This costs something: compiled Fiddler
   Classic extensions don't load unmodified (see the .NET Extension
   Compatibility Design). This tenet wins when it conflicts with tenet 1.

## Clean-room policy

VioLens is built from public documentation (the
[Fiddler Classic docs](https://github.com/telerik/fiddler-docs)), publicly
observable behaviour such as the structure of `.saz` files, and its own
design decisions. Never from decompiled or leaked Fiddler source. The full
policy is in `CONTRIBUTING.md`.

## License

MIT, the same as Eric Lawrence's
[ericlaw1979/Clearinet](https://github.com/ericlaw1979/Clearinet), which
inspired this project, so code can move between the two. VioLens is
independent: it isn't his project and he hasn't contributed to it.

## Platform and technology

- C# on .NET 10.
- [Avalonia UI](https://avaloniaui.net/) for Windows and macOS from one
  codebase. Clearinet planned WinForms first; cross-platform from day one
  was the point of choosing differently.
- FiddlerScript runs through a translation layer rather than JScript.NET,
  which was never ported to .NET Core
  ([dotnet/runtime#27155](https://github.com/dotnet/runtime/issues/27155)).
  [Jint](https://github.com/sebastienros/jint) runs `CustomRules.js` after a
  small preprocessing pass strips JScript.NET's type annotations. Roslyn
  scripting for `CustomRules.cs` is planned.
- No headless/CLI mode in the MVP.

## Phases

- **Phase 0, scaffolding.** Project structure and placeholder types.
- **Phase 1, HTTPS decryption.** Capture a browser's HTTPS traffic with
  `Violens.DevHost` and save a `.saz` file Fiddler Classic can open. Done.
- **Phase 2, desktop MVP.** The Avalonia app with a live session list,
  breakpoints, filtering, decompression, SAZ import/export, JSON/WebForms/
  image inspectors, Composer and Replay, HAR, upstream proxies and capturing
  other devices. This is where the project is now.
- **Phase 3, beta.** Extension compatibility, started early because tenet 1
  makes it an MVP concern for existing users:
  - FiddlerScript: `OnBeforeRequest`/`OnBeforeResponse` run against live
    traffic, along with Rules-menu options, context actions, tools actions
    and script-defined columns. See the FiddlerScript Compatibility Design.
  - Compiled extensions, recompiled from source against
    `Violens.CompatShim`, load from an Extensions folder into their own
    `AssemblyLoadContext`s. `IAutoTamper`, `Inspector2` and the
    import/export interfaces are wired up.
  - Extensions that need WinForms types removed from .NET 5+ run
    out of process in `tools/Violens.LegacyExtensionHost`, a `net48` host
    that receives sessions over a named pipe. Optional and off by default.

## Fiddler Classic compatibility review

A Fiddler Classic workflow usually includes custom automation, not just UI
habits, so tenet 1 makes extension support an MVP concern rather than a
beta one. Fiddler has two mechanisms: FiddlerScript (`CustomRules.js`, a
`Handlers` class with `OnBeforeRequest` and friends) and compiled .NET
extensions (`IFiddlerExtension`, `IAutoTamper`, `Inspector2`,
`ISessionImporter`/`ISessionExporter`). FiddlerScript came first: it's the
everyday customization path, while compiled extensions are the heavier,
less common one. Compiled extensions are supported at the source level
(recompiled against VioLens's interfaces), not as unmodified binaries.

## Lessons from Fiddler's own history

Eric Lawrence's
[retrospective on Fiddler's mistakes](https://textslashplain.com/2024/11/24/fiddler-my-mistakes/)
shaped several decisions:

- **No public fields.** `Session` and the captured message types are
  records with init-only properties, so there's nothing to convert later.
- **Async I/O throughout.** The proxy never blocks a pool thread on the
  network, avoiding both thread-per-connection and ThreadPool starvation.
- **Open source from the start**, which he calls his biggest regret not
  doing.
- **Cross-platform UI**, rather than WinForms lock-in.
- **Extensibility early**, not as a retrofit.
- **A deadlock-safe preferences store.** It takes no lock on reads and never
  holds one across I/O or a watcher callback (see the Preferences Design).
- **Bounded memory.** Captured bodies are capped, and the session list
  can keep only the newest N sessions. Spilling large bodies to disk is
  still to do.

One repeat is deliberate: the core type is called `Session`, which he
regrets, because tenet 1 matters more here. HTTP/2 is supported and on
by default: `SslStream` exposes ALPN, which was his blocker.

## Extensible config lists

From Clearinet's
[Issue #2](https://github.com/ericlaw1979/Clearinet/issues/2): any list of
data that may change (browser process names, hosts to always or never
intercept, and so on) should come from an overridable preference, not a
hardcoded array. Preferences use `violens.config.<category>.<name>` keys
with semicolon-separated values, read through `GetListPref`/`SetListPref`.

## macOS release

`.github/workflows/release-macos.yml` builds an arm64 `.dmg`:

- **Developer ID signed and notarized**, when the repository's signing
  secrets are set: the app with the hardened runtime, then the `.dmg`, and
  Apple's ticket stapled to both. The secrets, and the one-time steps to
  make the certificate and the App Store Connect API key, are in the
  workflow's header comment. Without the secrets (forks, test builds) the
  app is ad-hoc signed instead, and Gatekeeper asks once on first launch;
  System Settings > Privacy & Security > Open Anyway gets past it.
  `installer/macos/smoke-test.sh` checks the signature (and for a signed
  build, the hardened runtime, the stapled tickets and Gatekeeper's
  verdict), the `.dmg` contents and that the app launches.
- **A `.dmg`**, for the usual drag-to-Applications install.
- **arm64 only.** An Intel or universal build is a small addition if it's
  wanted.

`installer/macos/build-installer.sh` assembles the `.app` bundle and the
`.dmg` and runs fine outside CI.

## Road to 1.0

Written up on 27 October 2026, after HTTP/2 went into preview
(0.3.0-preview.1). 1.0 is the point where the app should hold up to
all-day use on both platforms and ProxyCore's public API stops changing.

Must have:

1. **HTTP/2 out of preview.** Mostly done: on by default in the app and
   in `InterceptingProxyListener`, after real browsing on Windows showed
   no problems. Still to do: browsing on macOS, and settling ProxyCore's
   representation of HTTP/2 requests with the API freeze (the Embedding
   guide still warns it may change).
2. **ProxyCore's public API frozen.** One review of everything public;
   anything not ready becomes internal. From 1.0, semantic versioning:
   breaking changes wait for 2.0.
3. **A full hands-on macOS test.** Certificate trust and removal, the
   system proxy (and whether `networksetup` needs admin rights), restoring
   proxy settings after a crash, and the notarized install. Then update the
   README's macOS limitation.
4. **Memory over long captures.** Done: a Keep drop-down (Fiddler
   Classic's "Keep: N sessions") backed by `SessionStore.MaxSessions`.
5. **Skip decryption for chosen hosts.** Done: the
   `violens.config.https.skipdecryption` list, edited on the Connections
   row, tunnels those hosts untouched
   (`InterceptingProxyListener.SkipDecryptionHosts`).
6. **Diagnostics for bug reports.** Done: the bug report issue form
   (`.github/ISSUE_TEMPLATE/bug_report.yml`) and Help > Copy Diagnostic
   Info to fill most of it in.

Should have:

7. **Client certificates (mutual TLS).** Done: one certificate from the
   personal store, sent to every server that asks or only to listed hosts
   (`InterceptingProxyListener.ClientCertificate` and
   `ClientCertificateHosts`). Fiddler's `ClientCertificate.cer` and
   `https-Client-Certificate` flag could follow.
8. **Update notice.** Done: a start-up check of GitHub's releases (Help
   menu to check now or turn it off), offering previews only to preview
   users.
9. **Docs brought up to date.** Done: the README's Known limitations and
   the Interception Certificate Design now match what's built. Revisit
   after the macOS test.
10. **A security review.** Done; the results are in SECURITY.md. It
    stopped other devices reaching this computer's localhost services
    through the proxy, and the host now refuses a pipe made by another
    account. Left open: the app's pipe uses the default permissions (a
    denial of service only).

Fine after 1.0: an about:config editor, SOCKS and Digest proxy sign-in,
DHCP proxy discovery on macOS, built-in NetLog import, `CustomRules.cs`,
a headless/CLI mode, and spilling large bodies to disk.

Suggested order: 6, 4 and 5 first (small to medium, and users will
notice; done), then the macOS test and the docs, then HTTP/2 and the API
freeze together so the API is frozen once it's stable.

## Still undecided

- How the project sustains itself long term.
- Governance, and maintainers beyond the original author.
- Priority order of the remaining Fiddler features (see the Fiddler Feature
  Inventory).
- Whether to set a target date.
