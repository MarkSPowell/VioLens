# Security policy

VioLens decrypts HTTPS traffic with a root certificate it creates on your
own computer, so security problems matter here more than in most apps. If
you find one, thank you for reporting it privately first.

## Reporting a vulnerability

Please **don't open a public issue** for a security problem. Instead,
either:

- use GitHub's private reporting: the **Report a vulnerability** button on
  the repository's [Security tab](https://github.com/MarkSPowell/VioLens/security), or
- email **security@violens.dev**.

Include what you found, the steps to reproduce it, and the version of
VioLens and the operating system you used. You'll get a reply within a
week, usually sooner. Once a fix is released, the release notes credit you
unless you'd rather not be named.

## What counts

For example:

- anything that exposes or misuses the root certificate's private key, or
  lets another program or user on the computer use it
- VioLens trusting a server certificate it shouldn't, or weakening the TLS
  connection to the real server
- the proxy accepting connections from other computers when it shouldn't
- an extension, script or file (SAZ, HAR) that can run code it shouldn't
  when opened
- the proxy host (`Violens.ProxyHost`) accepting commands from anything
  other than the app that started it, or the app accepting `violens-exec`
  commands from another account
- the app sending sessions to a legacy extension host run by another
  account

## How VioLens protects you

Reviewed before 1.0 (October 2026, and again in November 2026). What's in
place, and what isn't:

**The root certificate.**
- Each computer and user account gets its own root, made on that computer.
  VioLens never ships a shared root, so a root taken from one computer is
  no use against another.
- The root is ECDSA P-256, valid for 5 years, and can only sign server
  certificates directly (path length 0).
- Its private key is stored without the Exportable flag in your own
  certificate store, so the certificate APIs won't hand it out. It's
  trusted for your account only.
- It's still a key in your account: a program running as you can ask the
  OS to sign with it, as with any per-user key. Other accounts can't.
  That includes any program built on ProxyCore (which anyone can download
  from NuGet) that asks for VioLens's root by name: it gets a root your
  browsers already trust, with no prompt. A program running as you could
  already read your browsers' data, so this isn't a new way in, but it's a
  reason to remove the root (below) when you've finished debugging HTTPS.
  VioLens Capture does that for you when it closes.
- VioLens only picks up a root from your store that's shaped like one it
  made (self-signed, path length 0, an ECDSA P-256 key), not any
  certificate that shares the name. A program running as you could still
  make one that passes, so this guards against mistakes, not that program.
- **Tools > Remove Root Certificate**, and the Windows uninstaller, remove
  it and its key (on Linux, from every browser database VioLens added it
  to).
- On macOS, whether the keychain marks the key non-extractable still needs
  checking on a real Mac.
- On Linux there's no OS key store: .NET keeps the key in a file in your
  home folder (under `~/.dotnet`) that only your account can read, and
  the non-exportable flag isn't enforced. The root is trusted in your
  browsers' certificate databases, for your account only, never in the
  system's store.

**Per-site certificates.** VioLens signs a certificate for each site,
valid for 7 days. Their keys are never saved to disk for good: each is
deleted when its certificate is released.

**Real servers.** VioLens checks each server's certificate the normal way
and doesn't hide a failure. It doesn't turn certificate checks off. VioLens
sends no client certificate unless you choose one. Once you do, it goes to
every server that asks for one, unless you list the hosts it's for.

**violens-exec.** Scripts drive VioLens through a named pipe that only
your own account can open; nothing listens on the network. A program
running as you could use it to start or stop capturing, or to save and
open capture files, though such a program could already do more than
that. **Tools > Accept Commands from violens-exec** turns it off.

**Update check.** At start-up VioLens asks GitHub's public releases list
whether a newer version is out; you can turn it off in the Help menu. It
opens only release pages it builds itself on github.com, and never
downloads or runs anything.

**Captured content.** Responses are shown as data, never run:
- The Markdown tab shows links and images without following or fetching
  them.
- The WebView tab's **Preview in Browser** writes a copy of the page to
  the temp folder with a Content-Security-Policy that blocks scripts,
  frames, plug-ins and form submission, and with the page's own
  `http-equiv` settings, including any automatic redirect, removed. Nothing loads from the network unless you check **Load images
  and styles from the site**. Copies are deleted after a day.
- Files you open (SAZ, HAR, packet captures) and bodies the inspectors
  read (JSON, CBOR, MessagePack, Markdown, images) are treated as
  hostile: sizes are checked before anything is allocated, nesting is
  limited, and the Markdown reader limits how far it searches so crafted
  text can't stall the window. A compressed body is decoded only up to
  256 MB, so a small one that would expand without end is shown raw.

**Machine policy.** Locked settings come from places only an
administrator can write (`HKLM\SOFTWARE\Policies\VioLens`, or
`policy.json` under ProgramData, `/Library/Application Support` or
`/etc`). A policy that can't be read is skipped, so VioLens falls back to
the user's own settings rather than refusing to start; an administrator
relying on a lock should make sure the file is valid.

**VioLens Capture.** It records only: no breakpoints, rules, scripts or
extensions run. Unless you check **Include secure (HTTPS) websites** it
trusts nothing, and when it does, closing it removes the certificate
again unless you uncheck that. It shares VioLens's certificate.

**Listening.**
- VioLens listens on 127.0.0.1 only, unless you turn on **Allow remote
  computers to connect**. That choice is remembered between runs, so turn
  it off when you've finished.
- While remote computers are allowed, anyone on your network can use the
  proxy. There's no proxy password.
- Other devices never get your identity: VioLens doesn't sign in to your
  network's proxy for them (with your saved password or your Windows
  account), and doesn't send your client certificate on their
  connections. A proxy that asks them to sign in gets its 407 back to
  them, as it would if they used it directly.
- A proxy password from Tools > Connections is never sent in plain text
  (Basic) without asking you first, once per proxy. VioLens can't tell
  which proxies are your network's own: one named by an automatic
  configuration script could belong to anyone on the network.
- Other devices can't use VioLens to reach this computer's own localhost
  services, or link-local addresses such as a cloud server's metadata
  service (`169.254.169.254`): they get a 403 instead. A name that
  resolves to one of those one moment and another address the next (DNS
  rebinding) is a narrow gap that remains.
- Everything else this computer can reach, they can reach too. That
  includes sites served from this computer's own network address (which
  is how a phone tests them), and any network it's on through a VPN, such
  as your company's intranet. Don't allow remote computers on a network
  you don't trust.
- A connection that doesn't send its request within 2 minutes is closed.
- Proxy auto-config scripts, which can come from the network through
  automatic detection, run in a JavaScript interpreter with no access to
  .NET and limits on time, recursion and memory.

**The proxy host (`Violens.ProxyHost`).**
- The app makes a pipe with a random name that takes one connection, and
  starts the host with a 128-bit token from the cryptographic random
  number generator.
- The host refuses a pipe made by another account. The app refuses a host
  that doesn't send the token back.
- The host stops capturing, restores the system proxy and exits when the
  app exits.
- Not yet covered: the app's pipe uses the operating system's default
  permissions. On Windows, another account could connect to it first and
  make the start-up time out. It can't send commands or read anything.

**The legacy extension host (optional, Windows).** The app sends each
session to it over a named pipe that only your account can open, and
never from another computer. The app refuses a pipe of that name made by
another account, so another user on a shared computer can't stand in for
the host and read your decrypted traffic.

## Supported versions

Fixes go into the next release; older releases and release candidates
aren't patched. Please check the problem is still there in the
[latest release](https://github.com/MarkSPowell/VioLens/releases/latest).
