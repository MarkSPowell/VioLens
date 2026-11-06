# VioLens Interception Certificate Design

How VioLens makes and trusts the certificates it uses to read HTTPS
traffic, on each platform. The code is in
`src/Violens.ProxyCore/Certificates/`. Where the reason for a decision
can't be recovered from the code, this says so instead of guessing.

## Why a certificate authority

To inspect HTTPS, VioLens sits in the middle of the TLS connection. It
ends TLS with the client using its own certificate, decrypts the traffic,
and makes a separate TLS connection to the real server. A browser only
accepts VioLens's certificate without a warning if it trusts the
certificate authority that signed it. Fiddler Classic works the same way,
and it's why every tool like this installs a root certificate.

## The root certificate authority (`CertificateAuthority`)

Each install makes one root CA for the user:

- **Algorithm**: ECDSA on the P-256 curve, signed with SHA-256.
- **Validity**: 5 years, starting a day before it was made, to allow for
  clock skew.
- **Subject**: `DO_NOT_TRUST_ViolensRoot (<machine name>, generated
  <date>)`. The `DO_NOT_TRUST_` prefix follows Fiddler Classic's
  `DO_NOT_TRUST_FiddlerRoot`, so a stale or foreign copy is easy to spot in
  a trust store.
- **Key storage**: the private key is saved with `PersistKeySet` but not
  `Exportable`. It lives in a real key container on disk and can't be
  exported again through the certificate API. It's exported to an
  in-memory PFX once, to re-import it with the right flags, and the PFX
  bytes are zeroed afterwards.
- **Where it's kept**: `CurrentUser\My` (the personal store) holds the
  certificate and key, and `CurrentUser\Root` (trusted roots) holds the
  trust. It's per user, so it needs no admin rights and doesn't affect
  other accounts.
- **Reuse**: at start-up, a root that's already in the personal store,
  still valid and still has its key is used again, so the user only has
  to trust it once.

### Installing trust

On Windows, adding the root to `CurrentUser\Root` makes Windows ask "Do
you want to install this certificate?" the first time. VioLens doesn't try
to avoid that. Installing a root CA silently would look exactly like
malware, and the OS's own prompt is the same one Fiddler Classic's setup
walks people through.

### Removal

`CertificateAuthority.Uninstall()` removes the root from the personal and
trusted-root stores. `CertificateAuthority.RemoveRoots` removes every root
with VioLens's name, including ones from earlier installs, with their
keys. Both are used by **Tools > Remove Root Certificate**, the Windows
uninstaller (`Program.RemoveRootCertificateArgument`) and
`ProxyHostClient.UninstallRootCertificateAsync` for apps using the proxy
host. On Windows the OS asks before each removal from the trusted-root
store; answering No keeps that root's key too, so nothing is left
half-removed.

## Leaf certificates (`LeafCertificateProvider`)

VioLens signs a new certificate for each host it intercepts, with the root
above:

- **Algorithm**: ECDSA P-256, like the root.
- **Validity**: 7 days, starting 5 minutes early for clock skew. They're
  only meant to last for a run.
- **Subject Alternative Name**: the host, as a DNS name, or as an IP
  address when the client connected to an IP. Modern clients (Chromium in
  particular) check the SAN rather than the Common Name, so that's the part
  that has to be right.
- **Extended key usage**: server authentication only
  (`1.3.6.1.5.5.7.3.1`).
- **Key usage**: `DigitalSignature` only. `KeyEncipherment` is for RSA
  keys and doesn't apply to an EC key.
- **Chain building**: each leaf has an Authority Key Identifier pointing
  at the root's key, which strict validators (and some Chromium checks)
  expect even in a two-level chain.

### Caching and concurrency

