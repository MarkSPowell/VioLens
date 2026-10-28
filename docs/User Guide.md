# VioLens User Guide

This guide covers using VioLens day to day: capturing traffic, finding
your way around the session list, and the tools that come with it. If you
want to build VioLens yourself, start with [README.md](../README.md). You
can open this guide from the app at any time with **Help > Documentation**.

## Upgrading from CLeARINET

VioLens was called CLeARINET until version 0.2. If you used CLeARINET on
this computer:

- Your settings carry over. When VioLens first runs, it copies
  CLeARINET's settings, AutoResponder rules and FiddlerScript preferences
  into its own folder, and the extensions in `Documents\CLeARINET\Extensions`
  into `Documents\VioLens\Extensions`. The originals stay where they were,
  with a note listing what was copied; delete them once you're happy. If
  CLeARINET was closed without putting your proxy settings back, VioLens
  puts them back.
- Only CLeARINET's own files are touched. Another app with a similar name
  keeps its files in folders called `Clearinet`, which Windows and macOS
  treat as the same folders. VioLens only copies, and the installer only
  removes, the files CLeARINET created, so anything else there is left
  alone.
- There's a new root certificate. VioLens asks you to trust its own
  `DO_NOT_TRUST_ViolensRoot` certificate, then removes CLeARINET's
  `DO_NOT_TRUST_ClearinetRoot` (on Windows, the system asks you to confirm
  the removal). Phones and other devices you set up to trust CLeARINET's
  certificate need the new one: visit `http://violens/` through the proxy
  to download it, and remove the old one from the device.
- On Windows, installing VioLens removes the program files and shortcuts
  CLeARINET installed. On macOS, VioLens is a separate app: drag CLeARINET
  from Applications to the Bin once VioLens is working.

## Starting the proxy

1. Pick a port. **Port: Auto** (the default) uses any free port, which is
   fine unless something else on your machine expects VioLens on a
   specific port. **Specify** lets you type one in.
2. Click **Start**.
3. On first start, VioLens asks you to trust a certificate named
   `DO_NOT_TRUST_ViolensRoot...`. That's what lets it see inside HTTPS
   traffic on this machine, the same way Fiddler Classic does. On Windows
   you get the system's "Do you want to install this certificate?"
   prompt. On macOS, VioLens shows its own confirmation before it adds
   anything to your login keychain. Accept it to continue. Nothing you
   capture leaves your device unless you send it somewhere.
4. Once it's running, the status bar shows the port VioLens is listening
   on and whether it registered itself as the system proxy. If it couldn't
   (because another tool already has that role, for example), you'll
   still capture traffic from anything you point at the port yourself.

Click **Stop** to release the port and stop capturing. Sessions you've
captured stay in the list until you remove them (see below) or close the
app.

To stop trusting VioLens's certificate, choose **Tools > Remove Root
Certificate**. This stops capturing and removes the certificate and its
key from your account, and the next **Start** makes a new one.
Uninstalling VioLens on Windows does the same. On Windows, the system asks
you to confirm.

HTTPS interception works on both Windows and macOS, but the Mac build is
newer and less tested. See the Known limitations section of
[README.md](../README.md) for details.

A macOS build that's signed and notarized opens like any other app. One
that isn't (one you built yourself, or a test build) makes macOS say it
can't verify the developer the first time you open it. Open it anyway
from **System Settings > Privacy & Security > Open Anyway**. You only
need to do this once.

## The session list and inspectors

Every request/response pair VioLens sees appears as a row in the main
grid: number, time, status, method, URL, and request/response sizes. Click a
row to see its **Request** and **Response** panels below, each with its own
tabs:

