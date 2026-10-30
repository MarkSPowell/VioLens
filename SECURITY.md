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

## How VioLens protects you

Reviewed before 1.0 (October 2026). What's in place, and what isn't:

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
- **Tools > Remove Root Certificate**, and the Windows uninstaller, remove
  it and its key.
- On macOS, whether the keychain marks the key non-extractable still needs
  checking on a real Mac.

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

**Listening.**
- VioLens listens on 127.0.0.1 only, unless you turn on **Allow remote
  computers to connect**. That choice is remembered between runs, so turn
  it off when you've finished.
- While remote computers are allowed, anyone on your network can use the
  proxy. There's no proxy password.
- Other devices can't use VioLens to reach this computer's own localhost
  services: they get a 403 instead. A name that resolves to a local
  address one moment and another address the next (DNS rebinding) is a
  narrow gap that remains.
- A connection that doesn't send its request within 2 minutes is closed.

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

## Supported versions

VioLens is in preview: fixes go into the next release, and older previews
aren't patched. Please check the problem is still there in the
[latest release](https://github.com/MarkSPowell/VioLens/releases/latest).