Leaves are cached per host and reused until 5 minutes before they expire.
The cache makes each certificate once
(`ConcurrentDictionary<string, Lazy<X509Certificate2>>` with
`ExecutionAndPublication`). A browser opens several connections to a new
host within milliseconds of each other; the first one signs a certificate
and the rest wait for it, instead of all signing (and saving a key for)
the same host at once. That race used to cause intermittent
`AuthenticationException` and `Win32Exception: An unknown error occurred
while processing the certificate` failures.

A failed signing removes itself from the cache. A faulted `Lazy<T>` would
otherwise keep returning the failure, and one passing problem would break
that host until the app restarted.

### The ephemeral-key problem

`CertificateRequest.Create(...).CopyWithPrivateKey(...)` returns a
certificate whose key exists only in memory. On Windows, `SslStream`'s
server side (Schannel) refuses it: "the platform does not support
ephemeral keys". The fix is to export and re-import it, as with the root,
but without `PersistKeySet` and without adding the leaf to any store. The
key then lives in a real key container, which Schannel accepts, and
Windows deletes the container when the certificate is released, so leaf
keys don't build up in the user's profile. (An earlier version used
`PersistKeySet` and left a container behind for every leaf.) A leaf
dropped from the cache isn't disposed straight away, since a handshake may
still be using it; its container goes when the garbage collector releases
it.

A newly saved key container also isn't always ready for Schannel
straight away, which showed up as an occasional `AuthenticationException`
in the middle of a client's handshake, separate from the race above.
`WarmUpPrivateKey` signs something with the key right after import,
before `SslStream` sees the certificate, and tries twice. That way the
problem shows up, and can be retried, at a point where it's easy to
handle, not halfway through a handshake where it drops one of the
client's connections.

## Platform status

**Windows**: everything above describes Windows, and it's what runs
today.

**macOS** made three choices:

1. **Run the `security` command-line tool** rather than calling
   `Security.framework` directly. `SecTrustSettingsSetTrustSettings` needs
   awkward `CFType` and `CFDictionary` marshaling in .NET, and with no Mac
   to test on, hand-written P/Invoke declarations could easily have been
   wrong in ways nothing would catch. `dotnet dev-certs https --trust`
   takes the same approach to the same problem on macOS.
2. **The user's login keychain, not the system keychain**, to match
   Windows (`CurrentUser\Root`, no admin rights). As on Windows, only that
   account can see decrypted traffic.
3. **Trust and the system proxy together**. Trust alone isn't usable:
   without the system proxy pointing at VioLens, a Mac user would have to
   set it by hand in System Settings each time. See "System proxy
   registration" below.

**macOS has only been partly tested on a real Mac.** The first Mac run
found the Start freeze described below, which is fixed, and CI checks that
the signed app launches. A full hands-on test is item 3 of the Project
Plan's Road to 1.0. It has to confirm that Chrome honors the trust
straight away, how the password prompt behaves, that 7-day leaf
certificates meet Apple's rules for server certificates, and that removal
works.

### Keychain trust

`MacOSCertificateTrust` runs two commands, called from
`CertificateAuthority.EnsureTrusted` and `Uninstall` in place of the
Windows `X509Store(StoreName.Root, ...)` calls. (Before macOS support,
those methods had no platform check, so any platform other than Windows
got an unhandled `CryptographicException` rather than a clear message.)

- **Install**: writes the root to a temporary DER (`.cer`) file and runs
  `security add-trusted-cert -r trustRoot <path>`. There's no `-k`, so it
  goes into the user's default keychain (normally `login.keychain-db`,
  but that can differ), and no `-d`, which would add it to the admin store
  for the whole system and need elevation. The temporary file is deleted
  whether or not the command succeeds.
- **Uninstall**: `security delete-certificate -Z <SHA-1 thumbprint>`. It
  matches by hash (`X509Certificate2.Thumbprint` is the uppercase hex
  `-Z` expects), not by name, so it can't pick the wrong root when an old
  one from an earlier install is still there. Deleting the certificate
  should remove its trust settings too, going by `security`'s
  documentation; that hasn't been checked on a real keychain, including
  odd cases such as a certificate added again after a partial removal.
