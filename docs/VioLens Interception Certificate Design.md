# VioLens Interception Certificate Design

This document exists because ten source files reference "the Interception
Certificate Design doc" for design rationale that was never actually
written down anywhere accessible — only scattered across code comments.
This reconstructs what's knowable from those comments and from the
implementation itself (`src/Violens.ProxyCore/Certificates/`), which
already fully implements the Windows half of what's described below. Where
a decision was made but the reasoning behind it isn't recoverable from the
code, that's marked explicitly rather than guessed at.

## Why a certificate authority at all

To inspect HTTPS traffic, VioLens sits in the middle of the TLS
connection: it terminates TLS toward the client with its own certificate,
decrypts the traffic, and separately terminates TLS toward the real
server. For the client (a browser) to accept VioLens's certificate
without a warning, it needs to trust the certificate authority that
signed it — the same interception model Fiddler Classic itself uses, and
the reason every such tool has to install a root certificate at all.

## The root certificate authority (`CertificateAuthority`)

One root CA is generated per install, per user:

- **Algorithm**: ECDSA on the P-256 curve, SHA-256 signing.
- **Validity**: 5 years from one day before generation (a small
  `notBefore` buffer against clock skew).
- **Subject**: `DO_NOT_TRUST_ViolensRoot (<machine name>, generated
  <date>)` — the `DO_NOT_TRUST_` prefix follows Fiddler Classic's own
  long-standing convention (`DO_NOT_TRUST_FiddlerRoot`), so a stale or
  foreign copy is recognizable in a trust store at a glance.
- **Key storage**: the private key is persisted with `PersistKeySet` but
  deliberately without `Exportable` — the key exists as a real key
  container on disk, but can't be exported back out through the
  certificate API once created. It's exported to an in-memory PFX only
  for that one round trip (immediately re-imported with the flags
  actually wanted), and the PFX bytes are zeroed afterward.
- **Store location**: `CurrentUser\My` (personal store) for the
  certificate-with-key, `CurrentUser\Root` (trusted root store) for trust.
  A per-user install, not per-machine — no admin elevation required, and
  no other user account on the same machine is affected.
- **Reuse across runs**: on startup, an existing, still-valid, still
  privately-keyed root already in the personal store is reused rather
  than generating a new one every launch — the same root keeps working
  across Start/Stop cycles and app restarts, so the client's trust
  decision doesn't have to happen more than once.

### Installing trust

Adding the root to `CurrentUser\Root` is what triggers Windows' native
"Do you want to install this certificate?" prompt the first time — this
is deliberate and not something VioLens tries to suppress or automate
around. A silent, unprompted root-CA install would be indistinguishable
from malware behavior; relying on the OS's own confirmation dialog is the
honest way to do this, and it's the same prompt experience Fiddler
Classic's own documented setup walks users through.

### Removal

`CertificateAuthority.Uninstall()` removes the root from both the
personal and trusted-root stores, and `CertificateAuthority.RemoveRoots`
removes every root with VioLens's name, including ones from earlier
installs, along with their keys. Both are wired up: **Tools > Remove Root
Certificate** in the app, the Windows uninstaller (through
`Program.RemoveRootCertificateArgument`), and
`ProxyHostClient.UninstallRootCertificateAsync` for apps using the proxy
host. On Windows the OS asks before each removal from the trusted-root
store; a "No" keeps that root's key too, so nothing is left half-removed.

## Leaf certificates (`LeafCertificateProvider`)

A fresh leaf certificate is signed for each host VioLens intercepts,
signed by the root above:

- **Algorithm**: ECDSA P-256 again, for consistency with the root.
- **Validity**: 7 days, `notBefore` backdated 5 minutes for clock-skew
  tolerance. Short-lived by design — these are ephemeral, per-run
  certificates, not something meant to be reused across long stretches of
  time.
- **Subject / SAN**: the certificate's Subject Alternative Name carries
  the actual host — a DNS name, or an IP address when the host being
  connected to is literally an IP. Modern clients (Chromium in
  particular) validate against the SAN, not the legacy Subject Common
  Name, so getting the SAN right is what actually matters for the
  certificate to be accepted.
