# Extension Test Targets

Four real, public Fiddler Classic extensions chosen as the first ones to
get working in VioLens. This doc checks each one against what VioLens
supports today, lists what's missing and proposes an order. It's based on reading each extension's public source.
No Fiddler source was consulted (see the clean-room policy in
CONTRIBUTING.md).

**Where things stand:** all four are covered. The NetLog importer and the
CSP Rule Collector run in VioLens on Windows and macOS; cookie viewing
is built in, with Eric Lawrence's cookie sample ported as a test of the
extension UI hooks; and image-bloat detection is built in, designed from
the ImageBloat blog posts' description of what it does (no code from them). The Status table below is current; the gap analysis after it was
written before the work started and is kept for its reasoning.

| # | Extension | Source | License | Fiddler surface | UI coupling |
|---|---|---|---|---|---|
| 1 | NetLog importer | [ericlaw1979/FiddlerImportNetlog](https://github.com/ericlaw1979/FiddlerImportNetlog) @ `1a927f4` | BSD-3 | `ISessionImporter` | None (one file-open dialog) |
| 2 | Cookie/P3P scanner sample | [Telerik docs](https://www.telerik.com/fiddler/fiddler-classic/documentation/extend-fiddler/cookieextension) | Docs sample | `IAutoTamper2` | Menu, session-list column, row colors |
| 3 | ImageBloat | Source in [Telerik blog post](https://www.telerik.com/blogs/identifying-image-bloat-part-two); **its GitHub repo isn't publicly available** | Unknown | `IAutoTamper3` | One Rules-menu item, row colors |
| 4 | CSP Rule Collector | [ericlaw1979/CSP-Fiddler-Extension](https://github.com/ericlaw1979/CSP-Fiddler-Extension) @ `b60444e` | MIT | `IAutoTamper3` | A whole WinForms tab |

## Status

| # | Extension | State |
|---|---|---|
| 1 | NetLog importer | **Ported (milestones 1 and 2).** Builds from upstream with only its three `using Fiddler;` lines deleted. Loads through `ExtensionHost` and imports on both platforms in CI (the extension-ports test step in `ci.yml`). See "NetLog port: known differences" below. Included with the installers as an optional extension. |
| 2 | Cookie/P3P sample | **Built in, and ported as a test.** Cookie viewing is built in (the Cookies tab, `CookiesInspector`). The sample itself (Eric Lawrence's Privacy Scanner) is also ported, from Telerik's docs repo at a pinned commit, as the test of extension menus, flag-bound session-list columns and row colors. Also included with the installers as an optional extension, off by default. |
| 3 | ImageBloat | **Built in.** VioLens's own `ImageBloatAnalyzer` finds bytes that don't change how a PNG, JPEG, GIF or WebP looks (metadata, comments, text, trailing data), shown in an Image Bloat column (heavily bloated rows are tinted) and on the ImageView tab. Designed from the published description of the extension's behavior; its rules come from the format specifications, not its code. Not done: the extension's in-page overlay, which rewrites images in flight. |
| 4 | CSP Rule Collector | **VioLens-only fork** ([MarkSPowell/CSP-CLeARINET-Extension](https://github.com/MarkSPowell/CSP-CLeARINET-Extension), builds `VioLensCSP.dll`). Its traffic code runs on the new per-request hooks (`ShimAutoTamperSet`) with only renames; its tab is rewritten in Avalonia. Fiddler Classic users keep the original. Headless tests in `tests/ExtensionPorts`, built from the fork at a pinned commit. Tried in the app against real sites. The fork also fixes two things that affect the original: current browsers report inline code and `eval()` as the keywords `inline`/`eval` (the original turned them into `inline:`), and rule order no longer depends on the machine's language settings. It adds **Tools > Hide CSP Report Requests**. Included with the installers as an optional extension. |

## Port or build in?

Before porting any extension, whether one of these four or a later one,
decide whether its feature belongs in VioLens itself. Porting proves
compatibility; building in serves users better when the feature is
something almost everyone needs. Record the verdict and the reason here.

**Build it into VioLens when most of these are true:**

- Most users would want it. Examples: it's standard HTTP behavior
  (cookies, caching, compression), or it's a Fiddler Classic built-in
  people expect.
- It's small and well specified, usually by an RFC or a web standard,
  so a clean-room version is easy to write and test.
- The extension is a sample or demo, or it's unmaintained.
- Porting it is blocked by something we can't fix: missing source, an
  unclear license, or a Windows-only dependency.

**Port it when most of these are true:**

- It's specialist, and only some users need it.
- Its author maintains it, so a port keeps picking up upstream fixes and
  the author stays the owner.
- Its logic is large or subtle, and the author's code is the reference
  (the NetLog format, for example).
- Getting it working proves an extension API that other extensions also
  use.

**Either way:**

- A built-in feature is our own clean-room code, designed from the
  behavior and the relevant standards. It never copies the extension's
  source, and it never uses the extension's name or branding.
- If we build in something an extension already does, tell its author.
- Features that a port forces us to build (stateful hooks, extension
  menus and columns) belong in the base app anyway, because every
  extension benefits from them.

**Verdicts:**

| Extension | Verdict | Why |
|---|---|---|
| NetLog importer | Port (done) | Large, subtle parser that its author maintains. It proved `ISessionImporter` compatibility. |
| Cookie/P3P sample | Build in: a Cookies inspector that parses `Cookie` and `Set-Cookie` with their attributes | Every web developer needs cookie inspection. It's well specified (RFC 6265), and the extension is a docs sample. P3P is obsolete, because no current browser honors it, so it gets no dedicated feature: the header still shows on the Headers tab. The sample is still worth porting, since it's the smallest real user of the menu, column and row-color hooks; it's offered as an optional extension, off by default. |
| ImageBloat | Build in (proposed): flag images whose size is far larger than their pixel dimensions justify, or that carry large metadata | Its source and license are unconfirmed, and `System.Drawing` is Windows-only. Every front-end developer benefits. Design it clean-room from the behavior. |
| CSP Rule Collector | Port | It's specialist, MIT-licensed, and maintained. It forces two proxy features that many extensions need: one session object kept across a request's hooks, and extensions answering requests themselves. Its WinForms tab gets an Avalonia rewrite. Revisit building it in if CSP work proves popular. |

## The central finding: today's "source-level" compatibility isn't close enough yet

*Since resolved: the Fiddler-shaped layer proposed here now exists
(`Violens.CompatShim`, in the main app, on both platforms). Kept for
the reasoning.*

The .NET Extension Compatibility Design doc promises that an extension
with source can be ported with "a new `using`/base-interface and a
recompile." Against real extensions, that doesn't hold yet, on either of
VioLens's two extension paths:

- **The main app's path** (`Violens.Compatibility`, net10.0,
  cross-platform) uses VioLens-native shapes: `Exchange` instead of
  `Session`, and `ISessionImporter` returning
  `IReadOnlyList<ImportedSession>` instead of `Session[]`. Every
  `Session.BuildFromData`, `HTTPRequestHeaders`,
  `FiddlerApplication.Log` and `Utilities.*` call would have to be
  rewritten. For the NetLog importer that means touching most of its
  1,700-line parser. That's a rewrite, not a port.
- **The legacy host's path** (`Violens.CompatShim`, net48) is shaped
  like Fiddler's own API, but it only covers members seen in five other
  extensions. It only bridges `IAutoTamper`, not importers, and it only
  runs on Windows.

**For Windows/macOS parity, the fix belongs in the main app's path.**
The legacy host can never run on a Mac. The proposal below is to add a
Fiddler-shaped source-compatibility layer to the main app that runs on
net10.0 on both platforms. The goal is that porting one of these
extensions means changing `using Fiddler;` to the VioLens namespace,
retargeting the project to net10.0 and nothing else.

Two useful side effects:

- `FiddlerApplication.Prefs` can be backed by the new `PreferenceStore`.
  Its API was deliberately shaped like `IFiddlerPreferences`, so this is
  a thin adapter. The CSP extension and the cookie sample both need it.
- The size of each port's diff becomes a direct, trackable measure of
  tenet 1. The target is **one line changed per source file**, plus the
  project file.

## Per-extension gap analysis

*Written before the work started. For what each extension ended up
needing, see Status above and the sections after the milestones.*

### 1. NetLog importer (start here)

The right one to start with. It has no UI coupling, and it's
the only one that exercises the import path. A working NetLog import also
closes a README "Known limitations" item ("No HAR or Chromium Netlog
import").

**What it uses and VioLens lacks today:**

- `ISessionImporter.ImportSessions(string, Dictionary<string,object>, EventHandler<ProgressCallbackEventArgs>)` returning `Session[]`.
  VioLens's version has a different signature and return type.
- `[ProfferFormat(name, description, extensions)]`. It uses the
  three-argument form. VioLens's attribute only takes two.
- `Session.BuildFromData(bool, HTTPRequestHeaders, byte[], HTTPResponseHeaders, byte[], SessionFlags)`.
  Nothing equivalent exists.
- `HTTPRequestHeaders(string path, string[] headers)`,
  `HTTPResponseHeaders(int, string, string[])`, plus `.Add`,
  `.RenameHeaderItems` and the indexer.
- `Fiddler.Parser.ParseRequest` / `ParseResponse`.
- `SessionTimers`, with nine timestamp fields, assignable via `oS.Timers`.
- `SessionFlags`: `ImportedFromOtherTool`, `RequestGeneratedByFiddler`,
  `ResponseGeneratedByFiddler`, `ServedFromCache`, `ResponseBodyDropped`,
  `RequestBodyDropped`, `ResponseStreamed`.
- `oS[flag]` string flags and `oS.fullUrl`.
- `FiddlerApplication.Log.LogFormat` / `LogString`, `DoNotifyUser` and
  `ReportException`.
- `Utilities.emptyByteArray`, `GzipExpand`, `ByteArrayToHexView`,
  `ObtainOpenFilename`, `UNSTABLE_DescribeClientHello` and
  `UNSTABLE_DescribeServerHello`.

**Parity notes:**

- `ObtainOpenFilename` is a WinForms dialog in Fiddler. In VioLens it
  has to route to Avalonia's cross-platform file picker through a
  host-provided callback. It isn't called when the host passes
  `Filename` in the options, which VioLens's File > Import can always
  do.
- `UNSTABLE_DescribeClientHello` / `ServerHello` produce descriptive
  text only. A reasonable first cut returns a hex dump plus the parsed
  TLS version, and says so in the output.
- It uses `System.IO.Compression.ZipArchive`, which works on both
  platforms.

**How to test:**

- Build the upstream source at a pinned commit, with the one-line
  `using` change, against the compatibility layer in CI on both legs.
- Import small, **synthetic** NetLog JSON fixtures written for this
  project (plain JSON, gzip and zip variants) and assert on the resulting
  sessions: URL, method, status, headers, body and timers.
- Don't commit a real browser capture. They contain cookies and
  personal data.

### 2. Cookie/P3P scanner sample (second)

This is the smallest one that exercises the three UI hooks shared by most
real extensions: a top-level menu (`FiddlerApplication.UI.mnuMain`), a
session-list column (`lvSessions.AddBoundColumn`) and row colors
(`oSession["ui-backcolor"]`).

**What's missing:**

- A UI-neutral adapter for those three hooks, mapped onto Avalonia. The
  FiddlerScript work already built the Avalonia side: script-declared
  menus and `[BindUIColumn]` columns. This is mostly wiring.
- `ui-backcolor` isn't honored by the session grid at all yet.
- `FiddlerApplication.Prefs` (backed by `PreferenceStore`, see above).
- `IAutoTamper2.OnPeekAtResponseHeaders` is already built in VioLens's
  interfaces. Check that the listener actually calls it.

**Parity note:** Fiddler's `MenuItem`/`ListView` types are WinForms. The
adapter exposes Fiddler-shaped stand-in types backed by Avalonia. It
never loads WinForms, which doesn't exist on macOS.

### 3. ImageBloat (third, once the source is confirmed)

**Superseded:** built in as VioLens's own feature instead (see the Status
table). The notes below are kept for the reasoning.

**Its GitHub repo isn't publicly available.** The source is published in
the Telerik blog post, but that post's license isn't stated. **Don't build
against it until the repo or a license for the blog listing is
available.**

**What it uses:** `IAutoTamper3`, `FiddlerApplication.UI.mnuRules`, a
`GetStringPref` color preference, `oResponse.MIMEType`, `bBufferResponse`,
`isAnyFlagSet`, `utilDecodeResponse`, `responseBodyBytes` and
`ui-backcolor`.

**Parity blocker:** it redraws the image with `System.Drawing` (`Bitmap`,
`Graphics`, `SolidBrush`). Since .NET 6, `System.Drawing.Common` only
works on Windows. On macOS it throws at runtime. So this one **cannot**
be a "one line changed" port on both platforms. Options, to decide when
we get there:

- Keep it Windows-only and say so in the UI.
- Port the drawing code to SkiaSharp, which Avalonia already ships on
  both platforms. That's a real code change in the extension, which
  should be offered upstream rather than carried here.

### 4. CSP Rule Collector (last)

**What it uses:**

- `IAutoTamper3`
- `FiddlerApplication.Prefs` (Get/SetBoolPref)
- `session.utilCreateResponseAndBypassServer()`, `ResponseBody`,
  `GetRequestBodyAsString()`, `isTunnel`, `isHTTPS`, `isFTP`, `fullUrl`
  and `PathAndQuery`
- `oResponse.headers.Add`. It adds **two headers with the same name**,
  and `ExchangeHeaders` has no `Add` today, only set-by-name.
- The `x-replywithtunnel` flag
- `Utilities.LaunchHyperlink`

**Proxy gaps to fix first:**

- A response created in `AutoTamperRequestBefore` must be honored, so
  the request never reaches the real server.
- `x-replywithtunnel` must answer a CONNECT locally. The report host
  (`fiddlercsp.deletethis.net`) may not even resolve anymore, so both
  are required, not optional.

**Parity blocker:** its whole UI is a WinForms `UserControl` added to
`FiddlerApplication.UI.tabsViews`. That can't render on macOS or inside
the Avalonia app on Windows. Realistic options:

1. Port `RuleCollectionView` (two checkboxes, a list and a text box) to
   Avalonia as part of the port. The UI part is then no longer "one line
   changed".
2. Define a small UI-neutral "extension tab" contract and offer that
   upstream as an optional path for extensions.

The non-UI half (header injection, report collection, rule generation)
can be tested fully and headlessly on both platforms before the UI
question is settled. `CSPRuleCollector` is plain C#, and the repo already
has unit tests for it.

## Proposed order and milestones

1. **Fiddler-shaped compatibility layer, core types.** `Session` (backed
   by VioLens's own session, so there's still one set of mutation
   plumbing), `HTTPRequestHeaders`/`HTTPResponseHeaders`, `SessionFlags`,
   `SessionTimers`, `Parser`, `FiddlerApplication.Log`/`Prefs`/
   `DoNotifyUser`/`ReportException`, and the `Utilities` subset above.
   Everything clean-room from public docs and the extensions' own public
   source (call sites only). **Done.**
2. **NetLog importer runs on both platforms.** Wire the Fiddler-shaped
   `ISessionImporter` to File > Import, and add a CI job that clones the
   pinned commit, applies the one-line port and runs the fixture tests on
   Windows and macOS. Done, and confirmed importing in the app on
   Windows. With a second importer installed, File > Import silently used
   the first one, so a format picker (FormatPickerWindow) followed.
   **Done.**
3. **Extension UI hooks and a built-in Cookies inspector.** The menu,
   column and row-color adapters, plus `ui-backcolor` in the grid, tested
   with the cookie sample as a fixture. The Cookies inspector is
   VioLens's own feature (see "Port or build in?"). **Done**, plus
   Edit > Remove Selected/All Sessions.
4. **CSP.** Bypass responses, one session object per request, and
   multi-value `headers.Add`; the tab became an Avalonia rewrite in a
   VioLens-only fork. `x-replywithtunnel` turned out not to be needed:
   VioLens always answers CONNECT itself. **Done.**
5. **Image-bloat detection built in** (see "Port or build in?"). It
   replaces porting ImageBloat. **Done** (column and ImageView breakdown;
   the in-page overlay isn't).

## NetLog port: known differences

The port imports the same sessions, with these differences from running
it inside Fiddler Classic. None of them needed a change to the importer's source.

- **Server certificates in the `/SOCKETS` summary session show raw
  rather than parsed.** The importer calls `X509Certificate2.Import`,
  which modern .NET no longer supports. The importer catches the error and
  falls back to raw certificate text. Loading with the
  `new X509Certificate2(byte[])` constructor instead works on both .NET
  Framework and modern .NET, so a one-line upstream change would fix it
  everywhere. Worth offering upstream.
- **TLS ClientHello/ServerHello descriptions** in that same summary use
  VioLens's own text layout (`Utilities.UNSTABLE_DescribeClientHello`
  is undocumented, so it was written from the TLS RFCs). The two
  properties the importer relies on hold: the first two lines are a
  header, and a TLS 1.3 ServerHello contains `supported_versions\tTls1.3`.
- **Only the start time survives.** VioLens's native session has one
  timestamp, so the importer's other `SessionTimers` values are dropped on
  import.
- **String flags are kept, `BitFlags` aren't.** Flags such as
  `X-Netlog-URLRequest-ID` and `ui-backcolor` are stored on the session
  and shown on the request side's **Notes** tab, with a plain-English
  explanation for the ones the NetLog importer sets. They aren't written
  to SAZ yet. Row flags (`ui-backcolor` and friends) now style the row in
  the session list. Flag names come back lower-cased, as they do in
  Fiddler.
- **Warnings go to the status line**, not a dialog
  (`FiddlerApplication.DoNotifyUser`).
- **Ports are always built in Release.** The importer's JSON parser calls
  `Debug.Assert(false)` when it meets a truncated capture, just before
  the importer repairs the file. Shipped builds compile that out; a Debug
  build makes it throw under the test runner. That's upstream behavior,
  not a compatibility-layer bug, so the port project defines only `TRACE`
  regardless of configuration.

## Privacy Scanner port: what it needed

Eric Lawrence's cookie/P3P sample is the smallest real extension that uses the UI
hooks most extensions use. Its port deletes its `using Fiddler;` and
`using System.Windows.Forms;` lines and drops the `System.Windows.Forms.`
prefix from `MenuItem` (seven places); nothing else changes. To run, it
needed three things VioLens now has:

- **`FiddlerApplication.UI`**: a stand-in for Fiddler's main window with
  `mnuMain` (top-level menus, shown before Help), `mnuTools` (Tools menu
  items) and `lvSessions.AddBoundColumn` (a session-list column showing a
  session flag). `MenuItem` is VioLens's own, WinForms-shaped
  (`Text` with `&` access keys, `Checked`, `Enabled`, `MenuItems`,
  `Click`); the app draws it with Avalonia on both platforms.
- **Row styling from flags**: `ui-backcolor`, `ui-color`, `ui-bold`,
  `ui-italic`, `ui-strikeout` and `ui-hide`. A background without a text
  color gets black or white text, whichever reads better, so rows stay
  readable in dark mode.
- **Header edits while peeking are kept.** The sample renames an invalid
  `P3P` header in `OnPeekAtResponseHeaders`; as in Fiddler, that change now
  reaches the client (unless something else changed the headers first).

Not there yet: `mnuRules` and the session context menu
(`mnuSessionContext`), and columns computed by a delegate rather than
read from a flag.
