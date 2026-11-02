# Violens.LegacyExtensionHost.CompatShim

Builds `Violens.CompatShim.dll`, an unsigned .NET Framework 4.8 assembly
with the Fiddler Classic API members that real compiled extensions were
found to use. Its name isn't `Fiddler`, so extensions compiled against
Fiddler's own assembly don't bind to it unmodified: they need recompiling
or retargeting first (see the sibling project's `AssemblyMismatch`).

## Why not `Fiddler`

Project tenet 5, "Don't get sued" (see the Project Plan). The assembly was
first named `Fiddler`, so compiled extensions would bind to it as they
are, then `Violens.Fiddler`, and is now `Violens.CompatShim`. An assembly
presenting itself to the runtime as Fiddler's, however carefully its
members were reproduced, was too much trademark exposure to ship. The
cost is that the five extensions tested here no longer load as they are;
each needs recompiling, or its reference to `Fiddler` changing, which is a
metadata edit that doesn't touch its logic. The design doc's "Naming:
Don't get sued" section has the full reasoning.

## Where the members come from

From reading the metadata of five compiled Fiddler Classic extensions
(`AustralianImages`, `ContentBlock`, `JSFormat`, `SAZClipboard`, `Differ`):
type, method and field names and their parameter and return types, from
the `AssemblyRef`, `TypeRef` and `MemberRef` tables and signature blobs
(ECMA-335 Partition II). No IL, decompiled code or source was read. Each
file's comments say which extension uses which member.

The signatures are real, so those extensions' calls bind. What some
members do is VioLens's own guess (what `CONFIG.GetPath` returns, what
`frmViewer.actDoCompareSessions` shows, the values of `SessionStates`), and
each guess is marked in its file. The claim is that these extensions load
and run without throwing, not that they behave exactly as in Fiddler.

## Gaps

- Only `IAutoTamper` is connected to VioLens's live traffic (see the host's
  README). Inspectors, import and export, and exec actions use placeholder
  behavior; none of the five extensions use them.
- `frmViewer`'s own session list holds only two demo sessions:
  `GetSelectedSessions()` returns those, even while live traffic goes
  through the same extension's `IAutoTamper` hooks.
- `Session.utilDecodeResponse()` reports success without decoding
  anything.
- `ClientChatter.FailSession(...)` does nothing: the bridge has no way to
  fail a session.
- Through the bridge, the HTTP version can't be changed, and a repeated
  header (several `Set-Cookie`s) comes back as the last one.
- `frmViewer.actDoCompareSessions` shows a placeholder message, not a
  comparison.
- `Utilities.ReadSessionArchive` and `WriteSessionArchive` handle plain
  `.saz` files (`SazArchive.cs`). Encrypted ones (`CONFIG.bUseAESForSAZ`,
  or a password) throw `NotSupportedException` rather than reading nothing
  or writing an unprotected file.
- Only what these five extensions use is here. An extension that uses
  anything else fails to load, with a logged error, until that member is
  added the same way: confirmed from metadata first, then implemented.
