# Violens.LegacyExtensionHost

An optional .NET Framework 4.8 WinForms process for Fiddler Classic
extensions that use WinForms types removed in .NET 5 and later
(`MenuItem`, `MainMenu`, `ContextMenu`, `StatusBarPanel`). It runs beside
VioLens and works on VioLens's live traffic over a named pipe.
`docs/VioLens .NET Extension Compatibility Design.md` explains why this is
a separate process.

## What happens to an extension

Put an extension's `.dll` in `Documents\VioLens\LegacyExtensions\` and run
`Violens.LegacyExtensionHost.exe`.

- **Compiled against Fiddler's own `Fiddler` assembly** (every original
  extension): it isn't loaded. A dialog and the Log tab say what it expects
  (`Fiddler, Version=...`) and what this host provides
  (`Violens.CompatShim, Version=...`), and that it needs recompiling
  against `Violens.CompatShim`. See `AssemblyMismatch` and the design
  doc's "Naming: Don't get sued" section for why.
- **Recompiled or retargeted against `Violens.CompatShim`:** it's loaded,
  checked against `RequiredVersion`, and its `OnLoad()` runs, including
  extensions that add to the window's real `MenuItem`, `MainMenu`,
  `ContextMenu`, `StatusBarPanel` and `TabControl` controls. Two sample
  sessions are loaded so calls like `GetSelectedSessions()` return
  something.

The **Log** tab shows what was scanned and loaded, any mismatches or
errors, and anything logged through `FiddlerApplication.Log`. An assembly
without a `RequiredVersion` attribute is skipped without a message, as in
Fiddler.

## The session bridge

`SessionBridgeServer` listens on a named pipe
(`Violens.LegacyExtensionHost.SessionBridge.v1`) as soon as the host
starts. VioLens's `LegacyExtensionHostBridgeClient` uses it to run the
loaded extensions' `IAutoTamper` hooks on real traffic.

- VioLens checks for the host each time capture starts. If it isn't
  running, or has no `IAutoTamper` extensions, the hooks pass everything
  through for that run.
- Each hook call is its own connection, and the server listens with four
  pipe instances (`SessionBridgeServer.InstanceCount`), so concurrent
  requests don't wait for each other.
- A failure or a slow extension (over 3 seconds) lets the message through
  unchanged.
- The HTTP version can't be changed by an extension (the shim's `Session`
  has no member for it), and a repeated header such as several
  `Set-Cookie`s comes back as the last one, which is logged. See
  `SessionMapping`.
- Only `IAutoTamper` is bridged. None of the extensions this was built for
  use inspectors, import and export, or exec actions.

To try it end to end:

1. Build this folder's `Violens.LegacyExtensionHost.sln` and the main
   `VioLens.sln`.
2. Put a retargeted extension in `Documents\VioLens\LegacyExtensions\` and
   run the host. The Log tab's `[SessionBridge] Listening on pipe ...` line
   gives the number of `IAutoTamper` extensions loaded.
3. Start capturing in VioLens. Its console shows `[Extension] Session
   bridge reachable -- N AutoTamper extension(s) loaded there.`
4. Browse. Changes the extension makes show in VioLens's session list.
5. Close the host and start capturing again in VioLens: the hooks stop
   running, and capture carries on.

## Starting it from VioLens

**Tools > Legacy Extension Host** in VioLens shows a panel with **Launch
Violens.LegacyExtensionHost.exe automatically on Start**, off by default.
When it's on, Start launches the host (unless something already answers
on the pipe) from the `LegacyHost` folder beside VioLens, waits up to five
seconds for the pipe, and Stop closes it again. A host you started yourself
is never closed. The panel says what happened: already running, launched,
not found, or failed.

The `LegacyHost` folder gets there two ways:
- **Development builds:** this project's
  `CopyOutputToDesktopUiLegacyHostFolder` target copies its output into
  `apps/Violens.DesktopUi`'s output folder after each build.
- **Releases:** `release-windows.yml` builds it into `publish/legacyhost`,
  and the installer offers it as an optional task, unchecked by default,
  that installs it to `LegacyHost`. That hasn't yet been checked with a
  real installer.

## Building

You need the .NET Framework 4.8 Developer Pack, which Visual Studio's
desktop workloads include. This has its own solution so it can't affect
the main build:

```
dotnet build Violens.LegacyExtensionHost.sln
```

## Tests

```
dotnet test Violens.LegacyExtensionHost.sln
```

These always run:
- a `.saz` round trip through `SazArchive` with synthetic sessions, the
  header indexer returning null on a miss, and folder scanning edge cases;
- `SessionBridgeRunnerTests`: the bridge's mapping with fake extensions,
  including one that throws and a repeated header;
- `SessionBridgeServerTests`: the same over a real pipe and server
  (started once for the run by `SessionBridgeServerFixture`).

These need real extension DLLs, which aren't in the repository, and pass
without doing anything when they're missing:
- `RealExtensionLoadingTests`: the five original extensions
  (`AustralianImages`, `ContentBlock`, `JSFormat`, `SAZClipboard`,
  `Differ`) in `Documents\VioLens\LegacyExtensions\` or
  `VIOLENS_LEGACY_EXTENSIONS_DIR`. All five should be reported as
  mismatches with a useful message, and nothing should load or throw.
- `PatchedExtensionTests`: copies you've retargeted yourself, in
  `VIOLENS_LEGACY_PATCHED_EXTENSIONS_DIR`. They should load, add their
  menu items, and run every `IAutoTamper` hook on a synthetic session
  without throwing. The repository doesn't ship patched copies, since that
  would mean redistributing modified third-party binaries.
- `VIOLENS_LEGACY_TEST_SAZ` points one more test at a real capture.

Before the naming change, all five original extensions loaded here with
no errors, and SAZClipboard added a working Tools menu item and read a real
87-session capture. Differ crashes comparing a session without a
Content-Type header, as it would in Fiddler; the header-indexer test
guards that behavior rather than reaching into Differ's controls.

## Not done

- Tests for the launcher (`LegacyExtensionHostLauncher`,
  `LegacyExtensionHostBridgeClient.Ping`); `ExpectedExecutablePath` could
  at least be tested on its own.
- Showing this window inside VioLens's own.
