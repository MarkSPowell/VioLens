# Breakpoints Research Notes

How Fiddler Classic's breakpoints behave, from its public
[documentation](https://github.com/telerik/fiddler-docs), and the
decisions VioLens made where the documentation was unclear. No decompiled
or leaked source was used (see the clean-room policy in
`CONTRIBUTING.md`). `BreakpointAbortedException.cs` refers here.

## QuickExec commands

`BreakpointRules` mirrors Fiddler Classic's breakpoint commands: each
command has one active value, not a list of rules, as in Fiddler.

| VioLens | Fiddler | What it does |
|---|---|---|
| `BreakOnAllRequests` | Rules > Automatic Breakpoints > Before Requests | Pauses every request |
| `BreakOnAllResponses` | Rules > Automatic Breakpoints > After Responses | Pauses every response |
| `RequestUrlContains` | `bpu` | Pauses a request whose target contains some text |
| `ResponseUrlContains` | `bpafter` | Pauses a response whose *request* target contains some text |
| `RequestMethodEquals` | `bpm` | Pauses a request with a given method |
| `ResponseStatusCodeEquals` | `bps` | Pauses a response with a given status code |

In the app, the first two are toggles, and the other four are the
**Tools > Also Break On** row and the QuickExec commands of the same names.

## Resume and Abort

Fiddler Classic's documentation doesn't describe aborting a paused session
as something separate from not resuming it. VioLens makes Abort its own
action (`PendingBreakpoint.Abort`, a button in the app) next to Resume.
Abort goes through the same `BreakpointAbortedException` path as a session
that's killed, so both ways of giving up on a session end the same way.

## Editing

Fiddler Classic's TextView shows a paused message as one block of raw text
(start line, headers and body) rather than a control for each field.
`PendingBreakpoint.RawText` and `HttpMessageText` do the same, with one
difference (see `HttpMessageText`'s doc comment): if an edit leaves the
text malformed, VioLens reports an error when you resume, where Fiddler
Classic is documented to drop the edit silently.

## Still to write down

Other breakpoint decisions came from the same reading, and belong here:
why leaf certificates are renewed 5 minutes before they expire, and why
breakpoints pause before a `Session` object exists (see `BreakpointStage`'s
doc comment).