- **Both** run through `Process.Start`, capture the output, and throw a
  `CertificateTrustException` (with the exit code and error output) if
  the command fails.

### Asking before trusting

Windows asks the user before trusting a root. `security add-trusted-cert`
doesn't: from a process with an unlocked login keychain, which is normal
for an app the user started, it changes trust settings with no prompt.

So VioLens asks itself. Before running `security add-trusted-cert`, the
app shows a dialog that says what's about to happen (installing a root CA
that can decrypt this account's HTTPS traffic), and nothing is created
until the user agrees. Start awaits that dialog
(`Window.ShowDialog<bool>`), so the Start handler is `async`. An earlier
version blocked on it instead
(`ShowDialog<bool>(owner).GetAwaiter().GetResult()`), expecting the dialog
to keep the message loop running. It didn't: the first Mac test froze on
the first click of Start, because the dialog needed the UI thread that was
waiting for it. The certificate install (`CertificateAuthority`'s
constructor, which runs `security` and waits while macOS asks for the
password) then runs on a background thread, so the window keeps drawing,
and Start is disabled until it's done. On Windows, `CertificateAuthority`
is still created on the UI thread so Windows' own dialog appears in front
of the app.

### System proxy registration

`WinInetSystemProxy` is Windows-only (one global WinINET registry
setting). On macOS, `MacOSSystemProxy` runs `networksetup`. macOS has no
single proxy setting: each network service (Wi-Fi, Ethernet and so on)
has its own. So `Enable` and `Disable` go through every service
`networksetup -listallnetworkservices` lists, skipping disabled ones
(marked `*`), rather than guessing which one is active.
`-setwebproxy` and `-setsecurewebproxy` point HTTP and HTTPS at
`127.0.0.1:<port>`, and `-setwebproxystate` and `-setsecurewebproxystate`
turn them off again in `Disable`.

Crash safety works as on Windows. Before the first `Enable` in a run, the
current settings are saved to a JSON file (one entry per network service).
`Disable` restores and deletes it, and if a run never got to `Disable`,
`RecoverFromCrash` restores it on the next launch. Proxy bypass lists
(`networksetup -setproxybypassdomains`) are left alone, unlike Windows'
`<local>` handling: parsing `-getproxybypassdomains` reliably wasn't
something that could be tested without a Mac.
`SystemProxyController` picks the right implementation for the platform,
so the app's code doesn't branch on it.

**Open risk: whether `networksetup` needs admin rights.** Not needing them
on Windows (per-user registry keys, no UAC prompt) is part of why the
per-user keychain was chosen. But a lot of `networksetup` use runs under
`sudo`, and it hasn't been confirmed whether `-setwebproxy` and
`-setsecurewebproxy` work for an ordinary user on their own network
service, or fail or prompt without elevation. If elevation turns out to be
needed, there are two options: accept an admin prompt for the system
proxy, or stop setting it automatically on macOS and document the System
Settings steps instead.

**Testing.** CI runs the tests on `macos-latest`, a real Mac, so tests
could run `security` and `networksetup` for real there. They don't:
changing the runner's keychain and proxy settings as a side effect of
`dotnet test`, with the admin-rights question open, was too risky.
`MacOSCertificateTrustTests` and `MacOSSystemProxyTests` test the
arguments and JSON the code builds, like `WinInetSystemProxyTests` on
Windows. A real integration test, run only on macOS, can follow once the
admin-rights question is answered.

### Linux

Linux follows the macOS pattern: run the platform's own tools, for the
current user only, after VioLens's own confirmation dialog
(`TrustConfirmationWindow`, shared with macOS), since nothing on Linux
asks.

**Trust.** Linux has no single trust store that every app reads, so
`LinuxCertificateTrust` adds the root to the ones browsers use, all per
user and without elevation:

