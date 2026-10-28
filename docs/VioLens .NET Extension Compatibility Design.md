# VioLens .NET Extension Compatibility Design

Detailed design for the `Violens.Compatibility.Extensions` namespace,
sibling to `Violens.Compatibility.FiddlerScript` (see that design doc).
Read the Project Plan's "Fiddler Classic compatibility review" section
first for the context (the need to port existing extensions, the decision to
build FiddlerScript first and defer compiled extensions) and this doc's
own "Decision: FiddlerScript first" paragraph, which this work picks back
up.

Built from public documentation only —
[fiddlerbook.com/fiddler/dev/IFiddlerExtension.asp](https://fiddlerbook.com/fiddler/dev/IFiddlerExtension.asp),
fetched directly for this pass and quoted verbatim below where it matters
— consistent with this project's clean-room policy. No implementation code
is reproduced from any compiled extension's decompiled IL anywhere in this
codebase.

## Binary compatibility vs. source compatibility — the central finding

This project has a copy of a real, closed-source, compiled Fiddler
Classic add-on (`SAZClipboard.dll`, in `Local Resources/`) on hand. Rather
than decompile it — which would cross the clean-room line, since no public
source was known to exist for it at the time — it was inspected with
plain string/PE-metadata tools (`file`, `strings`, a raw PE-header read):
confirmed to be a real .NET Framework 2.0 assembly that references
`Fiddler`, `FiddlerApplication`, and `IFiddlerExtension` directly. That's
metadata inspection, not decompilation — no method body/IL was ever
disassembled or read.

That one fact settles an important question: **an existing compiled
Fiddler Classic extension .dll cannot be loaded by a VioLens-defined
interface, no matter how exactly its member shape matches Fiddler's own.**
.NET requires the exact same assembly-qualified type identity (namespace +
name + defining assembly) for a class to be recognized as implementing a
given interface — a structurally-identical-but-separately-defined
`Violens.Compatibility.Extensions.IFiddlerExtension` is not the same
type as `Fiddler.IFiddlerExtension`, and reflection-based discovery will
never bind the two. This isn't a research gap further docs would resolve;
it's how the CLR works.

This project's own interfaces (below) are therefore **source-level
compatible, not binary-level compatible**: an engineer with an existing
extension's *source* can port it here with a new `using`/base-interface
and a recompile — matching the goal of porting existing extensions with
minimal effort (which presupposes having the source) — but
an already-compiled `.dll` like `SAZClipboard.dll` can't be dropped in
unmodified.

**A theoretical path to real binary compatibility exists and is
explicitly NOT pursued in this pass:** a custom `AssemblyLoadContext`
with a `Resolving` handler could intercept a loaded extension's reference
to the real `Fiddler` assembly and substitute an in-memory, clean-room
shim assembly (same namespace/type names, independently authored from
public docs) that adapts into VioLens's own engine underneath.

**Update — five more real compiled extensions inspected the same way,**
sourced from links off fiddlerbook.com/Fiddler2/extensions.asp
(`AustralianImages.dll`, `ContentBlock.dll`, `JSFormat.dll`,
`SAZClipboard.dll` again, `Differ.dll`), using a hand-rolled, dependency-
free ECMA-335 metadata reader (no `ildasm`/decompiler available in the
sandbox that did this pass; ISO/IEC 23271 table layouts implemented
directly) — same plain-metadata-only method as the original
`SAZClipboard.dll` finding above, no method bodies/IL read on any of
them:

- **Unsigned, five for five.** Every one of the five references `Fiddler`
  with no public key or token in its `AssemblyRef` row — confirms the
  original single-sample finding above wasn't a fluke. This is the one
  fact that makes the shim's assembly-identity substitution
  mechanically plausible at all: a strong-named reference would also
  demand a matching key pair/signature, which isn't recoverable without
  Fiddler's own private key.
- **Version spread: `2.1.0.5` through `2.4.2.5`.** A shim targeting one
  fixed version wouldn't necessarily satisfy all of these — though
  because the reference is unsigned/weak-named, the .NET Framework's own
  binder does not enforce exact-version matching for weak-named
  assemblies the way it does for strong-named ones, so this is a smaller
  problem than the raw version spread suggests. Worth confirming against
  an actual loaded extension before relying on it, not just asserting
  from documented binder behavior.
- **API surface actually referenced, union across all five:**
  `IFiddlerExtension`, `IAutoTamper`, `IHandleExecAction`,
  `FiddlerApplication`, `Session`, `ClientChatter`/`ServerChatter`,
  `HTTPRequestHeaders`/`HTTPResponseHeaders`/`HTTPHeaders`, `CONFIG`,
  `Utilities`, `IFiddlerPreferences`, `HostList`, `Logger`,
  `CodeDescription`, `BasicAnalysis`, `SessionStates`,
  `RequiredVersionAttribute`, and two WinForms UI types — `frmViewer`,
  `frmPrompt` — extensions reach directly into Fiddler's own UI forms,
  not just its data model. Knowing the *types* referenced doesn't mean
  knowing the member-level shape (method signatures, fields) each one
  needs; that's a materially deeper research step (reading `MemberRef`
  signature blobs, still metadata-only and still clean-room-safe, but
  not done in this pass) and would be needed before any adapter code
  could actually be written, regardless of the policy question below.
- **`frmViewer`/`frmPrompt` specifically: a separate, harder problem
  from the rest of the shim, worth tracking on its own.** `frmViewer` is
  referenced by all five samples — including the trivially simple
  `AustralianImages` demo — and `frmPrompt` by two of five
  (`ContentBlock`, `SAZClipboard`). That's not an edge case; it looks
  like most real extensions touch at least one of these. Checked both
  fiddlerbook.com's `IFiddlerExtension.asp` dev docs and Telerik's
  current "Create Extension project" page for either name: **neither
  documents them.** They're Fiddler Classic's own internal WinForms UI
  classes, reachable by old extensions only because Fiddler.exe was one
  monolithic assembly with these classes public — not because they were
  ever a supported, documented extension point. That leaves two
  compounding problems on top of the trademark/naming question already
  raised above: (1) no public spec for what they actually do, only
  whatever their compiled method signatures turn out to be once someone
  reads them (not done yet); (2) they're WinForms `Form` subclasses,
  while VioLens's own UI is Avalonia — "equivalent functionality"
  means either running real `System.Windows.Forms` alongside Avalonia in
  the same process (technically possible on .NET on Windows, but two
  independent UI-framework message loops sharing a process is a real
  stability risk, and it's Windows-only, cutting against this project's
  own cross-platform direction) or hand-building Avalonia-based stand-ins
  for undocumented behavior — a full reimplementation, not an adapter.
  **Current honest answer: no, VioLens cannot give these extensions
  equivalent UI functionality today, and this may be a harder blocker
  than the naming question.**
- **New risk, not previously in this doc: all five are genuine .NET
  Framework binaries (CLR metadata version `v2.0.50727`).** Whether an
  old Framework 2.0/3.5-era assembly — especially the ones referencing
  `System.Windows.Forms`/`frmViewer`/`frmPrompt` — loads and runs cleanly
  inside a .NET 10 `AssemblyLoadContext` at all is a separate, untested
  question from assembly-identity binding, and adds real risk on top of
  it.

None of this changes the actual blocker, though: it strengthens the case
that the shim is *technically* buildable, but it raises no new
information about the **non-technical** question already flagged
above — trademark use of the `Fiddler` name, and whether shipping
something old binaries bind to unmodified reads as interoperability or
as redistributing Fiddler's own surface. This is still left as a flagged
future option, not a plan: it raises real questions this project isn't
positioned to resolve on its own, specifically for *this* approach (an
assembly literally named `Fiddler` that old binaries bind to
unmodified). (See "Update — Fiddler's own licensing history, and what
changed in August 2026" near the end of the Phase 1 section below: new
information that raises this question's stakes. Superseded in turn by
"Update — naming question closed: the second rename," further below: the
project has since dropped `Fiddler` from the shim's own identity entirely
and closed this question.)

## `frmViewer`/`frmPrompt` member-level findings — and a hard, non-VioLens-specific wall

Decided to pursue a technical solution here regardless of the trademark
question above (see "Update — naming question closed: the second rename,"
further below, for how that question was settled). Extended the metadata
reader to decode `MemberRef` signature
blobs (ECMA-335 II.23.2.1/.4: method/field name plus parameter and return
*types* — still no IL method bodies read) for every member these five
samples actually call on `frmViewer`/`frmPrompt`. Two findings change the
plan:

**`frmViewer` is Fiddler Classic's actual main window, not a popup
viewer, and extensions reach directly into its live control tree** —
this is not indirection through a documented API, it's raw field access
to the same menu bar, context menu, and status bar the end user sees:

- `AustralianImages`: field `mnuTools` (`System.Windows.Forms.MenuItem`)
- `ContentBlock`: `GetSelectedSessions()` → `Session[]`; fields
  `sbpInfo` (`StatusBarPanel`), `mnuMain` (`MainMenu`),
  `mnuSessionContext` (`ContextMenu`); plus `frmPrompt.GetUserString(
  string title, string prompt, string default, bool)` → `string` (a
  simple static input-dialog helper — much more tractable than the rest)
- `JSFormat`: fields `mnuRules` (`MenuItem`), `mnuSessionContext`
  (`ContextMenu`), `sbpInfo` (`StatusBarPanel`); `GetSelectedSessions()`
- `SAZClipboard`: field `mnuTools` (`MenuItem`);
  `frmPrompt.GetUserString(...)`
- `Differ`: field `tabsViews` (`TabControl`); `actDoCompareSessions(
  Session, Session)`