- **Extended Key Usage**: server authentication only (OID
  `1.3.6.1.5.5.7.3.1`).
- **Key usage**: `DigitalSignature` only — no `KeyEncipherment`, since
  that's an RSA-specific usage that doesn't apply to an EC key.
- **Chain building**: the leaf carries an Authority Key Identifier tying
  it back to the issuing root's key, which strict validators (and some
  Chromium checks) expect even for a simple two-level chain.

### Caching and concurrency

Leaves are cached per host and reused until within 5 minutes of
expiring. The cache is a single-flight design
(`ConcurrentDictionary<string, Lazy<X509Certificate2>>` with
`ExecutionAndPublication`): when a real browser opens several parallel
connections to a host it hasn't talked to yet, all of them hit the same
uncached-host case within milliseconds of each other, and only the first
caller actually signs a certificate — the rest wait for that one result
instead of each racing to sign (and persist a key container for) the same
host redundantly. This was built specifically because that redundant-race
behavior was reproduced live as intermittent `AuthenticationException` /
`Win32Exception: An unknown error occurred while processing the
certificate` failures under concurrent signing.

A failed signing attempt evicts itself from the cache rather than caching
the failure forever (the default behavior of a faulted
`Lazy<T>` under `ExecutionAndPublication`), so a transient failure doesn't
poison every future request for that host until the process restarts.

### The ephemeral-key problem

`CertificateRequest.Create(...).CopyWithPrivateKey(...)` hands back a
certificate whose private key exists only in memory. On Windows,
`SslStream`'s server-side handshake (Schannel) refuses such a certificate
outright with "the platform does not support ephemeral keys." The fix is
an export-then-reimport round trip like the root's, but without
`PersistKeySet` and without adding the leaf to any store. The key then
lives in a real key container, which Schannel accepts, and Windows
deletes that container when the certificate is released. Leaf keys don't
pile up in the user's profile. (An earlier version imported leaves with
`PersistKeySet` and left one container behind per leaf.) A leaf dropped
from the cache near expiry isn't disposed straight away, since a
handshake may still be using it; its container goes when the garbage
collector releases it.

A freshly persisted key container also isn't always immediately ready for
Schannel to use — reproduced live as an occasional, isolated
`AuthenticationException` failure right in the middle of a real client's
TLS handshake, independent of the concurrency issue the single-flight
cache already guards against. `WarmUpPrivateKey` forces one real
private-key signing operation immediately after import (before the
certificate is ever handed to `SslStream`), with up to 2 attempts, so
that failure mode surfaces (and can be retried) right after import — a
place where it's cheap to reason about — rather than mid-handshake, where
it silently drops one of the client's connections.

## Platform status

**Windows**: fully implemented — this entire document describes the
Windows half of the design, and it's what's actually running today.

**macOS**: built this pass, answering the three open questions this
section used to pose. Mark's own calls, in order:

1. **Shell out to the `security` command-line tool**, not P/Invoke
   `Security.framework` directly. `SecTrustSettingsSetTrustSettings` and
   friends have notoriously fiddly `CFType`/`CFDictionary` marshaling in
   .NET, and with no Mac available to this session to compile or run
   against, blind P/Invoke declarations carried real risk of shipping
   something subtly wrong in a way nothing here could catch. Shelling out
   is also precedented: it's the same approach `dotnet dev-certs
   https --trust` itself uses on macOS for the identical problem (trust a
   locally-generated root for local HTTPS).
2. **Per-user login keychain, not the system keychain** — matching the
   existing Windows design (`CurrentUser\Root`, no admin elevation)
   exactly, at the cost of only that one user account being able to see
   decrypted traffic, same trade-off Windows already made.
3. **Both cert trust and system proxy registration, together, in this
   pass** — cert trust alone doesn't produce a usable macOS build; without
   also pointing the system proxy at VioLens, a Mac user would have to
   set that by hand in System Settings every session. See "System proxy
   registration," below.

**Only partly tested on a real Mac.** The first real Mac run found the
Start freeze described below, which is fixed, and CI checks that the
signed app launches. A full hands-on test is item 3 of the Project Plan's
Road to 1.0. It still has to confirm that Chrome honors the trust setting
straight away, how the password prompt behaves, that the 7-day leaf
certificates satisfy Apple's rules for server certificates, and that
removal works.

### Keychain trust, mechanically

`MacOSCertificateTrust` (`src/Violens.ProxyCore/Certificates/`) shells
out to two commands, called from `CertificateAuthority.EnsureTrusted`/
`Uninstall` exactly where the Windows `X509Store(StoreName.Root, ...)`
calls already were — see that class's own remarks for the per-platform
dispatch, which also closes a latent gap: those two methods had no
platform guard at all before this pass, so running on Linux (or any
platform that isn't Windows) would already have hit an unhandled
`CryptographicException` from the Windows-only `X509Store` call, not a
clean "unsupported platform" message. Both are fixed now.

- **Install**: exports the root to a temporary DER-encoded (`.cer`) file,
  then runs `security add-trusted-cert -r trustRoot <path>` — no `-k`
  (defaults to the user's own default keychain, normally
  `login.keychain-db`, rather than hardcoding that path, which can
  legitimately differ) and no `-d` (that flag is specifically what adds
  the *admin* cert store, i.e. system-wide trust, which needs elevation;
  omitting it is what keeps this per-user and unelevated). The temp file
  is deleted in a `finally` whether or not the command succeeds.
- **Uninstall**: `security delete-certificate -Z <SHA-1 thumbprint>` —
  matched by hash (`X509Certificate2.Thumbprint` is exactly the uppercase
  hex `security` expects for `-Z`), not by common name, so there's no
  ambiguity if more than one VioLens root ever ends up in the same
  keychain (an old one from a previous install that was never cleanly
  uninstalled, say). Deleting the certificate item removes its trust
  settings along with it as far as this session's own reading of
  `security`'s documented behavior goes; genuinely unconfirmed without a
  real keychain to test the edge cases against (a cert re-added after a
  previous partial removal, for instance).
- **Both invocations** go through `Process.Start`, capture stdout/stderr,
  and throw a `CertificateTrustException` (carrying the exit code and
  captured stderr) on a non-zero exit rather than swallowing the failure
  -- matches this project's own "surfaced, not silently dropped" posture
  everywhere else.

### The silent-install tension, and how it's resolved

This document's own "Installing trust" section above states the
principle plainly: "a silent, unprompted root-CA install would be
indistinguishable from malware behavior." Windows satisfies that for
free — adding to `CurrentUser\Root` is what *triggers* the OS's own "do
you want to install this certificate?" dialog. `security
add-trusted-cert` has no equivalent: run from a process that already has
an unlocked login keychain (the normal case for a logged-in user's own
GUI app), it modifies trust settings with no OS-level prompt at all.

Shelling out was still the right call (see above), so the fix is
VioLens's own: before ever invoking `security add-trusted-cert`,
`MainWindow` shows a small, explicit confirmation dialog naming exactly
what's about to happen (installing a root CA that can decrypt this
account's HTTPS traffic) and requires an explicit click before
`CertificateAuthority`'s constructor is even called on macOS. This keeps
the "never silent" principle intact by VioLens's own doing, rather than
depending on the OS to provide it the way Windows' design happened to.
`Start()` awaits that dialog (`Window.ShowDialog<bool>` returns a `Task`),
which makes `Start()` an `async void` command handler. An earlier version
blocked on it instead (`ShowDialog<bool>(owner).GetAwaiter().GetResult()`),
on the theory that `ShowDialog` keeps the message loop running underneath
a synchronous wait. It doesn't: the first real Mac test froze the app on
the first click of Start, because the dialog needed the UI thread that was
waiting for it. The certificate install itself (`CertificateAuthority`'s
constructor, which runs `security add-trusted-cert` and waits while macOS
asks for the person's password) then runs on a background thread, so the
window keeps drawing; Start is disabled until it finishes. On Windows,
`CertificateAuthority` is still created on the UI thread, so Windows' own
install dialog stays in front of the app.

### System proxy registration

A second, separate gap from the Keychain one above, closed in the same
pass since cert trust alone isn't a usable macOS build: `WinInetSystemProxy`
(`src/Violens.ProxyCore/SystemProxy/`) is Windows-only (a single global
WinINET registry setting); its new sibling, `MacOSSystemProxy`, shells out
to `networksetup` instead. macOS has no single global proxy setting the
way Windows does — proxy configuration is per network *service* (Wi-Fi,
Ethernet, and so on, each configurable independently) — so `Enable`/
`Disable` iterate every service `networksetup -listallnetworkservices`
reports (skipping ones prefixed `*`, which denotes disabled) rather than
trying to guess which one is "the" active one. `-setwebproxy`/
`-setsecurewebproxy` point HTTP and HTTPS traffic at
`127.0.0.1:<port>`; `-setwebproxystate`/`-setsecurewebproxystate` toggle
them back off on `Disable`. The same crash-safety shape
`WinInetSystemProxy` already established: a JSON backup file (one entry
per network service this time, not one flat record) written before the
first `Enable` in a run, restored and deleted by `Disable`, and replayed
by `RecoverFromCrash` on the next launch if a previous run's own `Disable`
never got the chance to run. Proxy bypass-domain lists
(`networksetup -setproxybypassdomains`) are deliberately left untouched
this pass, unlike `WinInetSystemProxy`'s own `<local>` merge -- a
narrower scope, not an oversight, since parsing
`-getproxybypassdomains`' output correctly without a real macOS network
service list to test against felt like more unverified surface than this
pass should add. A `SystemProxyController` facade
(`src/Violens.ProxyCore/SystemProxy/`) dispatches to whichever of the
two platform implementations applies, so `MainWindowViewModel`'s call
sites don't carry their own platform branching.

**A real open risk, not just an unverified detail: whether `networksetup`
needs admin elevation at all.** Windows' own no-admin-required story
(`CurrentUser` registry keys, no UAC prompt) was part of why per-user
Keychain trust was chosen over system-wide above -- but plenty of
real-world `networksetup` usage in the wild runs it under `sudo`, and
this session found no way to confirm from here whether `-setwebproxy`/
`-setsecurewebproxy` genuinely work for a plain logged-in user against
their own network service, or silently fail (or prompt) without
elevation. If it turns out elevation is required, that's a real
follow-up, not a quick fix: either accept the admin-prompt regression
system proxy registration would then carry (asymmetric with the Keychain
half of this same pass), or fall back to *not* auto-registering the
system proxy on macOS and documenting the manual System Settings steps
instead. Flagged here rather than assumed away.

**Testing scope, deliberately narrow.** `ci.yml`'s own test matrix
already runs on `macos-latest` (a real Mac, via GitHub Actions), which
means unit tests actually could exercise real `security`/`networksetup`
invocations there in a way nothing else in this project's macOS work can
be verified. This pass doesn't take that step -- mutating the CI runner's
own keychain and network-service proxy state as a side effect of `dotnet
test`, with the elevation question above still open, felt like more risk
to an otherwise-green CI matrix than this pass should introduce
unreviewed. `MacOSCertificateTrustTests`/`MacOSSystemProxyTests` stick to
the same pure-logic-only shape `WinInetSystemProxyTests` already
established for the Windows side (argument/JSON construction, no real
process invocation) -- a real macOS integration test, gated to only run
when `OperatingSystem.IsMacOS()`, is a reasonable next step once the
elevation question above has an answer.

### Linux

Added after the macOS work, on the same pattern: shell out to the
platform's own tools, for the current user only, with VioLens's own
confirmation dialog first (`TrustConfirmationWindow`, shared with macOS)
since nothing on Linux prompts.

**Trust.** Linux has no one trust store every app reads, so
`LinuxCertificateTrust` (`src/Violens.ProxyCore/Certificates/`) adds the
root to the stores browsers actually use, all per-user and without
elevation:

- the shared NSS database, `~/.pki/nssdb`, which Chrome, Chromium, Edge,
  Brave and other NSS apps read. It's made (`certutil -N
  --empty-password`) if it isn't there yet, which is normal on a fresh
  account that has never run Chrome. The Chromium snap's own copy
  (`~/snap/chromium/current/.pki/nssdb`) too, when there is one;
- every Firefox profile with a `cert9.db`, under `~/.mozilla/firefox`,
  the snap's `~/snap/firefox/common/.mozilla/firefox` and the Flatpak's
  `~/.var/app/org.mozilla.firefox/.mozilla/firefox`. A running Firefox
  may not notice until it restarts;
- .NET's own per-user root store (`X509Store(Root, CurrentUser)`, a
  folder under `~/.dotnet`), for .NET apps. Best effort.

Each NSS change is `certutil -A -d sql:<db> -n <nickname> -t C,, -i
<root.cer>`, trust flags `C,,` meaning "a CA trusted to issue server
certificates" and nothing else (not email, not code signing). The
nickname is the root's name prefix and SHA-1 thumbprint, so removal can
find this root, or every VioLens root from earlier installs, by listing
nicknames (`certutil -L`) and deleting matches (`certutil -D`). `certutil`
comes from the distribution's NSS tools package (`libnss3-tools`,
`nss-tools`, `nss`), which isn't always installed; without it Start fails
with a message saying which package to install. A database that can't be
changed is logged and skipped; Start fails only if none took the root.

The system store (`/usr/local/share/ca-certificates` with
`update-ca-certificates`, `/etc/pki/ca-trust/source/anchors` with
`update-ca-trust`) is deliberately left alone: it needs root, differs by
distribution, and would make the root trusted for every account on the
computer, which the Windows and macOS designs avoid. The User Guide gives
the commands for someone who wants curl and other command-line tools to
trust it.

**The key.** The root and its key are kept in .NET's per-user `My` store,
as on the other platforms. On Linux that's a PKCS#12 file under
`~/.dotnet/corefx/cryptography/x509stores/my`, readable only by the
account; the non-exportable flag has no OS key store to enforce it.

**System proxy.** A desktop setting, not an OS one. `LinuxSystemProxy`
(`src/Violens.ProxyCore/SystemProxy/`) sets GNOME's
(`org.gnome.system.proxy`, with `gsettings`; also used by Cinnamon,
Budgie, Pantheon and others) and KDE Plasma's (`kioslaverc`'s Proxy
Settings, with `kwriteconfig6` or `kwriteconfig5`, then a D-Bus signal so
running KDE apps reread it), whichever are present. The previous values
are backed up exactly as the tools print them, to a JSON file, and
written back by `Disable` or by `RecoverFromCrash` on the next launch,
the same crash-safety shape as Windows and macOS. Other desktops get a
status message saying to set the proxy by hand. Detecting the upstream
proxy reads the same settings (manual, a PAC script, or discovery), then
falls back to `https_proxy`/`http_proxy` in VioLens's own environment.

**Testing.** `ci.yml` runs the whole test suite on `ubuntu-latest`, and
`release-linux.yml` smoke-tests the AppImage and tarball, including a
launch under Xvfb. As on macOS, the tests stop at argument building and
output parsing (`LinuxCertificateTrustTests`, `LinuxSystemProxyTests`);
nothing runs `certutil`, `gsettings` or `kwriteconfig` for real. A
hands-on test on GNOME and KDE is still to come.

## Open risks summary

- Linux trust, removal and system proxy registration: built, not yet
  tested on a real desktop (see "Linux" above). Includes whether Chrome
  and Firefox pick up the root without a restart, KDE Plasma 6's
  `kwriteconfig6` behaviour, and Wayland sessions.

- macOS Keychain trust, removal and system proxy registration: built,
  only partly tested on a real Mac (see "Platform status" above). That
  includes whether `networksetup` needs admin rights, and whether the
  root's key is non-extractable in the keychain.
- Leaf key containers: no longer an open risk (see "The ephemeral-key
  problem").
- Root removal: wired up (see "Removal").

SECURITY.md summarizes how the root, the listener and the proxy host are
protected.