- the shared NSS database, `~/.pki/nssdb`, which Chrome, Chromium, Edge,
  Brave and other NSS apps read. It's created (`certutil -N
  --empty-password`) if it isn't there, which is normal for an account
  that has never run Chrome. The Chromium snap's copy
  (`~/snap/chromium/current/.pki/nssdb`) too, if there is one;
- every Firefox profile with a `cert9.db`, under `~/.mozilla/firefox`, the
  snap's `~/snap/firefox/common/.mozilla/firefox` and the Flatpak's
  `~/.var/app/org.mozilla.firefox/.mozilla/firefox`. A running Firefox may
  not notice until it restarts;
- .NET's per-user root store (`X509Store(Root, CurrentUser)`, a folder
  under `~/.dotnet`), for .NET apps, where it can.

Each NSS change is `certutil -A -d sql:<db> -n <nickname> -t C,, -i
<root.cer>`. The trust flags `C,,` mean "a CA trusted to issue server
certificates" and nothing else (not email or code signing). The nickname
is the root's name prefix plus its SHA-1 thumbprint, so removal can find
this root, or every VioLens root from earlier installs, by listing
nicknames (`certutil -L`) and deleting the matches (`certutil -D`).
`certutil` comes from the distribution's NSS tools package
(`libnss3-tools`, `nss-tools` or `nss`), which isn't always installed;
without it, Start fails with a message naming the package. A database that
can't be changed is logged and skipped, and Start fails only if none of
them took the root.

The system store (`/usr/local/share/ca-certificates` with
`update-ca-certificates`, or `/etc/pki/ca-trust/source/anchors` with
`update-ca-trust`) is left alone. It needs root, differs between
distributions, and would make the root trusted for every account on the
computer, which the Windows and macOS designs avoid. The User Guide has
the commands for anyone who wants curl and other command-line tools to
trust it.

**The key.** The root and its key are kept in .NET's per-user `My` store,
as on the other platforms. On Linux that's a PKCS#12 file under
`~/.dotnet/corefx/cryptography/x509stores/my`, readable only by the
account. There's no OS key store to enforce the non-exportable flag.

**System proxy.** On Linux this is a desktop setting, not an OS one.
`LinuxSystemProxy` sets GNOME's (`org.gnome.system.proxy`, with
`gsettings`, which Cinnamon, Budgie, Pantheon and others also use) and KDE
Plasma's (`kioslaverc`'s Proxy Settings, with `kwriteconfig6` or
`kwriteconfig5`, then a D-Bus signal so running KDE apps reread it),
whichever are there. The previous values are saved exactly as the tools
print them to a JSON file, and written back by `Disable`, or by
`RecoverFromCrash` on the next launch, as on Windows and macOS. Other
desktops get a message saying to set the proxy by hand. Upstream proxy
detection reads the same settings (manual, a PAC script, or discovery),
then falls back to `https_proxy` and `http_proxy` in VioLens's
environment.

**Testing.** `ci.yml` runs every test on `ubuntu-latest`, and
`release-linux.yml` checks the AppImage and tarball, including a launch
under Xvfb. As on macOS, the tests stop at building arguments and parsing
output (`LinuxCertificateTrustTests`, `LinuxSystemProxyTests`); nothing
runs `certutil`, `gsettings` or `kwriteconfig` for real. It has had a
hands-on test on Ubuntu (GNOME); one on KDE Plasma is still to come.

## Open risks

- Linux trust, removal and the system proxy have been tested by hand on
  Ubuntu (GNOME), but not on KDE Plasma (see "Linux"), so KDE Plasma 6's
  `kwriteconfig6` is still unverified.
- macOS keychain trust, removal and the system proxy have only been
  partly tested on a real Mac (see "Platform status"), including whether
  `networksetup` needs admin rights and whether the root's key can be
  extracted from the keychain.
- Leaf key containers are no longer a risk (see "The ephemeral-key
  problem"), and root removal is built (see "Removal").

SECURITY.md summarizes how the root, the listener and the proxy host are
protected.
