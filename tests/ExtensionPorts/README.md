# Extension ports

Real Fiddler Classic extensions, built from their own public source against
VioLens's Fiddler-shaped compatibility layer (`Violens.CompatShim`, in
`src/Violens.Compatibility/CompatShim/`), then loaded and run the way the
app runs any extension. CI (`ci.yml`) fetches them and runs their tests on
both Windows and macOS.

This is the test of tenet 1 ("port an existing Fiddler workflow with
minimal effort") against extensions other people wrote, not against
samples written to fit.

## What a port changes

For each extension, the port is:

1. **Delete every `using Fiddler;` line.** `port.ps1` does this with one
   regular expression. For an extension with WinForms menus, it also
   deletes `using System.Windows.Forms;` and drops the
   `System.Windows.Forms.` prefix from `MenuItem`, which then resolves to
   VioLens's own. Nothing else in the upstream source changes.
2. **Add one file, `PortGlobalUsings.cs`**, which imports
   `Violens.CompatShim` and aliases `Fiddler` to it, so qualified names
   like `Fiddler.Parser.ParseRequest(...)` compile as written.
3. **A new SDK-style project file** targeting `net10.0`.

If an extension can't be ported that way, the fix goes in the compatibility
layer, not in the extension. When a platform gap makes that impossible
(WinForms UI, `System.Drawing` on macOS), that's recorded in
`docs/Extension Test Targets.md`, and any change is offered upstream.

Upstream source is fetched at a pinned commit into `.work/` (git-ignored)
and never committed here.

| Extension | Upstream | Commit | Lines changed |
|---|---|---|---|
| NetLog importer | [ericlaw1979/FiddlerImportNetlog](https://github.com/ericlaw1979/FiddlerImportNetlog) (BSD-3-Clause) | `1a927f4` | 3 `using Fiddler;` lines deleted |
| Privacy Scanner (cookies/P3P) | Eric Lawrence's sample in [telerik/fiddler-docs](https://github.com/telerik/fiddler-docs/blob/51c6ebebdbb79726b286caa3fc28bb2641a2f0c7/extend-fiddler/cookieextension.md) (Apache-2.0) | `51c6ebe` | 2 `using` lines deleted, 7 `System.Windows.Forms.` prefixes dropped from `MenuItem` |
| CSP Rule Collector | [MarkSPowell/CSP-CLeARINET-Extension](https://github.com/MarkSPowell/CSP-CLeARINET-Extension) (MIT), a VioLens-only fork of [ericlaw1979/CSP-Fiddler-Extension](https://github.com/ericlaw1979/CSP-Fiddler-Extension) | `4d4cb9a` | Not a port: the fork is VioLens's own version (see below) |

### Extensions with a WinForms UI: VioLens-only forks

WinForms doesn't exist on macOS or inside VioLens's Avalonia window, so an
extension with a WinForms UI can't be ported by changing a `using`. It gets a
**VioLens-only fork** instead, and Fiddler Classic users keep using the
original:

- The fork drops its Fiddler code and names: no `using Fiddler;`, no
  WinForms files, and no "Fiddler" in its assembly, namespace or class
  names. Its UI is rewritten in Avalonia and added with
  `Violens.Compatibility.Extensions.ExtensionUi.AddTab(title, view)`.
- It has its own SDK-style project, which builds against VioLens's
  compatibility layer from a VioLens clone next to it (or the VioLens
  repo it sits inside), and references Avalonia at compile time only.
- `port.ps1` copies the fork, unchanged, into `.work/<Name>/`, from a
  pinned commit or, with `-CspSource <clone>`, a local clone (to try
  changes before pushing them). The test project builds it from there.

The CSP Rule Collector's fork is
[MarkSPowell/CSP-CLeARINET-Extension](https://github.com/MarkSPowell/CSP-CLeARINET-Extension),
which builds `VioLensCSP.dll`.

## Running locally

From the repo root. `port.ps1` downloads each extension at its pinned
commit (a zip of the repo, or for the Privacy Scanner the one docs page;
no git needed) and runs in the Windows PowerShell that comes
with Windows, or in PowerShell 7 (`pwsh`) on macOS.

Windows (Command Prompt or PowerShell):

```
powershell -ExecutionPolicy Bypass -File tests\ExtensionPorts\port.ps1
dotnet test tests\ExtensionPorts\Violens.ExtensionPorts.Tests\Violens.ExtensionPorts.Tests.csproj
```

macOS (install PowerShell once with `brew install powershell`):

```
pwsh tests/ExtensionPorts/port.ps1
dotnet test tests/ExtensionPorts/Violens.ExtensionPorts.Tests/Violens.ExtensionPorts.Tests.csproj
```

`-ExecutionPolicy Bypass` applies to that one run only. Windows blocks
local scripts by default, and this doesn't change that setting.

The test project builds the ports first. A ported `.dll` lands in
`.work/bin/<Extension>/`; VioLensCSP, which has its own project, builds
to `.work/VioLensCSP/bin/`.

To try the NetLog importer in the app itself, copy
`.work/bin/FiddlerImportNetlog/VioLensNetLog.dll` (only that file)
into VioLens's extensions folder (`Documents/VioLens/Extensions`), start
VioLens, and use **File > Import via Extension**. The Privacy Scanner
(`.work/bin/PrivacyScanner/PrivacyScanner.dll`) works the same way: it adds
a **Privacy** menu. For the CSP Rule Collector, build the fork itself (see
its README).

## Shipping them as optional extensions

`installer/build-extensions.ps1` runs `port.ps1`, builds all three in
Release, and collects each `.dll` with its license text and a
`THIRD-PARTY-NOTICES.txt` (source, commit, license and what the port
changed; Apache-2.0 requires the last). The release workflows use it: the
Windows installer offers each as an optional task installed into
`<app>\Extensions`, and the macOS `.dmg` gets an **Optional Extensions**
folder. When a pinned commit changes here, update the matching notice in
that script.

```
pwsh installer/build-extensions.ps1 -OutputDir publish/extensions
```

## Fixtures

`Violens.ExtensionPorts.Tests/Fixtures/` holds synthetic captures written
by hand. Real browser captures are never committed: they carry cookies,
tokens and personal data.
