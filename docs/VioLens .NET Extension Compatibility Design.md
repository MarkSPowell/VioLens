# VioLens .NET Extension Compatibility Design

How VioLens runs compiled .NET extensions, including ones written for
Fiddler Classic. The code is in `src/Violens.Compatibility/Extensions/`,
`src/Violens.Compatibility/CompatShim/` and
`tools/Violens.LegacyExtensionHost/`. The Project Plan's "Fiddler Classic
compatibility review" explains why FiddlerScript was built first; the
FiddlerScript Compatibility Design covers that.

Sources are public only: the
[IFiddlerExtension documentation](https://fiddlerbook.com/fiddler/dev/IFiddlerExtension.asp)
on fiddlerbook.com, its object-model pages, and Telerik's published
FiddlerCore API reference. Where a member is undocumented, its signature
comes from the metadata of real extensions (names and types, never method
bodies). No Fiddler source or binary, and no extension's IL, was read.

## Three ways an extension runs

| | Main app, VioLens interfaces | Main app, `Violens.CompatShim` | Legacy extension host |
|---|---|---|---|
| For | Extensions written for VioLens | Fiddler Classic extensions ported from source | Fiddler Classic extensions that need WinForms types .NET 5+ removed |
| Runtime | .NET 10, in the app, one `AssemblyLoadContext` per extension | Same | .NET Framework 4.8, a separate process, Windows only |
| Session object | `Exchange` (shared with FiddlerScript) | A Fiddler-shaped `Session` | A Fiddler-shaped `Session` |
| Change needed | None | Usually just deleting `using Fiddler;` | Recompile against the shim, or retarget the `.dll`'s reference |

## Why a compiled Fiddler extension can't just be loaded

**Type identity.** .NET only treats a class as implementing an interface
if it's the same type: same namespace, name and defining assembly. A
VioLens `IFiddlerExtension` with exactly the same members as Fiddler's is
still a different type from `Fiddler.IFiddlerExtension`, and a compiled
extension refers to Fiddler's. So the main app's own interfaces are
compatible at the source level, not the binary level: with an extension's
source, you change a `using` and recompile; a compiled `.dll` can't be
dropped in.

**What real extensions use.** Five compiled extensions from
fiddlerbook.com's extension list were inspected with a small ECMA-335
metadata reader (`AustralianImages`, `ContentBlock`, `JSFormat`,
`SAZClipboard`, `Differ`; `SAZClipboard` was looked at first, with
`strings` and a PE header read). What they showed:

- All reference `Fiddler` without a public key, so a stand-in assembly
  could satisfy the reference; a strong-named reference would need
  Fiddler's private key. They reference versions `2.1.0.5` to `2.4.2.5`,
  which matters less than it looks, since the .NET Framework binder
  doesn't enforce exact versions for weak-named assemblies.
- All are .NET Framework 2.0 assemblies (`v2.0.50727`).
- Between them they use `IFiddlerExtension`, `IAutoTamper`,
  `IHandleExecAction`, `FiddlerApplication`, `Session`, the
  `ClientChatter`/`ServerChatter` and `HTTPHeaders` family, `CONFIG`,
  `Utilities`, `IFiddlerPreferences`, `HostList`, `Logger`,
  `CodeDescription`, `BasicAnalysis`, `SessionStates`,
  `RequiredVersionAttribute`, and two WinForms classes, `frmViewer` and
  `frmPrompt`, that Fiddler never documented.

**The WinForms wall.** Reading the member references showed that
`frmViewer` is Fiddler Classic's main window, and extensions reach into its
controls directly: `AustralianImages` and `SAZClipboard` use `mnuTools`
(`MenuItem`); `ContentBlock` uses `GetSelectedSessions()`, `sbpInfo`
(`StatusBarPanel`), `mnuMain` (`MainMenu`) and `mnuSessionContext`
(`ContextMenu`), plus `frmPrompt.GetUserString(string, string, string,
bool)`; `JSFormat` uses `mnuRules`, `mnuSessionContext` and `sbpInfo`;
`Differ` uses `tabsViews` (`TabControl`) and `actDoCompareSessions`.

`MenuItem`, `MainMenu`, `ContextMenu` and `StatusBarPanel` were removed
from WinForms when it moved to .NET Core
([Microsoft's list](https://learn.microsoft.com/en-us/dotnet/core/compatibility/windows-forms/5.0/winforms-deprecated-controls);
[dotnet/winforms#2614](https://github.com/dotnet/winforms/issues/2614)
shows the `TypeLoadException`). So four of the five can't run unmodified
in any .NET 5+ process, whatever the UI toolkit: they fail to load before
any Fiddler stand-in matters. Only .NET Framework still has those types.
Switching VioLens's own UI from Avalonia to WinForms wouldn't help either,
unless the whole app also moved back to .NET Framework, giving up macOS
and Linux. Hence the legacy extension host, below.

## The main app's interfaces

In `src/Violens.Compatibility/Extensions/`, with signatures from the
public documentation:

- **`IFiddlerExtension`**: `OnLoad()`, `OnBeforeUnload()`.
- **`IAutoTamper : IFiddlerExtension`**: `AutoTamperRequestBefore`,
  `AutoTamperRequestAfter`, `AutoTamperResponseBefore`,
  `AutoTamperResponseAfter`, `OnBeforeReturningError`, each taking an
  `Exchange`.
- **`IAutoTamper2`** adds `OnPeekAtResponseHeaders`, and **`IAutoTamper3`**
  adds `OnPeekAtRequestHeaders`.
- **`IHandleExecAction`**: `bool OnExecAction(string sCommand)`. The
  documentation doesn't spell out the return value; VioLens takes `true` as
  "handled, stop looking".

They take [`Exchange`](../src/Violens.Compatibility/FiddlerScript/Exchange.cs),
the object FiddlerScript's handlers get, rather than a second session
type. Someone who knows Fiddler finds the same members
(`oSession.hostname`, `oSession.oRequest`, `oSession["ui-color"]`), and
there's only one set of code turning edits back into requests and
responses. `Exchange` is an ordinary C# class, so an extension doesn't need
Jint.

**Loading (`ExtensionHost`).** It scans its folders (not their
subfolders) for `*.dll`: the user's Extensions folder first, so their
copy wins, then the extensions bundled with the app. Each `.dll` loads into
its own collectible `AssemblyLoadContext`, with an
`AssemblyDependencyResolver` so it can bring its own dependencies;
assemblies the app has already loaded are always shared, so a stray copy
of `Violens.Compatibility.dll` beside an extension can't create
mismatching interface types. `RequiredVersion` works as Fiddler documents
it: an assembly without the attribute is skipped silently, one with an
unreadable or too-high version is skipped with an error, and one that
passes is searched for types with a parameterless constructor that
implement any extension interface. Each type gets one instance, listed
under every interface it implements. Errors are collected in
`LoadErrors`. Extensions load once at start-up and unload at shutdown;
Fiddler doesn't reload compiled extensions either.

**Live traffic.** `InterceptingProxyListener` takes an optional
`IExtensionAutoTamperHost`, defined in ProxyCore and implemented by
`LoadedExtensionSet`, the same inversion as `IScriptRunner`. Request and
response handlers are another reason to buffer a body, like a script
handler, and run after FiddlerScript's and before the breakpoint check.
That order is an assumption, not checked against Fiddler. Each hook builds
one `Exchange` and passes it through every extension in turn, so each sees
the previous one's edits, as with Fiddler's shared `oSession`. One
extension throwing doesn't stop the others or the connection. The
`*After` hooks run once the message has been written, buffered or not.
`OnBeforeReturningError` isn't called: the proxy has no point where it
builds an error response to hang it on.

**Inspectors.** `IInspector2`, with `IRequestInspector2` and
`IResponseInspector2`, is adapted (`Inspector2Adapter`) into the app's own
`IInspector`, and `InspectorRegistry.CreateDefault(additional)` takes the
adapted ones. Fiddler's `AddToTab(TabPage)` and `GetOrder()` are left out:
they're WinForms, and VioLens's inspectors return data that the app draws.
What's kept is the part that lets an inspector transform a body: the
adapter sets `headers` (first, since inspectors often look at
Content-Type) and then `body`, and reads them back, showing the result as
UTF-8 text. An extension that drew its own WinForms control has nothing
equivalent here. Without `GetOrder()`, adapted inspectors sort after the
built-in ones.

**Import and export.** `ISessionImporter.ImportSessions` returns
`ImportedSession` records shaped like `SessionStore.Add`'s parameters,
rather than `Session` objects, because only the store hands out session
ids. `ISessionExporter.ExportSessions` gets real `Session`s, since export
only reads. `ProgressCallbackEventArgs` leaves out the documented
`PercentComplete` string, whose exact meaning couldn't be confirmed.
**File > Import via Extension** and **Export via Extension** list every
extension and format (`ProfferedFormats.ChoicesFrom`) in a picker when
there's more than one, and remember the last choice. The `options`
dictionary is always empty: an importer that needs a file asks for it
itself, as a Fiddler extension would.

**In the app.** `MainWindowViewModel` creates the `ExtensionHost` and
loads it in its constructor, hands its auto-tamper host to each new
listener on Start, folds its inspectors into the registry, and unloads it
on Dispose whether or not capture is running. The Extensions panel lists
each folder scanned (whether it exists and how many `.dll` files it has),
counts for each interface, and each load error, so finding out why an
extension didn't load never needs a console.

## `Violens.CompatShim`: Fiddler's shape, in the main app

Porting real extensions (see the Extension Test Targets doc) showed that
source compatibility through `Exchange` wasn't close enough. Porting Eric
Lawrence's NetLog importer would have meant rewriting most of its
1,700-line parser. So the main app also has a Fiddler-shaped layer,
`src/Violens.Compatibility/CompatShim/`, namespace `Violens.CompatShim`,
on .NET 10 on every platform. It has `Session` (with `BuildFromData`),
`HTTPRequestHeaders` and `HTTPResponseHeaders`, `Parser`, `SessionFlags`,
`SessionTimers`, `FiddlerApplication.Log`, `Prefs`, `DoNotifyUser` and
`ReportException`, part of `Utilities`, and Fiddler's own
`ISessionImporter` signature.

- It uses the same namespace as the legacy host's shim, so porting an
  extension is the same change for either. They're separate assemblies
  that never share a process. A ported extension can alias `Fiddler` to it
  in its own project to keep qualified names compiling.
- An assembly with the Fiddler-shaped `[RequiredVersion]` is checked
  against the Fiddler API level the layer stands in for (any 5.0.x), not
  VioLens's version. Its importers are wrapped in
  `CompatShimImporterAdapter`, so File > Import treats them like native
  ones, and imported sessions are converted to VioLens's own
  (`ShimSessionConverter`).

**Live traffic.** Real extensions expect one session object that lives
through every hook of a request, and a way to answer a request themselves:
the CSP Rule Collector marks a session in `AutoTamperRequestBefore` and
answers its report requests with `utilCreateResponseAndBypassServer()`.
`IExtensionAutoTamperHost` makes a new object for each hook and can't
answer, so ProxyCore has a second contract beside it,
`IExtensionSessionHost`:

- The listener starts one `IExtensionSession` per request and calls
  `PeekAtRequestHeaders`, `RequestBefore`, `RequestAfter`,
  `PeekAtResponseHeaders`, `ResponseBefore` and `ResponseAfter` in that
  order, then records the session with its flags. `RequestBefore` can
  return a response; the request then never reaches the server (or a
  request breakpoint), and only the response hooks run.
- The upstream connection opens after the request hooks, so a request an
  extension answers works even when its host doesn't resolve.
- `ShimAutoTamperSet` implements it over `Violens.CompatShim.IAutoTamper`,
  `2` and `3`, with one `CompatShim.Session` per request. If FiddlerScript
  or a breakpoint replaces the message between hooks, the session picks up
  the change and keeps its flags. Header edits made in the peek hooks are
  applied to the full message when nothing else changed those headers in
  between.
- `ExtensionHost` loads `CompatShim.IFiddlerExtension` implementations,
  calls `OnLoad` and `OnBeforeUnload`, and provides `CreateSessionHost()`.

Assumed rather than checked against Fiddler: response hooks run for a
response an extension created, and the request-after hooks don't. CONNECT
tunnels aren't passed to extensions, and `x-replywithtunnel` is ignored:
VioLens always answers CONNECT itself and opens the upstream connection
lazily, which is what that flag asked for.

**Menus, columns and row colors** (tested by porting Eric Lawrence's
Privacy Scanner sample):

- `Violens.CompatShim.FiddlerApplication.UI` (a `frmViewer`, as in
  Fiddler) has `mnuMain`, `mnuTools` and `lvSessions`. Its `MenuItem`,
  `MenuItemCollection` and `MainMenu` are plain objects shaped like the
  WinForms types, without WinForms. The app draws them as Avalonia menu
  items (`ExtensionMenus.cs`) and keeps them in step. Checked items get a
  check-mark icon rather than Avalonia's toggle, because WinForms-style
  handlers flip `Checked` themselves.
- `lvSessions.AddBoundColumn(title, order, width, flag)` adds a
  session-list column showing a session flag.
- Rows follow `ui-backcolor`, `ui-color`, `ui-bold`, `ui-italic`,
  `ui-strikeout` and `ui-hide` (`SessionRowStyle`). A selected row drops
  its custom background so the selection stays visible.
- `ExtensionUi.AddTab(title, view)` adds an extension's own tab (an
  Avalonia control) beside the inspectors. A fork calls it in place of
  `FiddlerApplication.UI.tabsViews`.

`tests/ExtensionPorts/` fetches the NetLog importer, the CSP Rule
Collector and the Privacy Scanner at pinned commits, ports them, builds
them and runs them through `ExtensionHost`, in CI on Windows, macOS and
Linux.

## The legacy extension host

For extensions that need the removed WinForms types:
`tools/Violens.LegacyExtensionHost/`, its own solution, kept out of
`VioLens.sln` so it can't affect the main build.

- **`Violens.LegacyExtensionHost.CompatShim`** (`net48`, unsigned) has
  every type and member the five extensions were found to use, with
  `frmViewer` and `frmPrompt` as real WinForms classes whose `MenuItem`,
  `MainMenu`, `ContextMenu`, `StatusBarPanel` and `TabControl` fields are
  real controls. Each member's signature traces to a member reference in a
  specific extension, noted in the file. The behavior of less central
  members (`CONFIG.GetPath`, `actDoCompareSessions`) is VioLens's own
  reasonable guess, marked as such. Plain `.saz` files are read and written
  for real (`SazArchive.cs`, from the published format); encrypted ones
  (`CONFIG.bUseAESForSAZ`) throw `NotSupportedException` rather than
  quietly reading nothing.
- **`Violens.LegacyExtensionHost`** (`net48`, x86) creates the real
  `frmViewer`, sets `FiddlerApplication.UI` before anything loads (an
  extension's `OnLoad` often adds to the menu straight away), and runs
  `LegacyExtensionLoader`, which works like `ExtensionHost` but loads
  with `Assembly.LoadFrom` into one AppDomain: .NET Framework has no
  `AssemblyLoadContext`, and a bad extension can only take down this
  process, not VioLens. The window has a Log tab showing load messages and
  anything logged through `FiddlerApplication.Log`.
- **x86**, because `AustralianImages.dll` is marked 32-bit only
  (`COMIMAGE_FLAGS_32BITREQUIRED`) and failed with
  `BadImageFormatException` in a 64-bit process. Fiddler Classic was a
  32-bit process, and AnyCPU extensions run fine in one.

**Results on a real machine**, before the naming change below: all five
extensions loaded with no errors (3 `IAutoTamper`, 1 `IHandleExecAction`;
their `OnLoad` added 2 items to `mnuTools` and 1 to `mnuRules`).
SAZClipboard's menu item opened its window, and its Load SAZ read a real
87-session capture. `Differ` crashes comparing sessions when one has no
Content-Type header (a 304 or a redirect, say): diagnostic logging showed
the header indexer returning null just before the crash, which is what
Fiddler documents ("If the header does not exist, returns null"). So
Differ has the same bug in real Fiddler, and VioLens doesn't invent a
header to hide it. Two shim fixes came out of this testing:
`HTTPMethod`, `UriScheme`, `RequestPath` and `HTTPResponseStatus` default
to an empty string rather than null, as Fiddler's would.

**Tests** (`Violens.LegacyExtensionHost.Tests`, `net48`, x86, in that
solution only):
- Always: a `.saz` write-and-read round trip from synthetic sessions
  (headers, bodies, flags, refusing encryption, an unreadable archive, a
  missing file), the header indexer returning null on a miss, and folder
  scanning edge cases.
- When the five real extensions are present
  (`%USERPROFILE%\Documents\VioLens\LegacyExtensions\`, or
  `VIOLENS_LEGACY_EXTENSIONS_DIR`; otherwise the tests pass without doing
  anything, since the DLLs aren't in the repository): they load, with the
  expected counts and menu items, nothing logs an exception, and each
  `IAutoTamper` hook runs against a synthetic session without throwing.
  `VIOLENS_LEGACY_TEST_SAZ` points a further test at a real capture.

The first run found a real bug: every session flag was silently lost on
the way through a `.saz`. `XDocument.Save` wrote a byte order mark,
`Encoding.UTF8.GetString` turned it into a leading U+FEFF, and
`XDocument.Parse` then failed inside a catch that only logged. Metadata is
now written without a BOM, and one is stripped on reading.

## Naming: "Don't get sued"

Fiddler was created by Eric Lawrence and was free from the start. Telerik
bought it in 2012, and Progress Software bought Telerik in 2014. In August
2026 Progress limited Fiddler Classic to non-commercial use (see its
[commercial-use page](https://www.telerik.com/fiddler/fiddler-classic/commercial-use)).
None of VioLens's research used Progress's binaries, so their license
doesn't govern it, but it made the naming question below matter more.

The legacy host's shim was first an assembly named `Fiddler`, so compiled
extensions would bind to it unmodified. An assembly presenting itself to
the runtime as Fiddler's raised a trademark question the project couldn't
settle on its own, so it was dropped, under a project rule: **don't get
sued.**

- The shim was renamed, first to `Violens.Fiddler` and then to
  **`Violens.CompatShim`**, as both namespace and assembly name. (Not
  `Violens.Compatibility`, which is the main app's unrelated layer.) An
  author reconnects with a namespace alias (`using Fiddler =
  Violens.CompatShim;`).
- Before loading anything, `FiddlerAssemblyReferenceInspector` reads a
  `.dll`'s assembly references (metadata only, nothing executed) and looks
  for one named `Fiddler`. A match becomes an `AssemblyMismatch`, which
  names the file, what it expects and what the host provides, and says
  what to do: recompile against `Violens.CompatShim`, or, without the
  source, change that reference with `ildasm`/`ilasm` or dnSpy. That edit
  is mechanical (a name and a version) and doesn't involve the extension's
  logic; the five extensions are plain, unobfuscated MSIL.
  `LegacyExtensionLoader` never loads a mismatched file, logs it, and
  `Program.cs` shows a summary.
- The cost: the five extensions no longer load unmodified.
  `RealExtensionLoadingTests` now checks that they're detected and
  explained. `PatchedExtensionTests` (with
  `VIOLENS_LEGACY_PATCHED_EXTENSIONS_DIR`) keeps the earlier checks for
  copies a developer has retargeted themselves. VioLens doesn't ship
  patched copies, since that would mean redistributing modified
  third-party binaries.

What's left is descriptive use: text saying what VioLens is compatible
with, and messages naming Fiddler to explain a mismatch. The project
treats the question as settled. Nothing here is legal advice.

A copy of SAZClipboard's source exists under CC BY-SA 3.0
([fiddler.wikidot.com/sazclipboard](https://fiddler.wikidot.com/sazclipboard)).
Share-alike doesn't fit an MIT project, so nothing is adapted from it.

## The legacy host's session bridge

`LegacyExtensionHostBridgeClient` is a second `IExtensionAutoTamperHost`
that runs `IAutoTamper` hooks in the legacy host process over a named
pipe, and hands back what the extensions changed. The listener and
`LoadedExtensionSet` didn't change: the app wraps both hosts in a
`CompositeExtensionAutoTamperHost`, which runs them in turn on one
message, so the listener still sees one host.

- **A named pipe**, because both processes are on the same machine and a
  pipe can't be reached from another one. Its name ends in a version
  (`.v1`), so two builds with different protocols fail to connect rather
  than misread each other.
- **The wire format** is its own `netstandard2.0` project
  (`Violens.LegacyExtensionHost.Bridge`), the one target both sides can
  reference: plain records mirroring `CapturedRequest` and
  `CapturedResponse`, a message kind for each hook (plus a capabilities
  probe), and length-prefixed JSON. Each side maps to its own types.
- **Never required.** `Probe` tries once, with a 250 ms timeout, each time
  capture starts (not at launch, since the host may be started later). If
  nothing answers, or the host has no `IAutoTamper` extensions, every hook
  passes straight through for that run. Each call has a 3-second limit
  (`RoundTripTimeoutMilliseconds`), enforced by disposing the pipe from
  another thread, since a blocked synchronous pipe read can't be
  cancelled. Every failure returns the message unchanged and is logged.
- **One connection per call**, so concurrent requests can't interleave on
  one stream; the server listens with four pipe instances, so one slow
  extension doesn't hold up the others.

Not carried across: the HTTP version (the shim's `Session` has no member
for it), and repeated headers such as several `Set-Cookie`s, which
collapse to the last one after passing through an extension (logged by
`SessionMapping.SetHeaders`). Only `IAutoTamper` is bridged; inspectors,
import and export, and exec actions aren't, since none of the five
extensions use them.

Tests: `SessionBridgeRunnerTests` (mapping and several extensions on one
session, with fake extensions), `SessionBridgeServerTests` (a real pipe
and server) and `CompositeExtensionAutoTamperHostTests` in ProxyCore.

## Launching the legacy host

- **Opt-in.** **Launch the legacy extension host automatically on Start**
  (`AutoLaunchLegacyHost`, off by default) starts it with capture and
  stops it with capture, so nobody gets a second process they didn't ask
  for.
- **A fixed place:** `LegacyHost\Violens.LegacyExtensionHost.exe` beside
  the app (`LegacyExtensionHostLauncher.ExpectedExecutablePath`), not a
  setting.
- **Visible status:** the Legacy Extension Host panel (**Tools** menu)
  shows the switch and what the last Start or Stop did.

`EnsureRunning` first checks whether anything answers on the pipe
(`Ping`, which, unlike `Probe`, doesn't care whether extensions are
loaded, so a running host with none isn't started twice). If not, it
starts the `.exe` and waits up to five seconds for the pipe, so the probe
that follows finds it. `StopIfLaunchedByUs` only stops a process this
launcher started: `CloseMainWindow()` first, so extensions' `OnBeforeUnload`
runs, then `Kill()` if it doesn't exit. `Dispose` calls it too, in case the
host started but capture didn't.

A development build copies the host's output into the app's
`LegacyHost` folder after building
(`CopyOutputToDesktopUiLegacyHostFolder`, on the host's project, so the
main build knows nothing about it). The Windows release builds it
(`publish/legacyhost`), and the installer offers it as an unchecked task
that copies it to `{app}\LegacyHost`.

## Alternatives considered

- **Converting the app to WinForms.** Doesn't bring the removed types
  back unless the app also returns to .NET Framework, which would end
  macOS and Linux support. Rejected.
- **A dynamic-proxy bridge** (`Reflection.Emit` or Castle DynamicProxy)
  intercepting a legacy extension's calls. It doesn't avoid the
  assembly-identity problem (the runtime resolves the `Fiddler` reference
  before any interceptor runs), proxies can't intercept field access, and
  much of the surface real extensions use is fields
  (`frmViewer.mnuTools`, `Session.oRequest`, `HTTPRequestHeaders.HTTPMethod`).
  An `AssemblyLoadContext` stays in the same .NET 10 runtime, without the
  WinForms types. Rejected.
- **A source-compatibility package for extension authors.** That's what
  `Violens.CompatShim` in the main app turned out to be, with the
  limitation expected at the time: it can't supply a WinForms UI, so
  extensions with one need a fork.

## Validation

`tools/Violens.SampleExtension/` is an original extension with one class
for each interface (`SampleAutoTamper` implements the whole `IAutoTamper3`
chain), tested by `tests/Violens.SampleExtension.Tests/`. Dropped into the
Extensions folder, the app's Extensions panel showed it loaded with one of
each interface. Its README lists manual checks for live traffic, the
inspector tabs and import and export. `LoadedExtensionSetTests` covers the
live-traffic wiring with fake extensions.

## Not built, or not confirmed

- `OnBeforeReturningError`, and passing CONNECT tunnels to extensions.
- A WinForms-style UI from an `Inspector2` (adapted inspectors show text).
- Showing the legacy host's window inside VioLens's own (it's a separate
  window; reparenting across processes is fragile).
- On a real machine: the legacy host's automatic launch and the
  five-second wait, stopping it with `CloseMainWindow`/`Kill`, and a
  release install with the legacy host task checked.
- Whether `ProgressCallbackEventArgs.PercentComplete` matters to real
  extensions.
