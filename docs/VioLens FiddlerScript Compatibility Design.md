# VioLens FiddlerScript Compatibility Design

How VioLens runs Fiddler Classic's `CustomRules.js` scripts. The code is in
`src/Violens.Compatibility/FiddlerScript/`. The Project Plan's "Fiddler
Classic compatibility review" explains why FiddlerScript came before
compiled extensions, and why the scope includes Context Actions, Tools
actions and custom columns.

Everything here comes from public documentation: Telerik's Fiddler Classic
docs, the public `Content/SampleRules.js` in `ericlaw1979/Clearinet`, and
fiddlerbook.com's cookbook. No script text is copied from them; the test
scripts in `tests/Violens.Compatibility.Tests` were written for this
project in the same style.

## Engine: Jint, not JScript.NET

Fiddler Classic compiles `CustomRules.js` with `Microsoft.JScript`
(JScript.NET), which was never ported beyond .NET Framework. It isn't
available on .NET 10, and the request for it is still open
([dotnet/runtime#27155](https://github.com/dotnet/runtime/issues/27155)).
So a different engine has to run scripts behind the same script-facing
objects.

**[Jint](https://github.com/sebastienros/jint)** (4.16.3) is that engine:
a maintained ECMAScript interpreter written in C#, targeting `net10.0`,
with CLR interop (`Options.AllowClr()`) and ES2022 support, including
static class members.

**Roslyn scripting** (`Microsoft.CodeAnalysis.CSharp.Scripting`) is the
plan for the C# variant Telerik added later (`CustomRules.cs`), which is
ordinary C# and needs no preprocessing. It isn't built. Real scripts are
mostly the JScript.NET kind (Eric Lawrence's writing treats it as the
everyday form), so that came first.

## Naming: new type names, Fiddler's member names

Clearinet's `SampleRules.js` renames Fiddler Classic's `Session` and
`FiddlerObject` to `Exchange` and `AppObject`
(`static function OnBeforeRequest(oEx: Exchange)`), noting that SAZ files
still call it a "Session". VioLens uses `Exchange` and `AppObject` for the
.NET types. That costs nothing: the preprocessor removes type annotations
such as `oSession: Session` before Jint runs the script, and scripts never
name the .NET types.

Members are the other way round. Every property and method keeps Fiddler
Classic's exact name and casing (`hostname`, not `Hostname`;
`responseCode`, not `ResponseCode`), rather than Clearinet's partly renamed
set (`host`, `urlContains()`). The aim is to run the `CustomRules.js`
someone already has, written against Fiddler's names. Clearinet's newer
names could be added as aliases later.

## The script host

- **`Exchange`, `ExchangeRequest`, `ExchangeResponse`, `ExchangeHeaders`,
  `ExchangeFlags`**: the objects scripts see. They wrap an editable copy of
  the request and response (ProxyCore's `CapturedRequest` and
  `CapturedResponse` are immutable records), so a handler can change
  headers, the body and routing. `ToRequest()` and `ToResponse()` turn the
  edited copy back into records after each handler.
- **`AppObject`**: Fiddler's `FiddlerObject`. It has `StatusText`,
  `Log.LogString` and `LogFormat`, `alert()` (written to a log callback
  rather than a modal dialog, so tests and headless use don't wait for a
  click), `playSound()` (does nothing) and `ReloadScript()` (a host
  callback). `prompt()` and `utilIssueRequest()` throw
  `NotSupportedException`, so a script that needs them fails clearly
  instead of behaving as if the prompt was cancelled.
- **`FiddlerScriptPreprocessor`**: regular expressions, not a parser, that
  turn JScript.NET syntax into ECMAScript. It drops `import` lines,
  removes `[Attribute(...)]` blocks, turns `static function Name(...)` into
  ES2022's `static Name(...)`, and removes `: Type` annotations from
  variables, parameters and return types. One known gap (see the class's
  remarks): an object-literal value that looks like a bare identifier in
  parameter position can be removed by mistake. No real script has shown
  it.
- **`FiddlerScriptHost`**: loads one script and calls
  `Handlers.OnBeforeRequest`, `OnBeforeResponse` and
  `OnPeekAtResponseHeaders` with an `Exchange`. `AllowClr()` is on, because
  real cookbook scripts use `System.Text.StringBuilder`,
  `System.Diagnostics.Process` and so on. That gives the script full access
  to .NET, which is right for a script you wrote for yourself (Fiddler
  Classic trusts scripts the same way) and wrong for one from someone you
  don't trust.
- **`ExchangeCodec`**: behind `utilDecodeResponse()`. It handles gzip,
  deflate and Brotli, but not zstd, which would mean adding
  `ZstdSharp.Port` to this project; a zstd body throws
  `NotSupportedException` rather than returning garbage. It repeats
  `RawTextInspector`'s decoding on purpose (see that class's remarks).

Tests: `FiddlerScriptPreprocessorTests` (string transforms),
`ExchangeTests` (no Jint), `FiddlerScriptHostTests` (scripts run through
the preprocessor and Jint) and `FiddlerScriptRunnerTests`.

## Running scripts against live traffic

`Violens.Compatibility` references `Violens.ProxyCore`, so
`InterceptingProxyListener` (in ProxyCore) can't reference the script
host directly. Instead ProxyCore defines a small interface,
`Violens.ProxyCore.Scripting.IScriptRunner`, with no Jint dependency, and
`FiddlerScriptRunner` implements it. The app creates the runner and hands
it to the listener, the same way it hands over `BreakpointManager` and
`AutoResponderRules`.

**The interface** has `HasOnBeforeRequest` and `HasOnBeforeResponse`
(cached when a script loads, so the listener can skip buffering a body
when the script has no handler for it), and `RunOnBeforeRequest` and
`RunOnBeforeResponse`, which take a session id, the host and the captured
message, and return the edited one. An earlier design also returned a
"force a breakpoint" flag, like `AutoResponderOutcome`'s; nothing would
have used it, so it was left out.

**Where scripts run.** Only for requests forwarded to the real server,
never for one AutoResponder answers (that path returns before the script
runner is consulted). Whether real Fiddler runs FiddlerScript before or
after AutoResponder hasn't been checked. On the forward path, a script
handler is one more reason to buffer a body, alongside a breakpoint or an
AutoResponder `*bpu`/`*bpafter` rule; with none of them, the body is
relayed as it arrives. When the body is buffered, the script's handler runs
first, and then `BreakpointManager` checks breakpoints against the edited
message, which is the order real Fiddler uses. A script can set
`oSession["x-breakrequest"]` or `x-breakresponse`, but nothing reads those
flags yet (see `BreakpointManager`'s remarks): they don't pause a session
unless a breakpoint condition also matches.

**Session ids.** A script sees `oSession.id`, from
`SessionStore.PeekNextId()`. The store only assigns an id when it adds the
finished session, which for a request handler hasn't happened yet. With
several connections at once another session can take that id first, so
it's a best guess, not a reservation.

**Loading and reloading.** `FiddlerScriptRunner` has `LoadFromFile`,
`Reload` and `LoadFromSource`, and is `FiddlerObject.ReloadScript()`'s
callback, so a script can reload itself. A reload that fails (a syntax
error, or an error in the script's top-level code) keeps the script that
was already running, as Fiddler does when an edit to `CustomRules.js` is
broken; `LoadError` holds the reason for the app to show. A handler that
throws is caught and logged, and the unedited message goes through.

The runner isn't thread-safe, and neither is Jint's `Engine`. The listener
handles each connection on its own task, with no lock around the shared
runner, so a reload can overlap a handler running on another connection.
That's accepted, in the same way that editing an AutoResponder rule can
overlap the next request.

**In the app**, **Tools > FiddlerScript** shows a panel with the script's
path, Load and Reload buttons, and a line listing the handlers the script
defines, or its load error.

**Not done:**
- Scripts don't run for requests AutoResponder answers.
- `x-breakrequest` and `x-breakresponse` don't pause on their own.
- Nothing calls a script's `OnPeekAtResponseHeaders` yet.
- `Exchange.hostname` (to send a request elsewhere) and
  `Exchange.bypassGateway` are recorded but don't change where the request
  goes.
- No lock around a runner shared by several connections.

## Rules menu, actions and columns

From Telerik's "Customize Menus" and "Add Columns to Web Sessions List"
articles, and a Telerik blog post on `BindPref`, the only public
description of it found. The test scripts are original.

**`FiddlerScriptDirectiveScanner`** reads the script's source before the
preprocessor removes the attributes, looking for `RulesOption`,
`RulesString` and `RulesStringValue`, `BindPref`, `ContextAction`,
`ToolsAction` and `BindUIColumn`. Like the preprocessor, it uses regular
expressions: each attribute has to sit directly above its field or method,
with no comment or blank line between them. A script laid out differently
still loads and runs; it just doesn't get that menu entry or column.

Each attribute becomes a small record (`RulesMenuOption`,
`RulesMenuStringOption` and `RulesStringChoice`, `BindPrefBinding`,
`ContextActionDescriptor`, `ToolsActionDescriptor`, `UIColumnDescriptor`),
gathered into a `FiddlerScriptDirectives` for the script
(`FiddlerScriptRunner.Directives`, or `FiddlerScriptDirectives.Empty` when
nothing is loaded). `FiddlerScriptHost` reads, writes and calls what they
describe (`GetRulesOptionValue`, `SetRulesOptionValue`,
`GetRulesStringValue`, `SetRulesStringValue`, `InvokeContextAction`,
`InvokeToolsAction`, `ComputeUIColumnValue`), and each method refuses a
field or method name that the scanner didn't find.

**The Rules menu** is rebuilt from `MainWindowViewModel.RulesMenuEntries`
whenever a script loads. It's one flat list rather than nested submenus: a
flat `ItemsSource` is a well-tested Avalonia pattern, and a recursive menu
template wasn't something that could be tested at the time. An option in a
submenu is labelled "Submenu: Option" instead. Picking a radio option
(`IsRadio`, sharing a `SubmenuName`) or a `RulesString` choice clears the
other options' script fields as well as their check marks, so the script
never has two options of a group set at once.

**`BindPref`** values are kept by `FiddlerScriptPreferenceStore` in
`fiddlerscript-prefs.json` in VioLens's data folder. Fiddler loads them
when the script starts and saves them when it unloads. VioLens applies a
saved value when the script loads and saves each change straight away,
which survives a crash better and doesn't need an unload event. A field
with `[BindPref]` and no `RulesOption` or `RulesString` (like the
cookbook's `bpResponseURI`) gets its saved value at load, but a change the
script makes to it later isn't saved: there's no general way to watch a
Jint field being assigned. A preference whose name contains "Ephemeral"
(Fiddler's `fiddlerscript.ephemeral.*` convention) is kept in memory, so
it survives a reload but not a restart.

**Context actions and Tools actions.** `ContextActionEntries` fill the
session list's right-click menu, and `ToolsMenuEntries` become a Script
Actions row of buttons in the FiddlerScript panel; both are rebuilt when a
script loads. Tools actions were first a Tools menu of their own, but a
menu that's usually empty looked broken. Fiddler's context actions get
every selected session (`Session[]`); VioLens passes only the session that
was right-clicked, although the list can now select several. Each action
gets an `Exchange` for the session (`Exchange.ForResponse`), but edits it
makes aren't written back to the session store.

**Columns (`BindUIColumn`).** Fiddler's four overloads (name; name and
sort numerically; name and width; name, width and display order) become
one `UIColumnDescriptor`, told apart by the arguments: a boolean second
argument is `SortNumerically`, a number is `Width`. Each `SessionRow` has
`ScriptColumns`, computed once when the row is made, with
`FiddlerScriptRunner.ComputeUIColumnValue`. Rows captured before a script
loads or changes keep their old values, just as changing
`OnBeforeResponse` doesn't change responses already captured. The app adds
and removes the `DataGridTextColumn`s in code (`DataGrid.Columns` can't be
bound), each bound to `ScriptColumns[Title]`. `DisplayOrder` and
`SortNumerically` are read but not used yet: script columns go after the
built-in ones, in the order the script declares them.

**Not done:**
- Nested Rules submenus (shown as "Submenu: Option").
- Saving later changes to a `BindPref`-only field.
- Context actions on several selected sessions.
- Writing a context action's edits back to the session.
- `DisplayOrder` and `SortNumerically` for script columns.

All of this needed a working script host first, which is why the host was
built before the menus and columns.
