# Violens.SampleExtension

A small extension, written for VioLens, that tests compiled extension
support (`ExtensionHost`, `LoadedExtensionSet`, `Inspector2Adapter`, and
import and export) against a real, separately compiled `.dll`. It isn't a
port of any Fiddler Classic extension.

## What's in it

| File | Interface | What it does |
|---|---|---|
| `SampleAutoTamper.cs` | `IAutoTamper3` (and so `IAutoTamper2`, `IAutoTamper`, `IFiddlerExtension`) | Adds a marker header to every request and response, and counts every hook call, including ones VioLens doesn't call yet |
| `SampleExecActionHandler.cs` | `IHandleExecAction` | Handles one QuickExec command, `sample.ping` |
| `SampleRequestInspector.cs`, `SampleResponseInspector.cs` | `IRequestInspector2`, `IResponseInspector2` | Put a one-line summary before the body |
| `SampleSessionImporter.cs` | `ISessionImporter` | Imports two fixed sessions |
| `SampleSessionExporter.cs` | `ISessionExporter` | Writes a one-line summary of each session to a text file |

## Building and installing it

```
dotnet build tools\Violens.SampleExtension\Violens.SampleExtension.csproj -c Release
```

Copy `Violens.SampleExtension.dll` from
`tools\Violens.SampleExtension\bin\Release\net10.0\` into VioLens's
Extensions folder (`ExtensionHost.DefaultExtensionsFolder`, VioLens's
version of Fiddler's `Documents\Fiddler2\Scripts`), creating the folder if
needed. Only the `.dll` is needed; the folder isn't searched recursively.

That folder is under your Documents folder as Windows knows it, which
isn't always `%USERPROFILE%\Documents`. If OneDrive moves your Documents
folder, it's `%USERPROFILE%\OneDrive\Documents\VioLens\Extensions\`. The
app's Extensions panel (**Tools > Extensions**) shows which folder it
looked in.

## What to check in the app

1. **Loading.** The Extensions panel should show `Scanned '...\Extensions':
   found 1 .dll file(s).` and `Loaded: 1 AutoTamper, 1 request inspector,
   1 response inspector, 1 importer, 1 exporter, 1 exec-action handler
   (total 6).` If it found 0 files, the `.dll` is in a different folder
   from the one the app scanned. An `Error:` line means the extension was
   found but not loaded.
2. **Live traffic.** Start capturing and browse. Every request and response
   should have an `X-Violens-SampleExtension` header
   (`AutoTamperRequestBefore-ran` or `AutoTamperResponseBefore-ran`) on the
   Headers tab; a site that echoes requests back, such as
   `https://httpbin.org/post`, shows the request one arriving. The console
   also shows a line for each `AutoTamperRequestAfter` and
   `AutoTamperResponseAfter`, which don't change anything (the `*After`
   hooks only observe).
3. **Inspectors.** A captured session with a body has
   `SampleRequestInspector` and `SampleResponseInspector` tabs, after the
   built-in ones, with `[SampleRequestInspector: N header(s), M original
   body byte(s)]` before the body.
4. **Import and export.** **File > Import via Extension** adds two
   sessions for `sample.violens.invalid` (a GET returning 200 and a POST
   returning 201). **File > Export via Extension** writes
   `SampleExtension-Export.txt` beside the app's executable, one line per
   session.

## Unit tests

```
dotnet test tests\Violens.SampleExtension.Tests\Violens.SampleExtension.Tests.csproj
```

These call every method directly, with no app, traffic or installed
`.dll`, so they check that each method does what its comment says. The
checks above are what show that loading the `.dll` and the app's wiring
work.