**The wall: `MenuItem`, `MainMenu`, `ContextMenu`, and `StatusBarPanel`
were removed outright from WinForms when it was ported to .NET Core/.NET
5+ — not deprecated, gone.** Confirmed via Microsoft's own WinForms
breaking-change docs
([winforms-deprecated-controls](https://learn.microsoft.com/en-us/dotnet/core/compatibility/windows-forms/5.0/winforms-deprecated-controls),
`StatusBar`/`StatusBarPanel` explicitly listed as removed, replaced by
`StatusStrip`/`ToolStripStatusLabel`) and a real user's
`TypeLoadException: Could not load type 'System.Windows.Forms.ContextMenu'`
on .NET Core 3.1
([dotnet/winforms#2614](https://github.com/dotnet/winforms/issues/2614)).
`MainMenu`/`MenuItem` were dropped from the same old pre-`*Strip` menu
family and replaced by `MenuStrip`/`ToolStripMenuItem`.

**This means "add real WinForms to VioLens" does not, by itself, get
these four extensions running unmodified — on any .NET 5+ runtime,
VioLens or otherwise.** The compiled IL in `AustralianImages`,
`ContentBlock`, `JSFormat`, and `SAZClipboard` hard-references BCL types
that no longer exist anywhere in modern .NET's `System.Windows.Forms`.
Loading them would throw `TypeLoadException` on the missing BCL type
itself, before VioLens's own `Fiddler`-identity shim question is even
reached. The only runtime that still has these types is classic .NET
Framework (2.0–4.8) — meaning the only way to run this exact unmodified
IL is inside an actual .NET Framework process, not "WinForms support"
in any .NET 5+ process, VioLens included.

`Differ` is the one exception in this sample set: `TabControl` is a
current, fully-supported control, and `actDoCompareSessions` is
Fiddler's own method (a design question for VioLens's shim, not a
missing-BCL-type problem) — so it isn't blocked by this wall the way the
other four are.

**Real options from here, not yet decided:**

1. **Out-of-process legacy host.** A separate helper process built
   against actual .NET Framework (still present on Windows 10/11),
   hosting these extensions for real against a legacy-shaped surface,
   bridged to VioLens over IPC (proxied traffic in, mutations/UI
   actions out). Gets real equivalent functionality for all five
   samples, including the four blocked above, but is a materially larger
   architecture than anything scoped in this doc so far — closer to
   "build and maintain a second, legacy runtime component" than "add a
   shim assembly."
2. **Scope binary compatibility to non-UI extensions only.** Extensions
   that stick to `IAutoTamper`/`Session`/headers/etc. (the documented,
   data-oriented interfaces this doc's interfaces already cover
   source-level) are unaffected by this wall. The ones reaching into
   `frmViewer`'s live control tree — empirically, most of this sample
   batch — are excluded from true binary compat, full stop, regardless
   of WinForms hosting.
3. **`frmPrompt.GetUserString` alone, as a smaller, real win.** It's a
   simple static method (`(string, string, string, bool) -> string`),
   not a control-tree dependency — genuinely adaptable as an Avalonia
   dialog behind a same-named/same-signature shim method, independent of
   the harder `frmViewer` question.

None of this resolves the trademark/naming question above either — it's
additive. At the time this was written, whichever of the three (or a mix)
got pursued was thought to still need the `Fiddler`-identity question
settled before shipping; see "Update — naming question
closed: the second rename," further below, for why that's no longer the
gate.

## Sizing option 1 (the out-of-process legacy host) — and why converting the whole app to WinForms doesn't help

**Correction to a natural-seeming idea, checked before estimating
anything:** switching VioLens's own UI toolkit from Avalonia to
WinForms would not bring `MenuItem`/`MainMenu`/`ContextMenu`/
`StatusBarPanel` back. Their removal is a **.NET 5+ BCL/CLR fact, not an
Avalonia-vs-WinForms one** — WinForms running on .NET 10 is missing
these types exactly as much as Avalonia is, because they were deleted
from `System.Windows.Forms` itself when it was ported off .NET
Framework, for every host app regardless of UI framework (see the
finding above). The only runtime that still has them is actual .NET
Framework (net48) — so the only way "convert to WinForms" would help is
if paired with also retargeting the whole app to classic .NET Framework,
not just swapping UI toolkits while staying on .NET 10.

That's a much bigger and different decision than a UI-framework choice,
and it cuts directly against this project's own foundations: Jint was
deliberately chosen over JScript.NET specifically *because* JScript.NET
never came forward past .NET Framework (see the FiddlerScript design
doc) — VioLens exists as a modern, actively-maintained alternative,
and .NET Framework has no future upstream support and no macOS/Linux
story at all, ever. It would also make "a separate macOS client" stop
being a nice-to-have and become mandatory from day one: Avalonia's whole
value to this project is one UI codebase running cross-platform already;
WinForms has zero macOS path, so choosing it for the primary client
means starting a second, unrelated UI codebase for macOS immediately,
not deferring it. For reference, `apps/Violens.DesktopUi` (the only
Avalonia-specific project — everything in `src/` is already UI-agnostic
backend code untouched by this choice either way) is about 3,100 lines
across 19 files: not a large rewrite by itself, but a rewrite that buys
nothing toward running these five extensions and actively costs the
cross-platform architecture. **Recommendation: don't pursue a full
WinForms conversion of the main app** — it doesn't solve the stated
problem and forces a permanent architecture cost for a benefit that
doesn't materialize.

**The out-of-process legacy host, sized instead** — the one option that
actually gets real equivalent functionality, because it's the one place
an actual .NET Framework process (which genuinely still has these types)
does the work, while VioLens's main app stays on .NET 10/Avalonia/
cross-platform, untouched:

- **A new, isolated project** (e.g. `tools/Violens.LegacyExtensionHost`),
  targeting `net48`, referencing the real `System.Windows.Forms` that
  ships with .NET Framework (already present on Windows 10/11 — no extra
  runtime install for end users, just a second build target in CI).
- **A real compatibility assembly named `Fiddler`** (unsigned, per the
  strong-naming finding above) implementing actual, working adapter
  classes for the member surface found so far —
  `frmViewer.mnuTools`/`mnuMain`/`mnuSessionContext`/`sbpInfo`/
  `tabsViews` as real `MenuItem`/`MainMenu`/`ContextMenu`/
  `StatusBarPanel`/`TabControl` instances (genuinely available here,
  unlike in the main app), plus `GetSelectedSessions()`,
  `actDoCompareSessions(Session, Session)`, and `frmPrompt.GetUserString`.
  This still carries the same trademark/naming question as the original
  shim idea — moving it into a small, optional, clearly-separate legacy
  helper doesn't, on its own, remove the question, though it may be an
  easier ask than the same identity trick living in the main app. (What
  the project actually shipped, per "Strategy 5" and the second rename
  below, is different from a real assembly literally named `Fiddler`: see
  "Update — naming question closed: the second rename," further down.)
- **A session/data bridge to the main process.** The legacy host doesn't
  proxy traffic itself — it needs VioLens's real captured sessions
  mirrored into it, and any extension-side mutations relayed back. Needs
  an IPC transport (named pipes are the natural Windows-local choice)
  and a serialization contract for `Session`/headers — new surface, not
  reuse of anything existing. **Built** — see "The legacy host's session
  bridge," below the "Don't get sued" strategy write-up.
- **A UI presentation decision, unresolved:** a fully separate legacy
  window (simpler, real, but a second visible window rather than
  integrated menus/tabs) versus reparenting the legacy process's HWND
  into VioLens's own window (visually seamless, but cross-process
  window reparenting is fragile — DPI, focus, and message routing all
  get harder across a process boundary). Not decided here; the separate-
  window version is the realistic near-term target.
- **Process lifecycle and packaging.** Starting/stopping/crash-recovering
  a second process, and a second thing to build, sign, and ship — a real,
  ongoing addition to the release pipeline (`.github/workflows/
  release-windows.yml`, the Inno Setup script) on top of what exists
  today, and Windows-only by nature (no pretense of cross-platform for
  this piece specifically, which is fine as long as it's optional and
  doesn't drag the main app down with it).

**Honest scope comparison, no fabricated calendar estimate:** every
phase of the compiled-extension work built so far in this doc (the five
interfaces, `ExtensionHost`, `AssemblyLoadContext` isolation, the
inspector/import/export adapters, end-to-end validation) was already a
substantial multi-session effort. This is comparable to or larger than
all of that combined — a second runtime, a new IPC protocol, and a real
(not source-level) reimplementation of `frmViewer`'s control-tree
semantics — plus, at the time this was written, the open naming question
above. Reasonable to scope as its own optional, separately
shipped component (not bundled into the main installer) rather than a
core-app feature, so it can be built, tested, and revisited independent
of the main release cadence.

## Phase 1 — built: `tools/Violens.LegacyExtensionHost/`

Decided to proceed building the out-of-process legacy host (see "Decided
to pursue a technical solution" above, same standing decision; the naming
question was later settled by "Update — naming question closed: the
second rename," further below). A standalone solution, deliberately **not**
referenced by
`VioLens.sln` or `apps/Violens.DesktopUi`, so it can't affect the main
app's build and can be deleted entirely with no trace if it needs to be
backed out:

- **`Violens.LegacyExtensionHost.CompatShim`** — builds `Fiddler.dll`
  (net48, unsigned, `AssemblyName=Fiddler`). Implements every type and
  member this doc's metadata research confirmed the five real samples
  actually call: `IFiddlerExtension`/`IAutoTamper`/`IHandleExecAction`
  (public-doc signatures, same source as the main app's source-compatible
  interfaces), `Session`/`ClientChatter`/`ServerChatter`/`HTTPHeaders`
  family/`SessionStates`, `FiddlerApplication`/`IFiddlerPreferences`/
  `CONFIG`/`HostList`/`Utilities`/`Logger`/`CodeDescription`/
  `BasicAnalysis`, and — the piece this phase exists for —
  `frmViewer`/`frmPrompt` as real WinForms types with real
  `MenuItem`/`MainMenu`/`ContextMenu`/`StatusBarPanel`/`TabControl`
  fields. Every member's signature traces to a specific decoded
  `MemberRef` from a specific sample (recorded in each file's own
  remarks); the *behavior* behind less-central members (`CONFIG.GetPath`,
  `Utilities.ReadSessionArchive`, `actDoCompareSessions`) is this
  project's own reasonable placeholder, flagged individually, not
  confirmed against real Fiddler behavior. See that project's own README
  for the full "what this does and doesn't guarantee" breakdown.
- **`Violens.LegacyExtensionHost`** — the net48 WinForms host process
  itself. `Program.cs` constructs the real `frmViewer`, assigns it to
  `FiddlerApplication.UI` before anything loads (extensions' `OnLoad()`
  commonly hook the host's menu immediately), then runs
  `LegacyExtensionLoader` — a net48 counterpart of
  `Violens.Compatibility.Extensions.ExtensionHost`, deliberately mirroring
  its scan/gate/register shape for consistency, adapted for .NET
  Framework's lack of `AssemblyLoadContext`: plain `Assembly.LoadFrom`
  into the process's one AppDomain rather than one isolated load context
  per extension, since isolation is already achieved a level up (a bad
  legacy extension can only take down this sacrificial helper process,
  never VioLens's main app) — a deliberate, flagged scope cut, not an
  oversight.

**Explicitly not built this phase** (see each project's own README for
the full list): the IPC bridge to VioLens's real proxied traffic —
`Session` instances are synthetic demo data only; any launch integration
from the main app; the HWND-reparenting question (still a fully separate
window); a real diff view behind `actDoCompareSessions`; real `.saz`
archive read/write. This phase's claim is narrower and checkable on its
own: real compiled extensions using the confirmed member surface should
discover, gate, load, and run their `OnLoad()` hooks against real
WinForms controls that don't exist anywhere in VioLens's own .NET 10
process.

**Validated: builds clean.** `dotnet build Violens.LegacyExtensionHost.sln`
succeeded on the first real attempt on a real machine — both projects
compiled with no errors, producing `Fiddler.dll` and
`Violens.LegacyExtensionHost.exe`. This environment still has no local
`dotnet`/Windows toolchain of its own, so everything here was written
directly from the confirmed metadata and existing project conventions,
checked only for brace/paren balance and XML well-formedness before this
result came back — a genuine (if limited) confirmation that reasoning
from decoded `MemberRef` signatures rather than guessing produces code
that actually compiles.

**Validated: a real extension loads and touches a real WinForms control at
runtime.** Running the built `.exe` against two DLLs dropped into
`%USERPROFILE%\Documents\VioLens\LegacyExtensions\` produced (via the
on-screen log tab added to `frmViewer` after the first console-only pass
proved invisible without a debugger attached):
- `Violens.SampleExtension.dll` — a **correct rejection**: failed with
  `Could not load file or assembly 'System.Runtime, Version=10.0.0.0, ...'`.
  That version number gives it away — this is VioLens's own net10.0
  source-compatible sample extension (see
  `Violens.Compatibility.Extensions`), dropped into the wrong folder.
  `LegacyExtensionLoader` correctly refused to load a modern .NET
  assembly into a net48 process; this is exactly the folder-separation
  behavior the design called for (`LegacyExtensions`, not `Extensions`),
  working as intended rather than a bug.
- The second DLL, `SAZClipboard.dll` — a real compiled Fiddler Classic
  extension: loaded with **no errors**, and after `OnLoad()` ran, `mnuTools`
  — the live, real `System.Windows.Forms.MenuItem` field this whole
  legacy-host effort exists to provide — had gained one item. That is the
  actual claim this phase set out to check: a real compiled extension,
  unmodified, reaching into a WinForms control type that does not exist
  anywhere in VioLens's own .NET 10 process, through the `Fiddler`-named
  compat shim, and successfully mutating it. Clicking that menu item opens
  the extension's own window for real, and the "Load SAZ" button in it
  opens a real file picker.

Not yet confirmed: `IAutoTamper`/`IHandleExecAction` hooks actually firing
against real traffic (no traffic flows yet — `Session` instances are still
synthetic demo data), and everything else already listed under "Explicitly
not built this phase" above.

**Update — real `.saz` read/write, and an on-screen log.** Two follow-ups
after the runtime test above: first, `Debug.WriteLine`-only logging turned
out to be a real usability gap (invisible without a debugger or DebugView
attached, which is exactly how the `.exe` gets run in practice) — `frmViewer`
now has a "Log" tab, and both `LegacyExtensionLoader`'s scan/load messages
and anything logged via `FiddlerApplication.Log.LogFormat` (including
extension code) show up there. Second, testing `SAZClipboard.dll` for real
surfaced that `Utilities.ReadSessionArchive`/`WriteSessionArchive` being
no-ops wasn't just an unvalidated placeholder — it visibly broke that
extension's "Load SAZ" feature. Both are now real implementations
(`SazArchive.cs`), built from the `.saz` format's own public documentation
(it's Fiddler's long-published, generic ZIP-based archive format — not
something the clean-room policy around the five extension DLLs applies to).
Plain, unencrypted archives round-trip for real now. Password-protected /
encrypted `.saz` files (`CONFIG.bUseAESForSAZ`, a real member the samples
reference) are explicitly not supported — both methods refuse outright
(`NotSupportedException`) rather than silently reading zero sessions or
writing an unprotected file while claiming success, since hand-rolling ZIP
encryption is its own real scope with its own real risk of getting it
subtly wrong.

**Validated: `SAZClipboard.dll` loads a real, non-trivial `.saz` file end to
end.** With the above in place, Mark pointed `SAZClipboard`'s "Load SAZ" at
a real capture (`violens-capture-20260922-155314.saz`, 87 sessions) and
the Log tab confirmed `ReadSessionArchive: loaded 87 session(s)`, with the
extension's own window/clipboard then showing the loaded content. This is
the strongest evidence so far that the metadata-driven-reconstruction
approach behind this whole shim actually works in practice, not just
against synthetic demo data or a trivial test file — real member surface
(`HTTPRequestHeaders`/`HTTPResponseHeaders`/`Session`/`Utilities`), a real
87-session archive, and a real, unmodified third-party extension DLL all
working together correctly. Mark spot-checked the loaded data afterward:
correct, with body bytes showing raw "line noise" for some sessions — which
matches real Fiddler Classic's own behavior for compressed/encoded bodies
that nothing has decoded yet (this shim's `Utilities.getResponseBodyEncoding`/
`Session.utilDecodeResponse()` are still Phase 1 placeholders, already
flagged above), not a new bug in `SazArchive.cs`.

**Fixed: `AustralianImages.dll` needs a 32-bit host process.** Adding the
remaining four inspected samples turned up one real, extension-specific
compatibility issue: `AustralianImages.dll` failed with
`BadImageFormatException` ("incorrect format"), while `ContentBlock.dll`,
`JSFormat.dll`, and `Differ.dll` all loaded cleanly alongside it (2
`IAutoTamper` + 1 `IHandleExecAction` registered). Inspecting each DLL's own
PE/CLI header directly (COFF `Machine` field and `IMAGE_COR20_HEADER.Flags`
— more basic than the metadata tables this project already reads, and still
nowhere near IL/logic) found the real cause:
`COMIMAGE_FLAGS_32BITREQUIRED` is set on `AustralianImages.dll`
(`cli_flags = 0x3`), while every other sample inspected so far — including
`SAZClipboard.dll`, already confirmed working — is plain AnyCPU
(`cli_flags = 0x1`, `ILONLY` only). `AustralianImages.dll` can only load
into a genuinely 32-bit process; `Violens.LegacyExtensionHost.csproj` had
no `PlatformTarget`, so it was very likely running 64-bit on a 64-bit OS.
Fixed by pinning `<PlatformTarget>x86</PlatformTarget>` on the host `.exe`
project — real Fiddler Classic itself always ran as a 32-bit process
historically (plausibly why an extension like this was ever compiled
32-bit-only in the first place), and AnyCPU assemblies run fine in a 32-bit
process too, so this is strictly more compatible with real Fiddler Classic
extensions than the previous unpinned default, not a narrowing.

**Fixed: `HTTPMethod`/`UriScheme`/`RequestPath`/`HTTPResponseStatus`
defaulted to `null`.** Testing `Differ.dll` for real (its own "Differ" tab,
embedded into `tabsViews` — confirming the field reference noted above —
with a "Load SASZ Files" picker for two archives and a "Compare" button)
threw a real `NullReferenceException` directly inside `Differ.DiffView.
AddSessions`, with no shim-method frames anywhere in the stack — a strong
signal that a plain field/property was null, not a call into this shim
failing outright (a thrown call into `SazArchive`/`Utilities` would show
its own frames; a null field access compiles inline and shows none). Since
reading `Differ`'s own IL to find the exact line is off-limits, the fix
instead closes the gap at its root: `HTTPRequestHeaders.HTTPMethod`,
`.UriScheme`, `.RequestPath`, and `HTTPResponseHeaders.HTTPResponseStatus`
were all uninitialized fields/auto-properties (default `null`) unless
`SazArchive.ParseRawRequest`/`ParseRawResponse` happened to run for that
session. That's not just a `.saz`-loading gap — `frmViewer`'s own synthetic
demo sessions never touched these either, so any extension touching them
against demo data would hit the same crash. Real Fiddler's own session
model wouldn't expose null there for any loaded session (empty string,
worst case), and third-party extensions were written against that
guarantee, so all four now default to `string.Empty`.

**Update — the header-default fix alone didn't resolve it; added targeted
diagnostic logging instead of guessing further.** Same test (Differ's own
embedded "Differ" tab, "Load SASZ Files" for two archives, then "Compare")
still throws the identical `NullReferenceException` at the identical stack
trace after the fix above. Two things checked against real Fiddler's own
public API documentation (not Differ's IL) before going further: the
`HTTPHeaders` indexer's null-on-miss behavior is confirmed correct
(real Fiddler's own docs say the same — "If the header does not exist,
returns null" — so that wasn't a divergence to fix), and `Utilities.
TrimBefore`/`TrimAfter`'s null-input handling isn't documented either way.
Reading `Differ.dll`'s own IL to find the exact null dereference is exactly
the line this project has stayed on the right side of all session, so
instead of guessing at more field defaults blind, `HTTPHeaders`'s indexer
(on a miss), both `Utilities.TrimBefore` overloads and `TrimAfter` (on null
input), and `BasicAnalysis.ComputeBasicStatistics` (on every call) now log
diagnostically via `FiddlerApplication.Log` — visible on the on-screen Log
tab — without changing any actual return value or behavior. The plan: get
Mark to reproduce the crash again, then read back whatever the Log tab
shows right before it, which should narrow this to a specific shim call
sequence without ever touching Differ's own logic.

**Root cause found: a real gap in `Differ.dll` itself, not this shim.** The
diagnostic logging worked on the first try. Reproducing the crash (loading
the same real 87-session archive as both "sides," then "Compare") logged
exactly one line right before the identical `NullReferenceException`, with
no `TrimBefore`/`ComputeBasicStatistics` calls after it:
`HTTPHeaders["Content-Type"]: not found, returning null.` Some session in
that real archive genuinely has no `Content-Type` header (a 304, a
redirect, a HEAD response — all valid, all real), `Differ` reads it via the
indexer, gets `null` back exactly as documented, and dereferences it
without a null check (most plausibly while checking the `bIgnoreImages`
option against the content type). This is not a shim bug to fix: real
Fiddler's own public API documentation confirms this indexer is *supposed*
to return `null` on a miss ("If the header does not exist, returns null" —
already checked and confirmed matching, above), so a real Fiddler
installation handing `Differ.dll` a session with no `Content-Type` would
hit this exact same crash. `Differ` was apparently never tested against a
response missing that header. Synthesizing a fake `Content-Type` to paper
over it would mean misrepresenting what the captured response actually
said — the opposite of what this whole binary-compatibility effort exists
to do — so this is being left as a documented, real limitation of
`Differ.dll` itself, not patched around. Four of the five inspected
extensions (`AustralianImages`, `ContentBlock`, `JSFormat`, `SAZClipboard`)
have no such issue; `Differ` specifically breaks on any session lacking a
`Content-Type`, which real captured traffic will always eventually include
some of.

**Confirmed on a real machine: all five inspected extensions now load
clean.** After the `PlatformTarget=x86` fix, re-running against all five
samples together (`AustralianImages`, `ContentBlock`, `JSFormat`, `Differ`,
`SAZClipboard`) produced zero load errors, 3 `IAutoTamper` + 1
`IHandleExecAction` instances registered, and 2 `mnuTools` + 1 `mnuRules`
menu items added by their `OnLoad()` hooks. This is the headline result for
this whole phase: every real, unmodified, third-party-compiled Fiddler
Classic extension this project has a copy of discovers, gates, loads, and
runs against the `Fiddler`-named compat shim on a real machine — including
the one that specifically needed a 32-bit host process, and the one
(`SAZClipboard`) already confirmed to correctly read a real 87-session
`.saz` archive through it. Phase 1's own claim (see "What this proves right
now" in `Violens.LegacyExtensionHost`'s README) is fully met.

**Tests: locking in "actually run," not just "loaded."** Added
`tools/Violens.LegacyExtensionHost/Violens.LegacyExtensionHost.Tests/`
(xunit, `net48`/`x86` to match the host and shim it exercises; added only
to this sub-project's own `Violens.LegacyExtensionHost.sln`, never the
main `VioLens.sln`, same reason as the host and shim projects
themselves). Two tiers:

- *Always runs, no external dependency:* a full `SazArchive` write-then-read
  round trip built entirely from synthetic data (request/response headers,
  bodies, method, status line, session flags, an encrypted-write refusal,
  an unreadable-archive refusal, a missing-file no-op); a regression test
  locking in `HTTPHeaders`'s indexer's confirmed null-on-miss behavior (the
  documented reason `Differ.dll` crashes on a session genuinely missing
  `Content-Type`, see above); and folder-scanning edge cases (missing/empty
  extensions folder) against `LegacyExtensionLoader` itself.
- *Runs only when the five real extension DLLs are present locally* (looked
  up the same way `Program.cs` already does, `%USERPROFILE%\Documents\VioLens\LegacyExtensions\`
  by default, overridable via a `VIOLENS_LEGACY_EXTENSIONS_DIR`
  environment variable, and skipped as an inert pass, not a failure,
  otherwise, since these are real third-party binaries not part of the
  repo): loading all five produces zero load errors and the exact
  registered counts above (5 `IFiddlerExtension`, 3 `IAutoTamper`, 1
  `IHandleExecAction`); a real `frmViewer`'s `mnuTools`/`mnuRules` actually
  gain the exact 2/1 items their `OnLoad()` hooks are confirmed to add
  (not merely "the type constructed"); no extension's `OnLoad()`/
  `OnBeforeUnload()` logs a "threw:" line; and, the closest thing to a real
  functional check this project's clean-room policy allows, every
  registered `IAutoTamper`'s own hooks (`AutoTamperRequestBefore`/`After`,
  `AutoTamperResponseBefore`/`After`, `OnBeforeReturningError`) are each
  called with a well-formed synthetic session and asserted not to throw,
  entirely through that interface's own public contract, no IL read and no
  reflection into any extension's private controls. An optional test reads
  a real captured `.saz` file when pointed at via a
  `VIOLENS_LEGACY_TEST_SAZ` environment variable, for occasional manual
  runs against real traffic beyond what synthetic data can cover.

Deliberately not attempted: driving `Differ`'s own "Load SAZ Files"/
"Compare" buttons via reflection into its private internal controls, to
reproduce the crash itself end to end. That would mean reaching into
another DLL's own implementation details rather than its public contract,
fragile, and not something this project's own code controls, so the
`HTTPHeaders` regression test above is the actual guard against silently
regressing the confirmed root cause, not a UI-driving test.

**First real payoff: the test suite immediately found a genuine bug.**
Run for real on Mark's machine: 18 of 19 tests passed on the first try —
including every real-DLL-gated one, confirming the earlier "all five load
clean" and exact-count findings above hold outside this project's own
research environment too — but the synthetic `SazArchive` round trip
failed on `fullUrl`: a session flag (`https`, used as `GuessScheme`'s own
hint) set before writing came back as if it had never been set, silently
defaulting the scheme to `http`.

Root cause: `WriteMetadata` wrote each session's `_m.xml` via
`XDocument.Save(stream)`, which emits a UTF-8 byte-order-mark by default;
`ReadEntryText` decoded it back with a plain `Encoding.UTF8.GetString`,
which (unlike a `StreamReader`/`XmlReader` reading a byte stream directly)
doesn't strip a BOM — it decodes those bytes into a literal leading U+FEFF
character instead. That stray character made `ParseMetadata`'s
`XDocument.Parse(string)` throw immediately, caught by its own
already-existing try/catch and merely logged, never propagated — so
**every session flag on every session was silently failing to round-trip
through `.saz`**, not just the one flag this particular test happened to
check. Genuinely invisible without a test asserting on the actual
round-tripped value; a manual smoke test that just eyeballs "did some
sessions come back" would never have caught it.

Fixed at both ends in `SazArchive.cs`: `WriteMetadata` now writes through
an explicit BOM-less `UTF8Encoding` instead of the default
`XDocument.Save(stream)` overload, and `ReadEntryText` now also strips a
leading BOM character defensively on the way in, in case a real
Fiddler-produced `.saz`'s own metadata XML ever has one for unrelated
reasons. Also fixed in this pass: two `xUnit2013` analyzer warnings
(`Assert.Equal(1, collection.Count)` → `Assert.Single(collection)`) the
same build surfaced.

**Also found while scoping this:** a Creative Commons (BY-SA 3.0)
copy of `SAZClipboard`'s actual source
([fiddler.wikidot.com/sazclipboard](https://fiddler.wikidot.com/sazclipboard),
possibly old/out of date) exists. Useful as read-only reference for
understanding real extension behavior — but CC BY-SA's share-alike terms
don't obviously mix with this project's own plain MIT license (Creative
Commons itself advises against using CC licenses for software), so
nothing in this codebase is adapted from it. Worth a licensing decision
from the project owner, not something to route around quietly.

**Considered and rejected: an isolated "Bridge" plugin architecture
(dynamic proxy).** Proposed externally as an alternative to the approach
above: keep a native `Violens.PluginContract` for VioLens's own
extensions, and load legacy Fiddler Classic extensions through a separate
"Legacy Bridge Adapter Host" that intercepts their calls via reflection or
dynamic-proxy generation (`System.Reflection.Emit` / Castle DynamicProxy),
mapping Fiddler-shaped calls onto VioLens's own event model at runtime.
Its pitch was that operating "on metadata and interface definitions via
standard reflection" made VioLens's independence-as-an-interoperability-
tool position stronger than shipping a static compat assembly.

Doesn't hold up, on both the technical and the legal claim:

- *It doesn't avoid the assembly-identity requirement.* For the CLR to
  load a legacy extension `.dll` at all, it must resolve that `.dll`'s own
  `AssemblyRef` for "Fiddler" to something — this happens before any
  proxy/interceptor code gets a chance to run, and there is no way to defer
  or hook that step. Whatever gets built, something identity-named
  "Fiddler" still has to exist for the extension to load, whether it's a
  statically compiled shim (what's built here) or an assembly generated at
  runtime via `Reflection.Emit`. The mechanism changes; the underlying
  trademark/naming question this project had to work through does not.
- *Dynamic proxying can't cover the actual member surface.* Proxy
  generation (both `Reflection.Emit`-based and Castle DynamicProxy) works
  by intercepting virtual method calls on generated types — it cannot
  intercept field access, since a field read compiles to a direct `ldfld`
  with no vtable dispatch to hook. A large share of the confirmed surface
  this project found by reading real extension metadata is public
  **fields**, not properties or methods — `frmViewer.mnuTools`/`mnuMain`/
  `mnuSessionContext`/`sbpInfo`/`tabsViews`, `Session.oRequest`/`oResponse`/
  `responseBodyBytes`/`requestBodyBytes`/`oFlags`,
  `HTTPRequestHeaders.HTTPMethod`, `HTTPResponseHeaders.HTTPResponseStatus`
  — because that's how real Fiddler Classic's own API was actually shaped.
  A proxy-based bridge simply has no hook for most of what these
  extensions actually touch; a real concrete backing type with real fields
  is still required, which is exactly what's already been hand-written
  here.
- *`AssemblyLoadContext` doesn't solve the actual hard problem.* ALC
  isolation stays inside the same .NET 10 runtime process. It cannot bring
  back `System.Windows.Forms.MenuItem`/`MainMenu`/`ContextMenu`/
  `StatusBarPanel` — those types are fully removed from .NET 10's own
  `System.Windows.Forms.dll`, not hidden or deprecated, so no in-process
  isolation technique recovers them. The proposal's own diagram hedges
  with "(or AppDomain in legacy .NET Framework)" for exactly this reason —
  and AppDomain-in-a-separate-.NET-Framework-process is not an alternative
  to the out-of-process host built in this phase, it *is* that host,
  relabeled.
- *The "legal advantage" claim doesn't distinguish this from what's
  already built.* This project's existing shim was already built entirely
  from metadata (`MemberRef` signature decoding, never IL) — that's been
  the clean-room methodology all along. Static vs. dynamic code generation
  changes nothing about the underlying fact pattern: an assembly claiming
  the name "Fiddler" that real, compiled Fiddler Classic extensions bind
  against. If anything, a statically compiled, fully-readable shim is the
  easier thing to defend under scrutiny — a specific `.dll` with documented
  provenance for every member, versus a runtime-generated proxy assembly
  that's inherently harder to point at, review, or explain. (Written
  before the second rename described in "Update — naming question closed"
  further below, which dropped `Fiddler` from the shim's own identity
  entirely — nothing about this alternative would have changed that move
  either.)

Net: even a corrected version of this proposal (drop the field-access
gap, drop `AssemblyLoadContext`, keep only "a separate .NET Framework
process hosting a `Fiddler`-named assembly") converges on the same
architecture already built and validated against all five real samples,
with an added runtime-codegen layer that adds complexity and reduces
auditability without changing the legal exposure at all. Not pursued.

**Considered, not pursued yet: a source-level compatibility SDK for
active extension authors.** A second alternative proposed externally,
shaped very differently from the three above: instead of loading an
*already-compiled* legacy extension `.dll` unmodified, publish a NuGet
package or source file (e.g. `Violens.Compatibility.Fiddler`)
containing type aliases (`using Fiddler = Violens.Fiddler;`) or
adapter wrappers, so an extension author can recompile their own source
against it and emit a native VioLens extension. Pitched as shifting
"the responsibility of binary identity to the extension developer" and
providing "a clean, legal path forward for the ecosystem."

This is a genuinely different shape from Strategies 1–3, not a
restatement — worth being precise about why:

- *It sidesteps the assembly-identity question, for real.* Strategies
  1–3 all exist to satisfy the CLR's need to resolve an *already-compiled*
  binary's own `AssemblyRef` for "Fiddler" before any of its code can run.
  A source-level SDK has no such binary — the author's own source is
  recompiled directly against whatever VioLens actually is — so the
  identity-substitution mechanics this whole design doc otherwise centers
  on simply don't apply here. That's a real technical distinction, not
  just a rebranding of the same idea.
- *It does nothing for the five real samples this phase already
  validated.* `AustralianImages`, `ContentBlock`, `JSFormat`,
  `SAZClipboard`, and `Differ` are already-compiled binaries with no
  indication their authors are actively maintaining them. A
  recompile-against-a-new-SDK path only helps an author who is still
  around and willing to maintain a VioLens-specific build — it's not a
  substitute for the binary-compatibility host already built and proven
  against the real samples in hand, only a possible complement for a
  *future*, actively-maintained population of extensions.
- *"Without modifying their underlying logic" doesn't hold for anything
  that touches UI.* A type alias renames a type at compile time; it
  doesn't reshape its members. It only works where
  `Violens.Fiddler.Foo` already exposes the same fields/signatures the
  extension's source calls — plausible for pure data-processing code
  (header/body inspection, diffing), but not for the WinForms surface
  this project's own metadata read confirmed several real extensions
  actually use (`frmViewer.mnuTools`, `MenuItem`, `MainMenu`). Those
  types don't exist in VioLens's own Avalonia/.NET 10 app at all — the
  entire reason the out-of-process net48 host exists in this phase. An
  alias package can't paper over a UI toolkit that isn't there; an
  extension using it would need real source changes, not just a
  recompile.
- *"Shifts responsibility to the extension developer" is a reframing,
  not a resolution.* VioLens would still be the one authoring,
  naming, and publishing `Violens.Compatibility.Fiddler` with a
  `Fiddler` alias inside it — that's VioLens's own artifact, not
  something a third party independently chose to build. What plausibly
  *does* change is the character of the exposure: this looks closer to a
  compatibility/porting shim that says what it is, rather than a runtime
  assembly presenting itself, by binary identity, as something a
  pre-existing third-party `.dll` believes is the genuine article. That
  distinction seems meaningful. (Written before the project made its own
  call on the underlying naming question — see "Update — naming question
  closed: the second rename," further below.)

Net: not rejected the way Strategy 2 was — it isn't redundant with what's
already built, since it targets a different population (future, actively
maintained extension authors) through a different mechanism (source
recompilation, not binary loading). But it isn't a replacement for the
already-validated binary-compat host either, and its "no logic changes"
pitch only holds for the non-UI slice of what real extensions do. Worth
keeping in mind as a possible future, complementary offering — not
something to build in this phase.

**Update — Fiddler's own licensing history, and what changed in August
2026.** Fiddler was created by Eric Lawrence and was free to use from the
start. Telerik acquired it in 2012, and Progress Software acquired Telerik
in 2014. In August 2026 Progress changed the Fiddler Classic license so
that it may be used only for non-commercial purposes, directing
commercial users to its Fiddler Everywhere product (see Progress's
[Fiddler Classic commercial-use page](https://www.telerik.com/fiddler/fiddler-classic/commercial-use)).

This doesn't change anything this project depends on. The clean-room
research read the five real extension DLLs' own metadata (third-party
binaries with their own licensing) and Progress's public API
documentation, never Progress's Fiddler.exe or FiddlerCore binaries, so
Fiddler Classic's license, old or new, doesn't govern it. It did make the
naming question above more important to settle, which led to Strategy 5
below and, later, to the second rename. "Fiddler" and related names are
trademarks of Progress Software Corporation; this project uses them only
to describe compatibility, and doesn't present itself as Fiddler.

## Strategy 5 — adopted: drop the `Fiddler` assembly identity, detect and explain mismatches instead ("Don't get sued")

Proposed externally, right after the licensing-history update above, from
a simple factual question: all five real extension DLLs this project has
are .NET (MSIL), not native code — so what exactly stops the CLR from
loading them, and what's actually in that MSIL that a person, technical or
not, could inspect or change? The proposal that followed: drop the
`Fiddler` assembly identity from the compat shim entirely, and when an
extension's own metadata expects an assembly by that name and doesn't get
one, show a dialog naming what was found versus what was expected and
asking the user to recompile. Framed explicitly as trading a usability
regression (the five known samples, and any other unmodified extension,
stop loading out of the box) for removing the sharpest fact pattern in the
trademark exposure discussed above — decided, on the spot, to write this
up and then implement it immediately, on the strength of a new project
tenet: **don't get sued.** Where earlier strategies in this doc weigh
technical trade-offs, this one removes the naming concern directly, at a
cost the project has decided is worth paying.

**The factual premise, confirmed.** All five real samples
(`AustralianImages.dll`, `ContentBlock.dll`, `JSFormat.dll`,
`SAZClipboard.dll`, `Differ.dll`) are ordinary .NET assemblies compiled to
MSIL, and none are obfuscated — this project's earlier metadata-only
reads (`MemberRef`/`AssemblyRef` table decoding via
`System.Reflection.Metadata`, the same clean-room technique used
throughout this whole design) worked against all five with full fidelity,
which wouldn't be true of an obfuscated or IL-mangled binary. MSIL is
directly editable without needing to understand — or even see — an
extension's own logic: `ildasm`/`ilasm` (part of the .NET Framework SDK)
round-trip a `.dll` to human-readable IL text and back, and tools like
dnSpy or ILSpy edit and reassemble a `.dll` directly. Retargeting which
assembly a `.dll` expects to bind against is a purely mechanical edit to
its `AssemblyRef` table — a name and a version, nothing about *what the
extension does* — so a technical user, or an extension's own author, can
make this specific change without reverse-engineering anything.

**What was actually built.** Rather than leaving that detection to
whatever confusing `FileNotFoundException`/`FileLoadException` the CLR's
own binder happens to produce when an `AssemblyRef` can't resolve, this
project added a proactive, metadata-only pre-check that runs before
`Assembly.LoadFrom` is ever called:

- **`FiddlerAssemblyReferenceInspector`** scans a candidate `.dll`'s own
  `AssemblyReferences` table (via `PEReader`/`MetadataReader`, the same
  ECMA-335-metadata-only approach used everywhere else in this project)
  for a case-insensitive reference to an assembly named `Fiddler`,
  entirely without loading or executing the file.
- **`AssemblyMismatch`** captures what was found (the referenced name and
  version) against what this host's own compat assembly actually is now,
  and renders a diagnostic message naming the file, both assembly
  identities, and what to do about it — recompile against the real name
  if source is available, or edit the `AssemblyRef` directly via
  `ildasm`/`ilasm`/dnSpy if not.
- **`LegacyExtensionLoader.LoadOne`** runs this check first for every
  candidate `.dll`. A mismatch is recorded (surfaced both as a structured
  `AssemblyMismatches` list and folded into the existing `LoadErrors` list
  so it shows up on the Log tab either way) and the file is never handed
  to `Assembly.LoadFrom` at all — no load attempt, no partial
  construction, nothing for a half-bound extension to do.
- **`Program.cs`** pops a `MessageBox` summarizing every mismatch found
  during a scan, so this is visible immediately, not just in a log a user
  has to go looking for.

**The rename this depends on.** None of this closes anything by itself
unless the shim actually stops claiming to be `Fiddler` — a detector that
still ships inside an assembly named `Fiddler` would be flagging itself.
So the compat shim project itself (all 22 source files, its `csproj`'s
`<AssemblyName>`/`<RootNamespace>`) was renamed from `Fiddler` to
**`Violens.Fiddler`**, both as a C# namespace and as the literal
assembly identity the CLR binds against. This is the actual mechanism
behind the trademark-risk reduction: the binary-identity-substitution
fact pattern flagged throughout this document — an assembly presenting
itself, at the binder level, as the genuine `Fiddler.dll` real
third-party extensions already expect — no longer exists. What remains is
nominative reference only (the C# type `Violens.Fiddler.FiddlerApplication`,
prose describing compatibility, diagnostic messages that name `Fiddler`
to explain a mismatch): descriptive use that doesn't present this project
as Fiddler.

*(This was the first of two renames. The shim's current name is
`Violens.CompatShim` — see "Update — naming question closed: the
second rename," directly below the "Net" paragraph that follows.)*

**The trade-off, accepted deliberately.** All five real samples validated
earlier in this document — previously confirmed to load clean, register
correctly, and (for `SAZClipboard`) add a working `mnuTools` menu item —
no longer load unmodified. `RealExtensionLoadingTests` (see this
project's `Tests` folder) was rewritten to assert the new, intended
behavior: all five are detected as mismatches, nothing gets registered,
and the diagnostic messages actually explain what a user needs to do.
That old milestone — "a real, unmodified extension genuinely works end to
end" — isn't gone, just relocated: `PatchedExtensionTests`, gated by a
new opt-in `VIOLENS_LEGACY_PATCHED_EXTENSIONS_DIR` environment
variable, carries the pre-pivot assertions forward against a folder of
extensions a developer has recompiled or re-targeted themselves. Nothing
in this repo ships a patched copy of any real sample — that would mean
redistributing a *modified* third-party binary, a strictly stronger
copyright concern than redistributing the originals (already policy: ask
first) — so this test group is inert until a developer points it at their
own local folder.

**Net:** unlike Strategies 2 and 4 above, this isn't a rejected
alternative — it's the one adopted, and it's already built. At the time
this was written, it removed the single fact pattern this document had
consistently flagged as the sharpest one, without yet closing the
question in full — see immediately below for how it was closed.

## Update — naming question closed: the second rename

Prompted directly by Mark, reviewing the finished main-app integration
work above: since the project had already decided not to imitate the
`Fiddler` assembly (the whole point of Strategy 5, immediately above),
did it make sense to go one step further and drop `Fiddler` from the
shim's own name entirely?

**What changed, mechanically.** The compat shim was renamed a second
time: `Violens.Fiddler` → **`Violens.CompatShim`** — both the C#
namespace (all 22 source files) and the literal `AssemblyName`/
`RootNamespace` the CLR binds against. `Violens.CompatShim` was chosen
over the first name floated (`Violens.Compatibility`) specifically to
avoid colliding with `src/Violens.Compatibility`, the main app's own,
unrelated compatibility layer — reusing that name for a different
assembly doing a different job would have traded one confusion for
another. This costs nothing functionally: an extension author reconnects
to this shim by source-level namespace alias (`using Fiddler =
Violens.CompatShim;`), not by the shim's namespace literally being
called `Fiddler`, so nothing about the recompile story in
`AssemblyMismatch.ToDiagnosticMessage()` changes.

**What this does and doesn't resolve.** This removes the one thing the
first rename (above) left behind: the word `Fiddler` appearing in the
shim's own identity at all, even prefixed. What's left is descriptive
reference only: prose describing compatibility with Fiddler Classic, and
diagnostic messages that name `Fiddler` to explain a mismatch. The
project treats the naming question as settled on this basis. Nothing in
this document is legal advice.

## The legacy host's session bridge — built

Prompted directly by a simple question after "Don't get sued" shipped:
does the legacy host need to be integrated with the main program? The
honest answer at that point was no, not yet — Phase 1 (above) deliberately
stopped at proving discovery/loading/assembly-mismatch mechanics against
two synthetic demo sessions, with the main app never launching, connecting
to, or knowing about this process at all. Decided to close that gap now
rather than leave it as an open item: a legacy extension whose
`IAutoTamper` hooks only ever see synthetic data isn't actually useful yet
for its one real purpose (inspecting or tampering with a person's real
HTTPS traffic), and the trademark blocker that justified deferring this
work is now mostly resolved.

**What "session bridge" means concretely.** VioLens's main app
(`InterceptingProxyListener`) already has exactly one seam built for this:
`IExtensionAutoTamperHost` — the same narrow interface
`Violens.Compatibility.Extensions.LoadedExtensionSet` already implements
to run *in-process* compiled extensions' `IAutoTamper` hooks against real
traffic. The session bridge is a second implementation of that same
interface, `LegacyExtensionHostBridgeClient`, that runs those hooks
*out-of-process* instead — by calling across a named pipe into
`Violens.LegacyExtensionHost.exe`, which runs them for real against its
own loaded extensions and hands back whatever they changed. Neither
`InterceptingProxyListener` nor `LoadedExtensionSet` had to change at all
to make this work — see "How the two hosts compose," below.

**Why named pipes, not sockets or anything networked.** This never needs
to leave the user's own machine (both processes always run locally), and
a named pipe simply can't be reached from another machine at all — the
strongest, simplest form of "not exposed to the network" available,
matching the "Sizing option 1" section's own original recommendation
above. `SessionBridgeProtocol.PipeName` includes an explicit version
suffix (`.v1`) rather than a separate handshake field: the simplest
possible guard against two independently-rebuilt binaries (the net10.0
main app and the net48 legacy host are never guaranteed to ship together)
silently misinterpreting each other's bytes — a protocol change just fails
to connect instead of connecting and corrupting data.

**The wire contract lives in its own project, deliberately dependency-free.**
`tools/Violens.LegacyExtensionHost/Violens.LegacyExtensionHost.Bridge/`
targets `netstandard2.0` — the one target both a net10.0 project and a
net48 project can reference directly — and contains only plain data
shapes (`WireRequest`/`WireResponse`/`WireHeader`, mirroring
`Violens.ProxyCore.Http.CapturedRequest`/`CapturedResponse` field-for-field),
a `BridgeMessageKind` enum for which of `IAutoTamper`'s four hooks (plus
one `Capabilities` probe) a call is for, and the length-prefixed
JSON-over-`Stream` framing (`SessionBridgeProtocol.WriteMessage`/`ReadMessage`)
both ends use identically. Nothing net48-only (`Fiddler.Session`) or
net10.0-only (`CapturedRequest`) crosses this boundary — each side maps to
and from its own types at its own edge (`SessionMapping` on the legacy
host's side, private `ToWire`/`FromWire` methods on
`LegacyExtensionHostBridgeClient`'s side).

**Never a hard dependency, by design.** Nothing about running VioLens,
or proxying real traffic, requires the legacy host process to exist —
this was a hard requirement, not a nice-to-have, given tenet 5. Three
specific choices enforce it:

- `LegacyExtensionHostBridgeClient.Probe` tries once, with a short
  (250ms) connect timeout, every time the main app's proxy is *started*
  (not once at app launch — see below for why that's a deliberate
  difference from `ExtensionHost`'s own one-time-at-launch scope). An
  unreachable host, or one with zero `IAutoTamper` extensions loaded
  (true today for all five real samples, still identity-mismatched),
  makes every hook method an unconditional passthrough for that run —
  no further connection attempts, no per-request overhead, for the
  common case of nobody using this feature at all.
- Every hook call still has a bounded overall timeout
  (`SessionBridgeProtocol.RoundTripTimeoutMilliseconds`, 3 seconds) even
  after a successful connect — a hung legacy extension (blocking forever,
  the same failure mode real Fiddler Classic's own UI thread has always
  been vulnerable to from a badly-behaved extension) eventually lets real
  browsing traffic through rather than stalling it forever. Enforced by
  racing the actual pipe I/O on a background `Task` against
  `Task.Wait(timeout)` and forcibly disposing the pipe to unblock a stuck
  synchronous read if the deadline passes — `PipeStream` has no
  cooperative cancellation for a blocked synchronous `Read`/`Write`, so
  tearing down the handle from another thread is the only reliable way to
  stop waiting on one.
- Every failure mode (no host running, timeout, malformed response)
  fails *open*: the original, unmodified request or response is returned
  unchanged, logged once, never thrown from `RunRequestBefore` etc. A
  broken or slow legacy extension degrading to "as if it weren't
  installed" is the only acceptable failure mode for something sitting in
  the hot path of a person's real browsing traffic.

**One pipe connection per hook call, not one persistent connection.**
`InterceptingProxyListener` may be juggling several connections
concurrently (each on its own thread/task), and a single shared pipe
stream written to from multiple threads at once would interleave bytes
from different callers into a corrupted message with no correlation
mechanism to sort them back out. Reconnecting per call sidesteps that
correctness problem entirely — no request IDs, no multiplexing, no shared
mutable stream state — at the cost of a fresh connect per call, which is
cheap enough for local, same-machine named-pipe IPC not to matter for
interactive browsing. `SessionBridgeServer` matches this shape on its own
side with four concurrent `NamedPipeServerStream` instances of the same
pipe name (`SessionBridgeServer.InstanceCount`) rather than one, so one
slow extension handling one request doesn't serialize every other
concurrent connection behind it — not unlimited scale, just enough that a
handful of concurrent browser requests don't queue behind each other by
default.

**Probed fresh on every proxy `Start()`, unlike `ExtensionHost`'s
load-once-at-launch scope.** A deliberate divergence, not an oversight:
the legacy host is a separate, optional process someone might well launch
*after* already opening VioLens (or restart independently, e.g. after
recompiling an extension). Re-probing on every Start click means Stop then
Start in the main app picks that up immediately, with no restart of
VioLens itself required — the better default for a component that isn't
guaranteed to be running when the main app launches.

**How the two hosts compose.** `MainWindowViewModel.Start()` used to hand
`InterceptingProxyListener` exactly one `IExtensionAutoTamperHost`
(`_extensionHost.CreateAutoTamperHost()`, the in-process
`LoadedExtensionSet`). It now wraps that together with a freshly-probed
`LegacyExtensionHostBridgeClient` in a new
`CompositeExtensionAutoTamperHost` (`Violens.ProxyCore.Extensions`) —
runs each wrapped host's hooks in sequence against one progressively-edited
request/response, exactly the same "shared, threaded-through" shape
`LoadedExtensionSet` already uses one level down across several loaded
`IAutoTamper` instances, just one level up across several *extension
hosts*. `InterceptingProxyListener` itself needed zero changes: it still
only ever sees one `IExtensionAutoTamperHost`, unaware that it's now
potentially fanning out to a whole separate process.

**What doesn't round-trip yet, honestly.** `Fiddler.Session` (the
CompatShim's own type, reproduced strictly from the five real samples'
confirmed metadata — see "Binary compatibility vs. source compatibility,"
above) has no member for HTTP version at all, so `WireRequest.HttpVersion`/
`WireResponse.HttpVersion` always pass through the bridge unchanged; no
loaded legacy extension can actually see or edit it. And
`Fiddler.HTTPHeaders`' own indexer-based store (built before this bridge
existed, to match the confirmed real member surface) replaces rather than
appends a same-named header, so a request or response with a genuinely
repeated header name (multiple `Set-Cookie` being the realistic case)
collapses to its last value once it passes through a loaded extension —
logged clearly (`SessionMapping.SetHeaders`), not silently dropped, but
not fixed here either. Both are pre-existing properties of the CompatShim
itself, surfaced rather than newly introduced by this bridge.

**Still not built, as of this section:** automatic process launch (the main
app still doesn't start `Violens.LegacyExtensionHost.exe` itself — both
processes simply find each other over the named pipe if both happen to
already be running) and the UI-presentation question (still a fully
separate window, not reparented) — see the "Sizing option 1" section's own
remaining bullets for both, unchanged by this work. *Automatic launch is
now built* — see "The legacy host's launch/stop lifecycle — built," just
below — but the UI-presentation question remains exactly as described
there: a separate top-level window, not reparented into the main app, a
known and accepted limitation rather than a blocker. Only `IAutoTamper`'s
four hooks are bridged — `Inspector2`, `ISessionImporter`/`ISessionExporter`,
and `IHandleExecAction` aren't (none of the five real samples this shim was
built against reference them, matching this project's consistent "build
what's confirmed needed, not speculatively" approach throughout).

**Tests.** `SessionBridgeRunnerTests` exercises `SessionBridgeRunner`/
`SessionMapping` directly against small fake `IAutoTamper` implementations
(no pipe, no process) — the wire-message-to-`Session`-and-back mapping,
several tampers sharing one progressively-edited session in order, one
throwing tamper not affecting the others, and the repeated-header
collapse being logged rather than thrown on. `SessionBridgeServerTests`
repeats the core round trip for real, over an actual
`NamedPipeClientStream` talking to a real, running `SessionBridgeServer`
(started once for the whole test run, since the pipe name can only be
bound so many times concurrently at once — see `SessionBridgeServerFixture`'s
own remarks), confirming the wire framing and JSON serialization actually
work together end to end. `CompositeExtensionAutoTamperHostTests`
(`tests/Violens.ProxyCore.Tests/`) exercises the composition itself
against fake `IExtensionAutoTamperHost` implementations, independent of
either real host.

## The legacy host's launch/stop lifecycle — built

Prompted directly by Mark, once the session bridge above had been proven
against real live traffic through a real retargeted extension: "I think
we're ready to look at integrating the extension support into the main
app?" The honest gap at that point, spelled out explicitly at the end of
the session bridge section above ("Still not built: automatic process
launch"): the main app could *talk* to `Violens.LegacyExtensionHost.exe`
over the named pipe once it existed, but never started it — Mark had been
manually running it himself (`dotnet run`, or the built `.exe` directly)
for this whole project's worth of manual testing.

**Three scoping decisions, asked directly rather than guessed:**

- **Launch trigger: opt-in, not automatic.** A new `AutoLaunchLegacyHost`
  checkbox on `MainWindowViewModel` (default `false`) gates every call into
  `LegacyExtensionHostLauncher.EnsureRunning` — a person who's never
  touched legacy extensions never gets a second, Windows-only net48 process
  spawned under them just for clicking the main app's own Start button.
  When it's on, launching (if needed) happens as part of the same Start
  click, and stopping (if this app is what launched it) happens as part of
  the matching Stop click — one switch, no separate button, riding the
  proxy's own existing lifecycle rather than adding a second one next to
  it.
- **Path convention: a fixed subfolder of the main app's own install, not
  a user-configurable setting.** Mark's own call: for a real per-user
  install (e.g. main app under `%LocalAppData%\Programs\VioLens\`), the
  legacy host lives in a `LegacyHost` subfolder right beside it
  (`%LocalAppData%\Programs\VioLens\LegacyHost\Violens.LegacyExtensionHost.exe`).
  `LegacyExtensionHostLauncher.ExpectedExecutablePath` computes exactly
  that, relative to `AppContext.BaseDirectory` (the main app's own running
  location) rather than any hardcoded dev-tree path or a settings field to
  maintain.
- **Status UI: visible, not silent.** A new "Legacy Extension Host" panel
  (`MainWindow.axaml`), toggled from `_Tools` the same way the existing
  FiddlerScript/Extensions panels already are, showing the checkbox plus a
  status line built from whatever `LegacyExtensionHostLauncher.Result`
  the last Start/Stop actually reported — not just a console line, matching
  why the Extensions status panel itself exists (see Phase 2's own
  remarks).

**What was actually built.** `LegacyExtensionHostLauncher`
(`src/Violens.Compatibility/Extensions/`), alongside
`LegacyExtensionHostBridgeClient` (same folder, same "never a hard
dependency" posture one level up): `EnsureRunning` pings the session bridge
pipe first (`LegacyExtensionHostBridgeClient.Ping`, a new, cheaper sibling
of `Probe` that answers only "is anything listening at all," deliberately
different from `Probe`'s own "reachable AND has AutoTampers loaded"
question — needed so a legacy host that's genuinely running with zero
AutoTamper extensions dropped in yet still isn't duplicated), then either
reports `AlreadyRunning` or launches the `.exe` and waits (bounded, up to
five seconds, polling every 250ms) for its session bridge to actually come
up before returning — without that wait, `MainWindowViewModel.Start`'s own
very next `Probe()` call would almost always still see nothing listening
yet (WinForms startup plus `SessionBridgeServer`'s own listener threads
spinning up take longer than a cold process launch alone), silently
defeating the point of auto-launching for that entire run.
`StopIfLaunchedByUs` is the deliberately narrow other half: a no-op unless
this launcher's own `EnsureRunning` is what started the tracked process —
a legacy host reachable because Mark (or an earlier `AlreadyRunning`
result) started it independently is never touched, closed via
`Process.CloseMainWindow()` first (a real `WM_CLOSE` to `frmViewer`, giving
every loaded extension's `OnBeforeUnload` a chance to run through the
host's own normal FormClosing handling) with `Process.Kill()` as a bounded
fallback if it doesn't exit promptly. Wired into
`MainWindowViewModel.Start`/`Stop`/`Dispose` — `Dispose` calls
`StopIfLaunchedByUs` unconditionally (not just when `_proxy` ended up set),
specifically because `EnsureRunning` can succeed even in the rare case
where the proxy listener itself then fails to start, which would otherwise
leave an orphaned legacy host process behind if the app closes rather than
Stop being clicked first.

**What this took to close, flagged as it went rather than papered over:**
the `LegacyHost` subfolder convention above is the *installed-release*
layout Mark described, not the layout a `dotnet run` dev checkout of this
repo actually produces — running the main app from source gives an
`AppContext.BaseDirectory` under `apps/Violens.DesktopUi/bin/...`, with
no `LegacyHost` subfolder unless someone copies the legacy host's own build
output (`tools/Violens.LegacyExtensionHost/Violens.LegacyExtensionHost/bin/...`)
there by hand first. Making the two layouts agree needed two separate
pieces of work: an MSBuild post-build copy step for local dev runs, and an
actual change to the Inno Setup installer script for a real release. Both
are now built.

The first half is now built: `Violens.LegacyExtensionHost.csproj` has a
`CopyOutputToDesktopUiLegacyHostFolder` target (`AfterTargets="Build"`)
that copies its own build output into
`apps/Violens.DesktopUi/bin/$(Configuration)/net10.0/LegacyHost/` once it
finishes building, so a plain `dotnet build`/`dotnet run` (or F5 in Visual
Studio) picks up a populated `LegacyHost` folder automatically as long as
both projects were built under the same `$(Configuration)`. Placed on the
legacy host's own project rather than `Violens.DesktopUi.csproj`, on
purpose: that keeps the "independently built, independently shipped"
guarantee above one-directional — the main app's build still has zero
knowledge this project exists, and skipping this project's build (as CI
and the installer already do) just leaves `LegacyHost` unrefreshed rather
than breaking anything. This is scoped to local dev builds specifically;
it doesn't touch `dotnet publish` packaging or run against a
`RuntimeIdentifier`-qualified output path (neither project sets one today).

**Update — the installer script change is built too.**
`.github/workflows/release-windows.yml` now has its own
`dotnet build tools/Violens.LegacyExtensionHost/Violens.LegacyExtensionHost/Violens.LegacyExtensionHost.csproj
--output publish/legacyhost` step (plain `dotnet build`, not `dotnet
publish` — this is a framework-dependent `net48` project, not
self-contained like the main app's own `win-x64` publish above; .NET
Framework 4.8 ships with Windows 10/11 already, the same dependency real
Fiddler Classic itself had), separate from and after the main app's own
publish step, matching this project's own "independently built,
independently shipped" posture one level up into CI itself. `--output`
flattens the legacy host's own build output together with its
`ProjectReference`s' (`Violens.CompatShim.dll`,
`Violens.LegacyExtensionHost.Bridge.dll`) into one folder, the same
shape `CopyOutputToDesktopUiLegacyHostFolder` already produces locally.

`installer/VioLens.iss` picks that up as a new `[Tasks]` entry
(`legacyhost`), **unchecked by default** — mirroring
`AutoLaunchLegacyHost`'s own opt-in-off-by-default posture at the app
level, one layer up into the installer itself, rather than assuming every
install wants an extra `net48` payload for a feature most people will
never touch. A Task, not a `[Components]`/`[Types]` split: this installer
only ever produces one product either way, and a Task (the same mechanism
already used for the optional desktop-shortcut checkbox) is the simplest
fit for "only copy these files if the person checked this box." Checking
it copies `{#LegacyHostDir}\*` into `{app}\LegacyHost`, matching
`LegacyExtensionHostLauncher.ExpectedExecutablePath`'s fixed path
convention exactly.

Still unconfirmed back to this session (no Windows machine here to run any
of this against): the whole feature end to end on a real machine — the
opt-in checkbox actually launching the process, the five-second readiness
wait actually being enough in practice, `CloseMainWindow`/`Kill` actually
stopping it cleanly, and now also a real Inno Setup compile with the
`legacyhost` Task checked, to confirm the packaged `LegacyHost` folder
actually satisfies `ExpectedExecutablePath` on a machine that installed
via `setup.exe` rather than running from source.

## The interfaces: reproduced member-for-member, `Exchange` as the shared session object

`src/Violens.Compatibility/Extensions/`:

- **`IFiddlerExtension`** — `void OnLoad()`, `void OnBeforeUnload()`.
- **`IAutoTamper : IFiddlerExtension`** — `AutoTamperRequestBefore`/
  `AutoTamperRequestAfter`/`AutoTamperResponseBefore`/
  `AutoTamperResponseAfter`/`OnBeforeReturningError`, each
  `(Exchange oSession)`. The compiled-extension equivalent of
  FiddlerScript's `Handlers.OnBeforeRequest`/`OnBeforeResponse`, with two
  extra hook points (`*After`) FiddlerScript's own two-handler surface
  doesn't expose.
- **`IAutoTamper2 : IAutoTamper`** — adds `OnPeekAtResponseHeaders(Exchange oSession)`.
- **`IAutoTamper3 : IAutoTamper2`** — adds `OnPeekAtRequestHeaders(Exchange oSession)`.
- **`IHandleExecAction`** — standalone, `bool OnExecAction(string sCommand)`.
  The public docs give the signature but not a spelled-out return-value
  contract; this project treats `true` as "handled, stop looking" as a
  flagged, reasonable-but-unconfirmed assumption.

Every method signature above is reproduced verbatim from
fiddlerbook.com/fiddler/dev/IFiddlerExtension.asp, fetched directly for
this pass.

**Deliberate choice: these interfaces take
[`Exchange`](../src/Violens.Compatibility/FiddlerScript/Exchange.cs) —
the exact same session-facing object FiddlerScript's `Handlers` functions
already use — rather than a second, parallel "Session-shaped" type built
just for .NET extensions.** One object model serves both compatibility
layers: an extension author already familiar with Fiddler's
`oSession.hostname`/`oSession.oRequest`/`oSession["ui-color"]`/etc. finds
the exact same names here, and VioLens only has one set of
request/response mutation plumbing (`Exchange.ToRequest`/`ToResponse`) to
keep correct, not two that could drift apart. It also means an extension
author doesn't need Jint at all — `Exchange` is a plain public C# class,
usable directly from a compiled extension project via a normal
`ProjectReference`.

## What's built (this pass)

The five interfaces above, plus `tests/Violens.Compatibility.Tests/SampleExtension.cs`
(an original class, not a port of `SAZClipboard.dll` or the CC-licensed
source above) implementing every one of them, and
`ExtensionInterfaceTests.cs`, which exercises each method individually
and asserts on its specific, observable effect through `Exchange` — not
just that the interfaces compile. This proves the interface contract
itself works end-to-end through `Exchange`; it does not yet prove
anything about discovery, loading, or real proxy-traffic wiring, all of
which are separate, larger slices below.

## Phase 2 — discovery, listener wiring, inspectors, SAZ hookup (now built)

Everything items 1–4 below used to describe as future work is now built,
end to end: a compiled extension .dll dropped in the right folder is
discovered, gated, loaded into isolation, and its hooks actually run
against real proxied traffic, real inspector tabs, and the File menu —
not just callable directly the way this pass's original
`ExtensionInterfaceTests.cs` exercised the five interfaces alone.

**Discovery/loading** (`Violens.Compatibility.Extensions.ExtensionHost`).
Scans one or more folders, non-recursively, for `*.dll` files — matching
the flat, non-recursive `Scripts` folder convention the public docs
describe for Fiddler's own extension folders, just under VioLens's own
name (`ExtensionHost.DefaultExtensionsFolder`, not a copy of Fiddler's
path). Each `.dll` loads into its own isolated, collectible
`AssemblyLoadContext` (via `AssemblyDependencyResolver`, so an extension
can ship its own dependency `.dll`s alongside itself), reproducing
Fiddler's own documented `RequiredVersion` gating exactly: an assembly
with no `RequiredVersionAttribute` is silently skipped (not an error —
see the attribute's own remarks), one with an unparseable or too-high
minimum version is skipped with a load error recorded, and one that
passes gets scanned (`ReflectionTypeLoadException`-safe) for concrete,
parameterless-constructible types implementing any of this namespace's
extension interfaces. One instance is constructed per matching type and
registered into every applicable list it qualifies for (a type
implementing both `IAutoTamper` and `IHandleExecAction`, say, lands in
both lists as the same instance) — `AutoTampers`, `RequestInspectors`,
`ResponseInspectors`, `Importers`, `Exporters`, `ExecActionHandlers`, plus
a `LoadErrors` list surfacing everything that went wrong along the way.
`Load()`/`Unload()` call every loaded extension's `OnLoad`/
`OnBeforeUnload` once each, isolating a throw to just that one extension
the same way the rest of this class does. Deliberately scoped as
"load once at startup, unload once at shutdown" — no live hot-reload for
compiled extensions, unlike FiddlerScript's own `Reload()`; real Fiddler
Classic doesn't hot-reload compiled extensions either, so this matches
the thing being reproduced rather than cutting a corner.

**`IAutoTamper` wired into `InterceptingProxyListener`**
(`Violens.ProxyCore.Extensions.IExtensionAutoTamperHost`, implemented by
`Violens.Compatibility.Extensions.LoadedExtensionSet`). Same
interface-inversion pattern as `IScriptRunner`/`FiddlerScriptRunner`
(see that pair's own remarks): `InterceptingProxyListener` takes an
optional `IExtensionAutoTamperHost` and joins its
`HasAnyRequestBeforeHandlers`/`HasAnyResponseBeforeHandlers` into the
exact same body-buffering fork `IScriptRunner.HasOnBeforeRequest`/
`HasOnBeforeResponse` already forces, running immediately *after*
FiddlerScript's own handler (an assumed, not confirmed-against-real-Fiddler
ordering — see `IExtensionAutoTamperHost`'s own remarks) and before the
breakpoint check. `LoadedExtensionSet` builds one `Exchange` per call and
runs every loaded extension's hook against that same shared instance in
sequence, so each extension sees the previous one's edits — exactly like
real Fiddler running several loaded extensions' `AutoTamperRequestBefore`
against one shared `oSession`. One extension throwing doesn't stop the
others or the connection. The `*After` pair
(`AutoTamperRequestAfter`/`AutoTamperResponseAfter`) runs unconditionally,
fire-and-observe, right after the corresponding write completes on either
the buffered or relay-live path — it never needs to force buffering, so
it's called from one spot after the two paths rejoin rather than gated
and duplicated inside each. `OnBeforeReturningError` remains explicitly
out of scope, unchanged from item 2 below — there's still no synthetic
error-response code path in the proxy to hang it off.

**`Inspector2`/`IRequestInspector2`/`IResponseInspector2`**
(`Violens.Compatibility.Extensions.IInspector2`, adapted by
`Inspector2Adapter` into `Violens.Extensibility.Inspection.IInspector`).
As anticipated, this is an adapter into the existing `IInspector`
contract, not a second inspector-hosting system —
`InspectorRegistry.CreateDefault` gained an `IEnumerable<IInspector>?
additional` overload specifically for this, so `InspectorRegistry` itself
stays unaware extensions exist at all. `IInspector2` deliberately omits
real Fiddler's own `AddToTab(TabPage)`/`GetOrder()` — fundamentally
WinForms-coupled, incompatible with this project's "data in, rendering
out" tenet (see `IInspector.cs`'s own remarks) — keeping only what lets a
body-transforming inspector still work: settable `headers`/`body` the
adapter feeds in (headers first, then body, since real inspectors
commonly key off Content-Type), read back out afterward. This is a real,
flagged limitation, not a silent one: with no method that hands back
arbitrary UI content, `Inspector2Adapter.Inspect` can only ever produce
`TextContent` from the resulting bytes (UTF-8 decoded) — an extension that
populated a fully custom WinForms control in real Fiddler has no
equivalent surface here today, consistent with `InspectorContent`'s own
remarks that an image/tree case is future work for every inspector, not
just adapted ones. `GetOrder()`'s absence means every adapted inspector
sorts at a fixed `Inspector2Adapter.DefaultSortOrder`, below the built-ins.

**`ISessionImporter`/`ISessionExporter`**
(`Violens.Compatibility.Extensions.ISessionImporter`/`ISessionExporter`,
`ImportedSession`, `ProgressCallbackEventArgs`, `ProfferFormatAttribute`).
`ISessionImporter.ImportSessions` returns `ImportedSession` records — a
VioLens-native stand-in shaped exactly like `SessionStore.Add`'s own
parameters, not a freely-constructed `Session` the way real Fiddler's own
importers build one. `Session` itself is technically public-constructible
(an ordinary positional record, nothing stopping `new Session(...)`), but
only `SessionStore.Add` is allowed to hand out real, sequential `Id`s —
it assigns one itself and doesn't even take a pre-built `Session` as a
parameter — so an importer constructing its own `Session` would have to
guess an `Id` that's either going to collide with a real one or be
silently overwritten anyway. `ImportedSession` sidesteps the question
entirely by carrying no `Id` at all. `ISessionExporter.ExportSessions`
uses the real `Session` type directly, since export only ever reads
already-captured sessions (with their real, already-assigned `Id`s) and
never needs to construct one. `ProgressCallbackEventArgs` deliberately drops the separate
`PercentComplete: string` member the public docs mention alongside a
numeric ratio — its exact contract couldn't be pinned down from what's
publicly documented, and a redundant, unverified string duplicate wasn't
worth guessing at; flagged here, not silently dropped. Wired into the
desktop app's File menu as "Import via Extension…"/"Export via
Extension…", each enabled once at least one extension proffers the
matching capability. Deliberately simpler than real Fiddler's own
multi-format Import/Export dialog: with more than one importer/exporter
loaded, or one proffering more than one `[ProfferFormat(...)]`, VioLens
always uses the first one found rather than showing a picker — a real
format-choice UI is future work. Neither call ever receives a host-chosen
file path in its `options` dictionary (always passed empty): an importer
or exporter that needs one is expected to gather it itself, the same way
a real Fiddler extension would show its own dialog, rather than this host
inventing an unconfirmed options-dictionary convention.

**Desktop UI wiring.** `MainWindowViewModel` constructs one `ExtensionHost`
pointed at `ExtensionHost.DefaultExtensionsFolder` and calls `Load()` once,
synchronously, in its constructor — no live reload, matching
`ExtensionHost`'s own scope. Its `AutoTampers` are wrapped fresh via
`CreateAutoTamperHost()` and handed to `InterceptingProxyListener` on
every `Start()`, the same way `_fiddlerScriptRunner` already is; its
request/response inspectors are folded into `InspectorRegistry` once,
via the new `CreateDefault(additional)` overload; `Unload()` runs from
`Dispose()`, regardless of whether the proxy happens to be running at the
time, so every loaded extension's `OnBeforeUnload` is still called.

**Extension-status UI panel.** Discovery/loading results were originally
only visible as console output (see `Violens.SampleExtension`'s own
README, below) — not usable for anyone running the packaged desktop app
without a visible console attached. `MainWindowViewModel.ExtensionStatus`
(a read-only bound string, `BuildExtensionStatus()`, called once right
after `_extensionHost.Load()`) surfaces the same information directly in
the window instead: one line per folder `ExtensionHost` scanned (whether
it existed, and how many `.dll` files it found there), a summary line with
a per-interface breakdown (`AutoTamper`/request inspector/response
inspector/importer/exporter/exec-action-handler counts, plus a total), and
one line per entry in `ExtensionHost.LoadErrors`. Rendered in `MainWindow.axaml`
as a small read-only "Extensions" status block, the same pattern as the
existing FiddlerScript status panel it sits alongside — deliberately
verbose (every scanned folder, not just a total) rather than a single
summary count, specifically so "did it look in the right place, and what
did it find there" never requires console access at all.

## Validation: `Violens.SampleExtension`

`tools/Violens.SampleExtension/` is an original, from-scratch sample
extension (not a port of `SAZClipboard.dll` or the CC-licensed source
mentioned above) built specifically to validate everything in the Phase 2
section end to end against a real, separately-compiled `.dll` — the one
thing `LoadedExtensionSetTests.cs`'s in-process fakes can't prove on their
own, since this sandbox has no local `dotnet` build toolchain. One class
per interface (`SampleAutoTamper` implementing the full `IAutoTamper3`
chain, `SampleExecActionHandler`, `SampleRequestInspector`/
`SampleResponseInspector`, `SampleSessionImporter`/`SampleSessionExporter`),
each exercised directly by `tests/Violens.SampleExtension.Tests/` (no
desktop app or dropped `.dll` needed for that half), plus the project's own
`README.md` walking through building it, dropping the built `.dll` into
`ExtensionHost.DefaultExtensionsFolder`, and four manual checks against a
running desktop app (discovery/loading, `IAutoTamper` on live traffic,
`Inspector2` tabs, and the Import/Export File-menu commands).

**Confirmed working end to end on a real machine:** running the desktop
app with `Violens.SampleExtension.dll` dropped into the extensions
folder showed the Extensions status panel reporting 1 `.dll` found and
loaded (1 `AutoTamper`, 1 request inspector, 1 response inspector, 1
importer, 1 exporter, 1 exec-action handler) — the discovery/gating/
`AssemblyLoadContext` loading path this environment's own test suite
couldn't exercise. This satisfies item 4 under "What's not built yet"
below for discovery/loading specifically; the three `IAutoTamper`/
`Inspector2`/`ISessionImporter`-`ISessionExporter` manual checks in the
README were not separately confirmed back to this session, so they're not
marked done here.

**Tests.** `tests/Violens.Compatibility.Tests/LoadedExtensionSetTests.cs`
exercises `LoadedExtensionSet` through its `IExtensionAutoTamperHost`
surface with small, original fake `IAutoTamper` implementations — no real
compiled extension `.dll` anywhere in the suite, matching the
`FiddlerScriptRunner`/`IScriptRunner` testability split this one
was built to mirror. `ExtensionHost`'s own disk/reflection/
`AssemblyLoadContext` scanning isn't covered by an automated test in this
pass (there's no local build toolchain in this environment to compile a
throwaway test extension `.dll` against) — validating discovery end to
end against a real compiled extension is one of the manual checks below.

## What's not built yet

1. ~~The binary-compatibility shim described above~~ — stale: this was
   written before that work started, on the assumption it would wait for
   outside input. It didn't, and it's long since built — see
   "Phase 1 — built," "Strategy 5 — adopted," and "Update — naming
   question closed: the second rename," all above.
2. ~~A real per-format Import/Export picker UI~~ — built: File >
   Import/Export via Extension now lists every (extension, format) pair
   (`ProfferedFormats.ChoicesFrom`) in `FormatPickerWindow` when there's
   more than one, and remembers the last choice in preferences.
3. **`OnBeforeReturningError`.** Still nothing in the proxy to hang it
   off — see the Phase 2 section above for why this is unchanged from
   before.
4. **End-to-end validation against a real compiled extension `.dll`**,
   partially done: `ExtensionHost`'s folder scan, `RequiredVersion` gating,
   and `AssemblyLoadContext` isolation are now confirmed working against
   `Violens.SampleExtension.dll` on a real machine (see "Validation:
   `Violens.SampleExtension`," above). Still unconfirmed back to this
   session: the three runtime-behavior checks in that project's own
   README — `IAutoTamper` actually mutating live traffic, the `Inspector2`
   tabs rendering in the desktop app, and the Import/Export File-menu
   commands round-tripping sessions.

## Update — a Fiddler-shaped layer in the main app (Sept 2026)

Trying real, public Fiddler Classic extensions (see the Extension Test
Targets doc) showed that "source-level compatible" wasn't close enough in
practice. The main app's interfaces take `Exchange` and return
VioLens-native types, so porting Eric Lawrence's NetLog importer would have meant
rewriting most of its 1,700-line parser. The legacy host's shim is shaped
like Fiddler's API, but it's `net48`, Windows-only, and only bridges
`IAutoTamper`.

So the main app now has a Fiddler-shaped layer of its own:
`src/Violens.Compatibility/CompatShim/`, namespace
`Violens.CompatShim`, targeting net10.0 on both platforms. It provides
`Session` (with `BuildFromData`), `HTTPRequestHeaders`/
`HTTPResponseHeaders`, `Parser`, `SessionFlags`, `SessionTimers`,
`FiddlerApplication.Log`/`Prefs`/`DoNotifyUser`/`ReportException`, a
`Utilities` subset, and Fiddler's own `ISessionImporter` signature.

- **The same namespace as the legacy host's shim, on purpose**, so porting
  an extension is the same change whichever host it's for. The two are
  separate assemblies that never share a process. Neither name contains
  "Fiddler" ("Don't get sued" stands). A ported extension can alias
  `Fiddler` to it inside its own project to keep qualified names
  compiling.
- **Clean-room as before:** signatures and documented behavior come from
  Telerik's published FiddlerCore API reference and the fiddlerbook.com
  object-model pages. Where a member is Fiddler Classic-only and
  undocumented, it comes from the ported extensions' own public call
  sites. No Fiddler source or binary was read.
- **This doesn't replace the `Exchange`-based decision above.** Live-traffic
  hooks still use `Exchange`. The Fiddler-shaped `Session` is only used by
  importers for now, and is converted to VioLens's native session on
  the way in (`ShimSessionConverter`). Whether live `IAutoTamper` hooks
  should also get the Fiddler-shaped `Session` is the next decision,
  prompted by the CSP extension (milestone 4 in the targets doc).
- **`ExtensionHost` recognizes both kinds.** An assembly with the
  Fiddler-shaped `[RequiredVersion]` is checked against the Fiddler API
  level the layer stands in for (any 5.0.x), not VioLens's version. Its
  importers are wrapped in `CompatShimImporterAdapter`, so File > Import
  treats them like native ones. Extension load contexts now always share
  assemblies the host already has loaded, so a stray copy of
  `Violens.Compatibility.dll` next to an extension can't create a
  second, non-matching copy of its interfaces.
- **Proven against real code, in CI, on both platforms:**
  `tests/ExtensionPorts/` fetches the upstream extension at a pinned
  commit, deletes its `using Fiddler;` lines, builds it and runs it
  through `ExtensionHost` on Windows and macOS.

## Update — live traffic hooks for ported extensions (Sept 2026)

The existing hook contract (`IExtensionAutoTamperHost`) builds a fresh
object for every hook, and has no way for an extension to answer a
request itself. Real Fiddler Classic extensions expect both: the CSP Rule
Collector marks a session in `AutoTamperRequestBefore` and answers its
report requests with `utilCreateResponseAndBypassServer()`.

- **`IExtensionSessionHost` (ProxyCore)** sits beside the old contract
  and doesn't replace it. The listener starts one `IExtensionSession`
  per request and calls `PeekAtRequestHeaders`, `RequestBefore`,
  `RequestAfter`, `PeekAtResponseHeaders`, `ResponseBefore` and
  `ResponseAfter` on it, in that order, then records the session with the
  session's `Flags`. `RequestBefore` can return a local response; the
  request then never reaches the server (or a request breakpoint), and
  only the response hooks run.
- **The upstream connection now opens only when needed**, after the
  request hooks, so a request an extension answers works even when its
  host doesn't resolve.
- **`ShimAutoTamperSet` (Compatibility)** implements it over the
  Fiddler-shaped `Violens.CompatShim.IAutoTamper`/`2`/`3`, with one
  `CompatShim.Session` per request. If FiddlerScript or a breakpoint
  replaces the request or response between hooks, the session picks up
  the change and keeps its flags.
- **`ExtensionHost`** now loads `CompatShim.IFiddlerExtension`
  implementations, calls their `OnLoad`/`OnBeforeUnload`, and exposes
  `CreateSessionHost()`.
- **`ExtensionUi.AddTab(title, view)`** is VioLens's own API for an
  extension's tab (an Avalonia control), shown beside Inspectors. It's
  what a fork calls in place of `FiddlerApplication.UI.tabsViews`.

Assumed, not confirmed against Fiddler Classic: the response hooks run for
a response an extension created; the request-after hooks don't. CONNECT
tunnels are still never passed to extensions, and `x-replywithtunnel` is
ignored: VioLens always answers CONNECT itself and opens the upstream
connection lazily, which is what that flag asked for.

## Update — menus, session-list columns and row colors (Sept 2026)

Ported to test these: Eric Lawrence's Privacy Scanner sample (see the
Extension Test Targets doc).

- **`Violens.CompatShim.FiddlerApplication.UI`** (type `frmViewer`, as
  in Fiddler) has `mnuMain`, `mnuTools` and `lvSessions`. Its `MenuItem`,
  `MenuItemCollection` and `MainMenu` are plain, observable objects shaped
  like the WinForms types extensions use; they never load WinForms.
  `MainWindow.axaml.cs` / `ExtensionMenus.cs` draw them as Avalonia menu
  items and keep them in step. Checked items get a check-mark icon rather
  than Avalonia's toggle, because WinForms-style handlers toggle `Checked`
  themselves.
- **`lvSessions.AddBoundColumn(title, order, width, flag)`** adds a grid
  column bound to `SessionRow.Flags[flag]`.
- **Row styling** reads `ui-backcolor`, `ui-color`, `ui-bold`,
  `ui-italic`, `ui-strikeout` and `ui-hide` (`SessionRowStyle`). The grid
  tags each row with classes as it's shown, and styles in
  `MainWindow.axaml` apply the colors; a selected row drops its custom
  background so the selection stays visible.
- **Peek-hook header edits** are kept: `ShimAutoTamperSet` applies header
  changes made in `OnPeekAtRequestHeaders`/`OnPeekAtResponseHeaders` to
  the full message, when nothing else changed those headers in between.