- **Headers**: a table of header names and values.
- **Raw**: the decoded text body. It decompresses gzip, deflate, Brotli
  and zstd, and uses the character set from Content-Type (README.md has
  the full list of what's handled).
- **JSON** (only for a JSON body): the body pretty-printed, so an API
  response is easy to read. You get it for JSON content types, and for a
  body with no useful content type that turns out to be JSON.
- **WebForms** (request side, only when there's something to show): the
  query string and any form fields, one row per name and value, decoded.
  Form uploads (`multipart/form-data`) show each field, and each file's
  name, type and size.
- **ImageView** (only for an image): the picture itself, with its format,
  pixel size and size in bytes. PNG, JPEG, GIF, WebP, BMP and ICO are
  recognized even when the Content-Type is wrong. For PNG, JPEG, GIF and
  WebP it also lists any **image bloat**: bytes that don't change how the
  image looks, such as EXIF camera data, XMP, editing-program data,
  comments, text, or data after the end of the image. Removing them makes
  the file smaller with no visible change. (EXIF can hold the photo's
  orientation, which browsers honor, so keep that tag if the image relies
  on it.)
- **Hex**: a hex dump of the raw bytes.
- **Cookies** (only on a side that has cookies): on the request side,
  each cookie the browser sent; on the response side, each cookie the
  server set, with its attributes (Path, Domain, Expires, Secure, HttpOnly,
  SameSite...). A cookie browsers will refuse, such as `SameSite=None`
  without `Secure`, or a `__Host-`/`__Secure-` cookie that breaks its
  prefix's rules, gets a warning.
- **Notes** (request side, only when there are any): notes an importer
  or extension attached to the session. For example, a NetLog capture
  recorded without bodies notes the response size the browser reported,
  which explains why the Resp column shows 0 B.

Right-click a session for **Compare Sessions**, **Replay**, **Edit in
Composer** and **Remove**, plus any actions a FiddlerScript adds.
Right-clicking a session selects it, unless it's already selected.

To remove sessions, use **Edit > Remove Selected Session** (or press
**Delete** in the session list) or **Edit > Remove All Sessions**. Removed
sessions are gone for good: they're no longer in the list or in what
**Save SAZ** writes.

For long captures, the drop-down next to Start and Stop (**Keep all
sessions** by default, as in Fiddler Classic) limits how many sessions
VioLens holds: **Keep newest 100** up to **Keep newest 10,000**. Once the
list is full, the oldest session is removed as each new one arrives, so
memory use stays steady.

The **Version** column shows the HTTP version the browser used with
VioLens, followed by the one VioLens used with the server when they differ:
`HTTP/2 → HTTP/1.1` means the browser used HTTP/2 but the site only offered
HTTP/1.1. HTTP/2 is on by default; to see everything as HTTP/1.1, turn off
**Use HTTP/2 with browsers** under **Tools > Connections**. (Fiddler Classic's
Protocol column showed HTTP or HTTPS; here that's the start of the URL.)

The **Image Bloat** column shows, for image responses, the bloat bytes out
of the file's size and the percentage. Rows where bloat is at least a
quarter of the file (and at least 1 KB) are tinted orange, unless something
else has already colored the row.

Importers, extensions and scripts can also mark sessions for the list: a
row can have its own background or text color, be bold, italic or struck
through, or be hidden (it's still captured and saved). Extensions can add
columns to the list, too.

To choose which columns the list shows, **right-click any column header**
(or use **Tools > Session List Columns**) and check or uncheck columns.
Every column can be hidden except **#** and **URL**, including the
Waterfall and Image Bloat columns and columns a script or extension added;
**Show All Columns** brings them all back. Drag a header to move its
column, or drag the edge between headers to resize it.

VioLens remembers which columns are shown, their order and their widths
for next time. A script's or extension's column keeps its place and width
while that script or extension isn't loaded, and comes back there. (The URL
column always fills the space that's left.) **Reset Columns** in the same
menu shows every column again, in the original order and widths.

### Filtering

The filter box above the grid accepts free text (matched against the URL and
headers, never body content) plus a small query grammar:

- `method:GET` matches the method exactly
- `host:example.com` matches the host exactly
- `status:404`, `status:4xx` and `status:>=500` match an exact status, a
  class or a comparison

You can mix free text and query tokens in one filter. The list updates as
you type.

### Timing, the waterfall and statistics

VioLens times each phase of every exchange, and shows it the way
browsers' developer tools do:

- The **Waterfall** column in the session list draws each session on a
  shared time axis, from the first session shown to the last one finishing.
  Each bar is split into phases: **Queueing** (gray: reading the request,
  scripts and extensions, breakpoints), **DNS lookup** (teal), **Initial
  connection** (orange), **TLS handshake** (purple), **Request sent**
  (blue-gray), **Waiting for server** (green: time to first byte) and
  **Content download** (blue). A request that reused an open connection
  has no DNS, connection or TLS phases. Hover over a bar for the numbers.
- The line under the list sums up what's shown: how many requests, how much
  was transferred, and when the last one finished.
- The **Timing** tab shows the selected session's phases with their
  durations, whether it used a new or reused connection (and through which
  upstream proxy), and how long it sat at a breakpoint, if it did. For
  HTTPS it also shows the TLS version and cipher suite VioLens agreed
  with the server, and the server's certificate.
- The **Statistics** tab sums up the sessions the filter shows: bytes sent
  and received (and what compression saved), breakdowns by type, status and
  host, the slowest requests, and easy wins such as text sent uncompressed,
  image bloat, and responses with no caching headers.

The times are measured at VioLens, so they don't include time the
browser spent before sending a request, and anything the browser served
from its own cache never reaches VioLens at all. Sessions imported from a
file without timings have no bar.

If you don't need timing, right-click a column header and uncheck
**Waterfall** to hide that column.

**Servers that ask for a client certificate (mutual TLS).** VioLens makes
its own connection to the server, so when a server asks for a client
certificate, it asks VioLens, and the browser never sees the request (or
shows its certificate prompt). VioLens records it instead: the Timing tab
says the server asked, and which certificate authorities it accepts, and the
session gets a **Notes** tab with the `x-client-certificate-requested` note.
VioLens doesn't send a client certificate yet, so a server that requires
one refuses the connection; the 502 page then says that's the likely reason.

### Finding sessions

**Edit > Find Sessions** (**Ctrl+F**) looks for text anywhere in the
captured sessions, including inside request and response bodies
(decompressed, and read with their character set, as the Raw tab shows
them). Unlike the filter, it doesn't hide anything: every matching session
is highlighted in yellow and the first one is selected. **F3** (or
**Next**) moves to the next match.

In the Find bar you can choose to look in requests and responses or just
one side, in headers and bodies or just one of them, match case, and use a
regular expression. "Headers" includes the request line with the full URL,
and the status line. Press **Enter** to find, and **Escape** (or **Close**)
to close the bar and clear the highlighting. Sessions captured after a
Find aren't highlighted until you find again.

### Comparing two sessions

To see what changed between two exchanges, such as a request that works
and one that doesn't, select both: click one, then **Ctrl+click** the
other (**Cmd+click** on a Mac). Then choose **Edit > Compare Sessions**
(**Ctrl+W**), or right-click either of them and choose **Compare
Sessions**.

A window opens with the older session on the left and the newer on the
right. The **Request** and **Response** tabs each show the request or
status line (with the full URL), the headers and the body, side by side.
Lines that differ are yellow, with the part that differs highlighted; a
line only on the left is red and a line only on the right is green. Each
tab's title says how many places differ. **Next difference** (**F8**) and
**Previous difference** (**Shift+F8**) jump between them.

The options above the tabs change what's compared:

- **Decompress bodies** (on): compares the content of compressed bodies,
  not the compressed bytes.
- **Format JSON and forms** (on): puts each JSON value, and each form
  field, on its own line, so a single changed value shows on its own line
  rather than as one long changed line.
- **Ignore header order** (off): sorts the headers by name first, so the
  same headers sent in another order don't count as different.

Binary bodies, such as images, are compared as a hex dump, or by size and
SHA-256 hash when they're over 64 KB. Two very different large bodies are
shown as one changed block rather than lined up line by line. You can
keep several Compare windows open while capturing.

### WebSocket messages

Apps that keep a connection open for live updates, such as chat, dashboards
and development servers, often use WebSockets. A WebSocket connection shows
in the session list as the request that opened it, with status **101**.
Select it and open the **WebSocket** tab to see its messages, as browsers'
developer tools do:

- **↑** is a message the client sent, and **↓** one it received. Each row
  shows when it was sent (seconds after the connection opened), its type
  (text, binary, close, ping or pong), its size and the start of its data.
- Select a message to see it in full below the list: JSON is indented,
  and binary data is shown as hex unless it's really text.
- The list keeps growing while the connection is open, and the heading
  says whether it's still open and how many messages have gone each way.
- **Hide pings and pongs** hides the keep-alive messages some connections
  send constantly.

Compressed messages (permessage-deflate) are decompressed, and messages
sent in several pieces are put back together. The Size column shows a
compressed message's size as sent, then as decompressed. VioLens keeps up
to 1 MB of each message and 64 MB per connection; beyond that it counts
messages but doesn't keep their content. **Find** searches text messages
too: sent messages count as the request, received ones as the response.

WebSocket messages are saved in HAR files (and read from the HAR files
browsers export), but not in SAZ files yet.

## TextWizard

**Tools > TextWizard…** (**Ctrl+E**) encodes and decodes text. Type or
paste into the top box, choose a conversion, and the output updates as you
type. If text is selected in an inspector (or any text box) when you press
Ctrl+E, it becomes the input.

| Conversion | Does |
|---|---|
| To Base64 / From Base64 | Base64. Decoding also accepts the URL-safe kind (`-` and `_`), missing `=` padding and line breaks |
| URL encode / URL decode | Percent-encoding. Decoding treats `+` as a space |
| HTML encode / HTML decode | HTML entities such as `&lt;` and `&#39;` |
| To hex / From hex | Bytes as hex. Decoding ignores spaces, commas, colons, dashes, `0x`, `\x` and `%` |
| JavaScript escape / unescape | String escapes: `\n`, `\"`, `\uXXXX`, `\u{...}`, `\xHH` |
| Decode JWT | A JSON Web Token's header and payload, with `iat`, `nbf` and `exp` as dates. The signature isn't checked |
| Format JSON | JSON indented, one value per line |
| Unix time ↔ date | A Unix time (seconds or milliseconds) as a UTC and local date, or a date as a Unix time |
| Hashes | MD5, SHA-1, SHA-256 and SHA-512 |
| Deflate to Base64 / Inflate from Base64 (SAML) | The compression SAML's redirect binding uses. Inflating also accepts a URL-encoded value copied from `SAMLRequest=`, and zlib or gzip data |

**Characters as** chooses how text becomes bytes, and bytes text: UTF-8
(the default), Latin-1 or UTF-16. It's grayed out for the conversions where
it doesn't matter. When decoded bytes aren't text in that encoding (an
image, or compressed data, say), the output is a hex dump and a note says
so. **Use output as input** moves the result up to convert it again, for
example URL decode, then From Base64. The window remembers the last
conversion you used.

## The Tools menu

Several panels are hidden by default. You turn each one on or off from
the **Tools** menu: those entries are checkboxes, and checking one shows
the panel on the main screen. Unchecking it hides the panel again but
keeps whatever you'd set up in it. Tools also opens TextWizard and the
column chooser:

| Tools menu entry | Shows |
|---|---|
| FiddlerScript | The FiddlerScript panel (see below) |
| Extensions | The Extensions status panel (see below) |
| Legacy Extension Host | The Legacy Extension Host panel (see below) |
| Break on Requests | Pauses every outgoing request at a breakpoint |
| Break on Responses | Pauses every incoming response at a breakpoint |
| Also Break On | A row of narrower breakpoint conditions (URL contains, method, status code) |
| AutoResponder | The AutoResponder rules editor |
| Connections | The upstream proxy setting and "Allow remote computers to connect" (see below) |
| TextWizard… | Encodes and decodes text (see "TextWizard") |
| Session List Columns | Which columns the session list shows (see "The session list") |

Unchecking "Break on Requests", "Break on Responses" or "Also Break On"
only hides that control. It doesn't clear a rule you'd already set. While
a connection is paused at a breakpoint, the Breakpoints panel stays
visible whatever these checkboxes say, so you can always resume or abort
it.

You can also collapse a visible panel with the small arrow in its header,
to get some screen space back without hiding it from the Tools menu.

## Breakpoints

As in Fiddler Classic, you can pause a request or response before it
completes, look at its raw text (and edit it if you like), then either
**Resume** it or **Abort** the connection.

1. Check **Tools > Break on Requests** and/or **Break on Responses** to pause
   everything, or check **Tools > Also Break On** for narrower conditions:
   pause a request whose URL contains some text, a request with a specific
   method, a response whose request URL contains some text, or a response
   with a specific status code.
2. When something hits a breakpoint, it appears in the **Breakpoints**
   panel's list on the left. Select it to see its raw request or response
   text on the right: start line, headers and body, as one block you can
   edit.
3. Edit the text if you need to, then click **Resume** to send it on with
   your edits, or **Abort** to kill the connection. If an edit leaves the
   text malformed, you get an error rather than having the edit quietly
   dropped.

## AutoResponder

The AutoResponder works like Fiddler Classic's: an ordered list of
match/action rules that answer matching requests before they reach the
real server.

1. Check **Tools > AutoResponder** to show the rules editor.
2. Click **Add Rule** to add a new row. It starts disabled.
3. Fill in the **Match** column: a plain substring, a `*wildcard*`, or one
   of Fiddler's prefixes: `EXACT:`, `regex:`, `NOT:`, `METHOD:verb`.
4. Fill in the **Action** column: a local file path or `http(s)://` URL to
   serve instead, or one of Fiddler's action prefixes: `*redir:`, `*delay:ms`,
   `*header:Name=Value`, `*reset`, `*drop`, `*bpu`, `*bpafter`, and others.
5. Check the box on the left of the row to enable it. A rule only takes
   effect when its row is checked *and* **Tools > AutoResponder** is on
   (that toggle is Fiddler's "Enable rules" switch).
6. Use **Move Up** and **Move Down** to reorder rules. The first matching
   rule wins, so order matters when two rules could match the same
   request. **Remove** deletes the selected rule.

Rules are saved as you edit them, along with each row's checkbox, and are
there the next time you open VioLens. The panel's switch always starts
off (see "Settings that are remembered").

## Composer and Replay

The **Composer** tab (next to **Inspectors**) sends a request you've
written or edited, like Fiddler Classic's Composer. Type the request as raw
HTTP: a first line with the method, a full `https://` or `http://` URL and
the version, then headers, a blank line, and any body. Click **Execute**.

```
POST https://api.example.com/items HTTP/1.1
Content-Type: application/json

{"name": "test"}
```

To start from something you've captured, select it and use **Edit > Edit in
Composer**. **Edit > Replay Selected Session** (or **R** in the session
list) sends the selected request again, unchanged, like Fiddler Classic's
Reissue.

Both send through VioLens itself, so capture has to be running. What they
send shows up as a new session, and goes through the AutoResponder,
breakpoints, FiddlerScript and extensions just like traffic from a browser.
The Host header is set from the URL, and Content-Length from the body. The
URL can be `http://` or `https://` (a path with no URL is sent as HTTPS).
Replay sends the captured request byte for byte, whatever its body.

To send over HTTP/2, write `HTTP/2` as the version on the first line of an
`https://` request; Replay of an HTTP/2 session does the same. That needs
**Use HTTP/2 with browsers** on (under **Tools > Connections**; it's on
unless you turned it off). Without
it the request goes over HTTP/1.1 and the Composer says so. SAZ files save
HTTP/2 sessions as `HTTP/2.0` and HAR files as `http/2.0`, as Chrome does.

**Bodies that aren't text.** Under the request, **Body** says where the
body comes from:

- **In the request text** (the default): typed after the blank line, and
  sent as UTF-8 text.
- **Hex (exact bytes)**: typed as hex in the box that appears, such as
  `89 50 4E 47 0D 0A`. Spaces and line breaks don't matter. Use it for
  images, protobuf, compressed or other binary bodies.
- **From a file**: a file you choose with **Browse…**, read when you click
  Execute. Set the Content-Type header yourself.

With Hex or a file, leave nothing after the blank line in the request text.
**Edit in Composer** on a session whose body isn't text puts the body in
the hex box for you, so it goes out exactly as captured.

## QuickExec

The box under the session list is **QuickExec**, Fiddler Classic's command
line. Click it or press **Alt+Q** (**Edit > QuickExec**), type a command and
press **Enter**. **Up** and **Down** bring back earlier commands, and
**Escape** clears the box. What a command did shows on the status line.

| Command | Does |
|---|---|
| `?text` | Selects sessions whose URL contains the text |
| `@host` | Selects sessions whose host contains the text |
| `=404` | Selects sessions with that status code |
| `=POST` | Selects sessions with that method |
| `>40k`, `<1k` | Selects sessions whose response body is larger or smaller (bytes, `k` or `mb`) |
| `select type` | Selects sessions whose response Content-Type contains the text (`select image`) |
| `allbut type` (or `keeponly`) | Removes every session whose Content-Type doesn't contain the text |
| `cls` (or `clear`) | Removes all sessions |
| `tail 100` | Keeps only the newest 100 sessions |
| `bpu text` | Pauses requests whose URL contains the text (`bpu` on its own stops) |
| `bpafter text` | Pauses responses to requests whose URL contains the text |
| `bps 404` | Pauses responses with that status code |
| `bpm POST` (or `bpv`) | Pauses requests with that method |
| `go` | Lets every paused session continue |
| `start`, `stop` | Starts or stops capturing |
| `help` | Lists the commands and opens this guide |

Selection commands select among the sessions the filter shows, so you can
then compare, replay or remove them. The breakpoint commands set the same
conditions as **Tools > Also Break On**, and show that row.

Any other command goes to the loaded FiddlerScript's `OnExecAction`
function (with the command split into words; "quoted text" stays one word),
then to extensions that handle commands (`IHandleExecAction`). The first to
return true has handled it.

## Connections: upstream proxy and other devices

**Tools > Connections** shows how VioLens connects: the upstream proxy and
its sign-in, whether other devices may use VioLens, and HTTP/2.

**Upstream proxy.** On a network where the internet is only reachable
through a proxy (common at work), VioLens has to send its own connections
through that proxy too. Leave the box empty and VioLens uses whatever
proxy the computer was set to before you clicked Start; the status line
says what it chose. Type `host:port` to use a specific proxy, or `none` to
connect directly. Changes apply straight away.

If the computer uses an automatic proxy configuration script ("Use setup
script" on Windows, "Automatic proxy configuration" on macOS) or
"Automatically detect settings", VioLens fetches the script when you
click Start and asks it which proxy to use for each site, as a browser
would; the status line shows the script's address once it has loaded. If
the script can't be found or loaded, VioLens uses the computer's manual
proxy setting if there is one, or connects directly, and the status line
says why. While capturing, VioLens turns those automatic settings off
(browsers would otherwise skip VioLens) and turns them back on when you
click Stop.

**Proxy sign-in.** If the upstream proxy asks for a sign-in, VioLens
signs in as you, the way browsers do on a company network (Windows
sign-in: Negotiate, which is Kerberos, or NTLM). To use a different
account, or for a proxy that wants a plain user name and password
(Basic), enter them on the **Proxy sign-in** line. Type `DOMAIN\user` for
a domain account. The password is kept only while VioLens is running
and is never saved. If signing in doesn't work, the 502 page VioLens
shows says what the proxy offered.

VioLens can't yet use a SOCKS proxy.

**Allow remote computers to connect.** Lets phones, tablets and other
computers use VioLens as their proxy, so their traffic shows up
here. Check it, then click Start (it takes effect on the next Start; your
firewall may ask whether to allow it). The row then shows the address and
port to set as the proxy on the other device. On that device, with the
proxy set, open `http://violens/` to download VioLens's root
certificate, and follow the instructions on that page to install and trust
it. Anyone on your network can use the proxy while this is on, so turn it
off when you've finished, and remove the certificate from the other device.
Other devices can't reach this computer's own `localhost` services
through VioLens; they get a "403 Forbidden" page instead.

**Use HTTP/2 with browsers.** Lets browsers use HTTP/2 with
VioLens for HTTPS sites, as they would with the site itself. VioLens then
uses HTTP/2 with each site that offers it, with all that site's requests
sharing one connection, and HTTP/1.1 with sites that don't. It's on by
default; turn it off if you'd rather every request went over HTTP/1.1, as
in Fiddler Classic. If you turned it off while it was a preview, it stays
off until you check it. Changes apply straight away to new
connections, so reload the page (or restart the browser) to move off
connections it already has open. The session list's **Version** column
shows which version each side used. WebSockets still use HTTP/1.1, and
plain `http://` sites are unaffected.

**Skip decryption for.** Some apps only accept their own server's
certificate (certificate pinning), so they fail through any proxy that
decrypts HTTPS, VioLens included. List their hosts here, separated by
semicolons, and VioLens passes that traffic through without decrypting
it. `*` matches any part of a name: `*.example.com` covers
`api.example.com` but not `example.com` itself, so list both if you need
both. Each such connection shows as one `CONNECT` session, with no
requests inside it. Changes apply to new connections straight away. The
list is saved as `violens.config.https.skipdecryption` in
`preferences.json`.

**Client certificate.** Some servers ask the browser for a certificate of
its own (mutual TLS), often on company sites. Through VioLens, that
request reaches VioLens, not the browser, so VioLens answers it.
- Choose the certificate from the drop-down. It lists your personal
  certificates that have a private key and may be used to sign in to
  servers; on Windows that's the Personal store, on macOS your login
  keychain.
- With the box after **for** left empty, the certificate goes to every
  server that asks for one, as a browser would offer it. To keep it from
  other servers, list the hosts it's for there, in the same format as
  above.
- The Timing tab and Notes say whether the server asked, and which
  certificate VioLens sent.
- VioLens sends one certificate. Fiddler Classic's `ClientCertificate.cer`
  file and FiddlerScript's `https-Client-Certificate` flag aren't
  supported.

## FiddlerScript

FiddlerScript lets a script hook into every request and response, and add
its own menu entries, buttons and grid columns. It's the closest thing to
Fiddler Classic's `CustomRules.js`.

1. Check **Tools > FiddlerScript** to show the panel.
2. Click **Browse…** to pick a `.js` file. (The picker defaults to `.js`,
   but any file will do.)
3. Click **Load**. The status line below shows which handlers the script
   defines (`OnBeforeRequest`, `OnBeforeResponse`), or the load error if
   something's wrong with it. In that case the previous working script, if
   there was one, keeps running until you fix and reload.
4. After editing the script in your usual editor, click **Reload** to pick
   up the changes. A script can also reload itself.

Besides the request and response hooks, a loaded script can add to the
app in these places:

- **Rules menu** (in the menu bar): filled from the rule options and
  choices the script declares. It's empty and grayed out when no script is
  loaded or the script declares none; hover over it to see why.
- **Script Actions** (a row of buttons in the FiddlerScript panel, below
  Load and Reload): one button per action the script declares. The row
  only appears once the script declares at least one.
- **Session right-click menu**: a script can add its own entries, which
  act on the session you right-clicked.
- **Extra grid columns**: a script can declare its own columns, which are
  added after the built-in ones once the script loads.

## Extensions

VioLens loads compiled .NET extensions (its version of Fiddler Classic's
`.dll` extensions) from its Extensions folder when it starts. To add one,
put the `.dll` there before launching VioLens. The folder is
`Documents\VioLens\Extensions` on Windows and
`~/Documents/VioLens/Extensions` on macOS. VioLens also loads the
`Extensions` folder next to the app itself, where the Windows installer
puts the optional extensions below. If the same `.dll` is in both, the copy
in your Documents folder is used.

### Optional extensions included with VioLens

Three extensions come with the installers. Each is a separate work by its
author, under its own license (see `THIRD-PARTY-NOTICES.txt` and the
`licenses` folder that come with them):

| Extension | What it does | Windows installer |
|---|---|---|
| NetLog importer (`VioLensNetLog.dll`) | Imports Chromium/Edge NetLog captures | Checked by default |
| CSP Rule Collector (`VioLensCSP.dll`) | Builds a Content-Security-Policy for the sites you browse | Checked by default |
| Privacy Scanner (`PrivacyScanner.dll`) | Colors responses that set cookies and checks P3P headers | Unchecked (P3P is obsolete; mostly an example extension) |

- **Windows:** choose them on the installer's "Select Additional Tasks"
  page. Run the installer again to add or remove one.
- **macOS:** the `.dmg` has an **Optional Extensions** folder next to the
  app. Copy the `.dll`s you want into `~/Documents/VioLens/Extensions`
  and restart VioLens.

Check **Tools > Extensions** to see which folders were scanned, how many
`.dll` files were found, how many of each kind of extension loaded
(AutoTamper, request/response inspector, importer, exporter, exec-action
handler, ported Fiddler extension), and any load errors.

If a loaded extension offers session import or export, you can use it
from the File menu:

- **File > Import via Extension…**
- **File > Export via Extension…**

Each is grayed out (with a tooltip saying why) until a loaded extension
supports it. If your extensions offer more than one format, you're asked
which to use, with last time's choice already selected. If there's only
one, it's used straight away.

### Fiddler Classic extensions ported from source

If you have the source of a Fiddler Classic extension, you can usually
rebuild it for VioLens with very small changes, and it then runs on both
Windows and macOS. The first one ported this way is Eric Lawrence's
[NetLog importer](https://github.com/ericlaw1979/FiddlerImportNetlog),
which imports Chromium/Edge NetLog captures (`.json`, `.json.gz` or a
`.zip` containing one). The only change to its source is deleting its
`using Fiddler;` lines. It's one of the optional extensions above (or see
`tests/ExtensionPorts/README.md` to build it yourself). Use
**File > Import via Extension…**; you'll be asked to pick the capture file.

Ported extensions can also work on live traffic (Fiddler's
`IAutoTamper` hooks): each request gets one session object from its first
hook to its last, flags they set show on the session's **Notes** tab, and
an extension can answer a request itself without it reaching the server.
An extension can add its own tab next to **Inspectors** above the request
and response panels; the tab strip only appears once one has.

The **CSP Rule Collector**, which works out the smallest
Content-Security-Policy a page needs, has a VioLens-only version:
[CSP-CLeARINET-Extension](https://github.com/MarkSPowell/CSP-CLeARINET-Extension),
a fork of the Fiddler extension with an Avalonia tab. It's one of the
optional extensions above (or build it yourself; see its README). Open
the **CSP Rule Collector** tab, check **Enable Rule Collection**, clear the
browser's cache and browse the site. Uncheck it when you're done: while
it's on, responses get extra
report-only CSP headers and aren't cached. While it's collecting, the
browser sends many report requests to `fiddlercsp.deletethis.net`; the
extension answers them itself, and they're shown struck through. Check
**Tools > Hide CSP Report Requests** to leave new ones out of the list,
and use **Edit > Remove All Sessions** to clear ones already there.

Ported extensions can add their own menus (before **Help**, or in
**Tools**), session-list columns and row colors. The optional Privacy
Scanner, for example, adds a **Privacy** menu, a **Privacy Info** column
and row colors. See `docs/Extension Test Targets.md` for what's supported
so far.

## Legacy Fiddler Classic extensions

Some compiled Fiddler Classic extensions use Windows Forms menu, toolbar
and tab controls that Microsoft removed from .NET 5 and later. Those
can't run inside VioLens itself, so VioLens includes a separate program,
`Violens.LegacyExtensionHost.exe`, that runs them in its own window,
connected to your captured traffic.

It's off by default. You only need it if you have a compiled Fiddler
Classic extension of this kind that you want to keep using.

1. Put the extension's `.dll` in `Documents\VioLens\LegacyExtensions\`.
   An extension still compiled against the real Fiddler assembly won't load
   as it is. The pop-up or log message it produces says what to do, which
   comes down to recompiling it against `Violens.CompatShim` (if you
   have its source).
2. Check **Tools > Legacy Extension Host** to show the panel, then check
   "Launch Violens.LegacyExtensionHost.exe automatically on Start". With
   that on, clicking **Start** also launches the legacy host (if it isn't
   already running) and connects it to your traffic. The panel's status
   line says what happened (already running, launched, not found, or
   launch failed). With the checkbox off, VioLens works as normal.
3. The legacy host always opens in its own window, separate from
   VioLens's. Loaded extensions' request and response tampering hooks run
   against your real traffic, but anything the extension shows in its own
   window's session list is separate demo data, not your sessions.

## Saving and loading captured sessions

- **File > Save SAZ…** exports every session captured this run to a
  `.saz` (Session Archive Zip) file on your Desktop.
- **File > Open SAZ…** imports sessions from a previously saved `.saz` file
  back into the session list, exactly as if they'd just been captured live.
- **File > Export HAR…** saves every session as an HTTP Archive (`.har`),
  the format browsers' developer tools and many support teams use. Bodies
  are saved decompressed, as text where they're text, and each session's
  timings are included (see "Timing, the waterfall and statistics").
- **File > Import HAR…** adds sessions from a `.har` file, such as one
  exported from a browser's developer tools. HAR files keep bodies
  decompressed, so imported sessions don't have Content-Encoding headers.
  The browser's own timings come in too, so the waterfall shows them.

## Settings that are remembered

VioLens remembers these settings between runs, the same way on Windows
and macOS:

- The port choice (Auto or a specific port number)
- Which Tools-menu panels are shown (FiddlerScript, Extensions, Legacy
  Extension Host, Also Break On, Connections)
- The upstream proxy setting, the proxy sign-in user name (not the
  password), whether remote computers may connect, HTTP/2, the
  skip-decryption hosts, and the client certificate and its hosts
- Whether to check for updates at start-up
- How many sessions to keep
- The window's size and position, and whether it was maximized
- The session list's columns: which are shown, their order and widths
- TextWizard's last conversion and character encoding
- The AutoResponder rules (in their own file, `autoresponder-rules.json`,
  next to `preferences.json`)
- The session filter text
- The FiddlerScript path you last entered
- Whether the legacy extension host launches automatically

Breakpoints and the AutoResponder's on/off switch are **not** remembered.
Once VioLens is the system proxy, a breakpoint or AutoResponder rule that
came back on at launch would affect every app on your machine as soon as
you clicked Start.

Settings are saved in a `preferences.json` file:

- **Windows:** `%LOCALAPPDATA%\VioLens\preferences.json`
- **macOS:** `~/Library/Application Support/VioLens/preferences.json`

The file is the same format on both platforms, so you can copy it from one
machine to another. You can edit it by hand while VioLens is closed.
Changes made while it's running are picked up the next time it starts. If
the file ever becomes unreadable, VioLens renames it to
`preferences.json.corrupt-<date>` and starts with defaults, so nothing you
had is lost. To reset every setting, quit VioLens and delete the file.

## The Help menu

- **Help > Documentation** opens this file with whatever your system uses
  for `.md` files. If nothing is set up for them, you'll find the file at
  `docs/User Guide.md` in the VioLens source tree, or in a `Documentation`
  folder next to the app's executable.
- **Help > Copy Diagnostic Info** copies what a bug report needs: the
  VioLens version, OS, whether capture is running and how, the upstream
  proxy, HTTP/2, whether the root certificate is trusted, the network
  adapters (which show a VPN or Docker) and the last status message. It
  leaves out your computer's name, and only says whether proxy environment
  variables are set, not what they're set to. Paste it into the
  **Diagnostic info** box of a
  [bug report](https://github.com/MarkSPowell/VioLens/issues/new?template=bug_report.yml).
- **Checking for updates.** At start-up VioLens asks GitHub whether a
  newer release is out. If one is, a button beside the title says so and
  opens the release's page. If you're running a preview you're offered
  newer previews too; otherwise only full releases. **Help > Check for
  Updates** asks straight away. To stop the start-up check, uncheck **Help
  > Check for Updates at Start-up**. The check is one request to GitHub's
  public releases list and sends nothing about you or your traffic.
  VioLens never downloads or installs anything itself.
- **Help > About VioLens** shows which build you're running. That's
  useful when you report an issue or want to check you're on the latest
  preview.

## Known limitations

VioLens is still a preview. The main gaps are SOCKS proxies and Digest
proxy sign-in, sending only one client certificate, and the Mac build
having had less use than the Windows one. The full list is in the Known
limitations section of [README.md](../README.md).
