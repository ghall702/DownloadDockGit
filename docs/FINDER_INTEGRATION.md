# DownloadDock ⇄ Finder for Windows — Integration Reference

**Protocol version 1.0** · Shared library: `DownloadDock.Integration` (net8.0, no dependencies)

This document is the complete contract between **DownloadDock** (tray app: Downloads fan, screenshots)
and **Finder for Windows** (file manager). You can implement the Finder side from this document
alone: no knowledge of DownloadDock's internals is needed or assumed. The shared library in
`src/DownloadDock.Integration` implements everything described here; using it is strongly recommended
over re-implementing the wire format.

Contents:

1. [Architecture overview](#1-architecture-overview)
2. [Named-pipe endpoints](#2-named-pipe-endpoints)
3. [Protocol version and compatibility policy](#3-protocol-version-and-compatibility-policy)
4. [Wire format](#4-wire-format)
5. [Command reference](#5-command-reference)
6. [Events and subscriptions](#6-events-and-subscriptions)
7. [Error codes](#7-error-codes)
8. [Connection lifecycle](#8-connection-lifecycle)
9. [Security considerations](#9-security-considerations)
10. [Finder client implementation guide](#10-finder-client-implementation-guide)
11. [Example: connecting to DownloadDock](#11-example-connecting-to-downloaddock)
12. [Example: receiving commands from DownloadDock](#12-example-receiving-commands-from-downloaddock)
13. [Referencing the shared library](#13-referencing-the-shared-library)
14. [Native drag-and-drop compatibility](#14-native-drag-and-drop-compatibility)
15. [Installation and deployment](#15-installation-and-deployment)
16. [Troubleshooting](#16-troubleshooting)

---

## 1. Architecture overview

```
┌──────────────────────────── one Windows logon session ─────────────────────────────┐
│                                                                                     │
│   DownloadDock.exe                                     FinderForWindows.exe          │
│   ┌──────────────────────────┐   commands + events   ┌──────────────────────────┐  │
│   │ IntegrationServer         │◄──────────────────────│ client (Resilient…)      │  │
│   │ pipe: DownloadDock.       │  showFan, getStatus,  │ subscribes to events     │  │
│   │  Integration.v1.<key>     │  subscribe … ───────► │                          │  │
│   ├──────────────────────────┤                       ├──────────────────────────┤  │
│   │ client (Resilient…)       │──────────────────────►│ IntegrationServer        │  │
│   │ "Show in Finder" etc.     │ openFolder,revealFile │ pipe: FinderForWindows.  │  │
│   │                           │ filesReceived         │  Integration.v1.<key>    │  │
│   └──────────────────────────┘                       └──────────────────────────┘  │
│                                                                                     │
│   Files themselves move ONLY by native OLE drag-and-drop (CF_HDROP / Shell IDList). │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

* **Two independent endpoints.** Each application hosts its own named-pipe **server** and is a
  **client** of the other's. There are no conflicts over who listens, and either app may start, stop,
  crash or update independently.
* **In-process.** No service, no admin rights, no TCP/UDP ports. Each server runs on background
  threads inside its own app.
* **Request/response plus events** on one duplex connection: JSON messages with ids (many requests
  may be in flight) and server-pushed events after a subscription.
* **IPC coordinates, OLE moves files.** No command transfers file contents, and dragging files is
  never replaced by IPC (see §14).
* **Graceful degradation.** DownloadDock works fully without Finder. "Show in folder" falls back to
  File Explorer in every failure mode.

## 2. Named-pipe endpoints

| Endpoint | Hosted by | Pipe name (`\\.\pipe\…`) |
|---|---|---|
| DownloadDock | DownloadDock | `DownloadDock.Integration.v1.<key>` |
| Finder | Finder for Windows | `FinderForWindows.Integration.v1.<key>` |

* `v1` is the protocol **major** version (§3), so incompatible future versions use different pipes.
* `<key>` scopes the pipe to the **current user and logon session**. It's the first 16 lowercase hex
  characters of `SHA-256(UTF8("<user SID>|<session id>"))`, where the user SID is
  `WindowsIdentity.GetCurrent().User.Value` (e.g. `S-1-5-21-…-1001`) and the session id is
  `Process.GetCurrentProcess().SessionId`.
* Use `IntegrationEndpoints.DownloadDockPipeName()` / `IntegrationEndpoints.FinderPipeName()` from
  the library; don't hand-code the hash.

Example: `\\.\pipe\DownloadDock.Integration.v1.3f9a0c2b7e41d855`.

## 3. Protocol version and compatibility policy

* The version is `MAJOR.MINOR`, currently **`1.0`** (`IntegrationProtocol.Version`).
* **Major** changes only for incompatible changes (renamed or removed commands, changed semantics,
  new framing). A new major gets new pipe names; peers with different majors never talk.
* **Minor** changes are additive and backwards compatible: new optional fields, new commands, new
  events.
* Negotiation happens in `system.hello`: same major → compatible; the session uses the **lower
  minor** of the two. Different major → the server replies `incompatibleVersion` and closes.
* **Capabilities decide what you may call.** Every server lists its commands in
  `hello.capabilities`; a client must only call commands the server lists. Unknown commands get
  `unknownCommand`, never a crash.
* **Ignore unknown fields** in any message or payload (the library's serializer already does). Don't
  rely on field order.
* Enum-like strings (states, kinds, error codes) may gain new values in later minors; treat unknown
  values as the generic case.

## 4. Wire format

### Framing

Every message is a **frame**: a 4-byte little-endian `uint32` length, followed by exactly that many
bytes of UTF-8 JSON.

```
┌──────────────┬─────────────────────────────────────┐
│ length (LE)  │ UTF-8 JSON (length bytes)           │
└──────────────┴─────────────────────────────────────┘
```

* **Maximum message size: 65,536 bytes** (`IntegrationProtocol.DefaultMaxMessageBytes`).
  * A frame over the limit but ≤ 1 MiB is **skipped** by the server, which replies with a
    `messageTooLarge` error (id `null`); the connection stays usable.
  * A larger announced length makes the server close the connection without allocating anything.
* A length of 0 is invalid. A connection that closes mid-frame is treated as a disconnect.
* Malformed JSON, or JSON without a `type`, gets an `invalidMessage` error (id `null`); the
  connection continues.

### Envelope

All messages share one JSON object shape (camelCase; absent fields are omitted, not `null`):

| Field | Request | Response | Event |
|---|---|---|---|
| `type` | `"request"` | `"response"` | `"event"` |
| `id` | string, ≤ 64 chars, unique per connection | id of the request | — |
| `command` | command name, ≤ 128 chars | — | — |
| `event` | — | — | event name |
| `seq` | — | — | server-wide increasing number |
| `payload` | object (optional) | — | object |
| `ok` | — | `true` / `false` | — |
| `result` | — | object (when `ok`) | — |
| `error` | — | `{ "code", "message" }` (when not `ok`) | — |

```json
{"type":"request","id":"42","command":"downloadDock.showFan","payload":{}}
{"type":"response","id":"42","ok":true,"result":{"state":"opened"}}
{"type":"response","id":"43","ok":false,"error":{"code":"unknownCommand","message":"Unknown command 'x'."}}
{"type":"event","event":"downloadDock.screenshotCaptured","seq":17,"payload":{"path":"C:\\Users\\Ana\\Pictures\\Screenshots\\Screenshot 2026-11-09 08-30-15.png","name":"Screenshot 2026-11-09 08-30-15.png","folder":"C:\\Users\\Ana\\Pictures\\Screenshots","size":482113,"source":"region"}}
```

* Responses may arrive **out of order**; match them by `id`.
* JSON nesting deeper than 16 levels is rejected.
* Paths are always **absolute Windows paths** (§9.4).

### Limits and timeouts

| Limit | Value |
|---|---|
| Handshake: `system.hello` must arrive within | 5 s after connecting |
| Connections per server | 8 |
| Concurrent in-flight requests per connection (server) | 16 (more → `busy`) |
| Pending requests per client (library) | 64 (more → `busy`, client-side) |
| Server handler timeout (DownloadDock) | 10 s → `timeout` |
| Client default request timeout (library) | 5 s |
| DownloadDock waits for `finder.openFolder` / `finder.revealFile` | 3 s, then File Explorer |
| DownloadDock waits for `finder.filesReceived` | 2 s (best-effort notification) |
| Event queue per subscriber | 256 (more → newest events dropped for that client; see `seq`) |
| Files per `finder.filesReceived` | 100 |
| `clientName` / `clientVersion` | ≤ 64 chars |

## 5. Command reference

### 5.1 Built-in commands (every endpoint)

#### `system.hello` (must be the first message)

Request payload:

```json
{"protocolVersion":"1.0","clientName":"FinderForWindows","clientVersion":"1.2.0",
 "capabilities":["finder.openFolder","finder.revealFile","finder.filesReceived"]}
```

`clientName` identifies the app for logs and de-duplication; it is **not** authentication (§9). Use
`IntegrationProtocol.FinderClientName` = `"FinderForWindows"` from Finder. DownloadDock connects to
Finder as `"DownloadDock"`.

Result:

```json
{"protocolVersion":"1.0","serverName":"DownloadDock","serverVersion":"1.0.0",
 "capabilities":["downloadDock.showFan","downloadDock.closeFan","downloadDock.openSettings","downloadDock.getStatus","events"],
 "events":["downloadDock.downloadDetected","downloadDock.screenshotCaptured","downloadDock.fileAddedToDownloads",
           "downloadDock.downloadsFolderChanged","downloadDock.fanOpened","downloadDock.fanClosed"],
 "processId":10412,"maxMessageBytes":65536}
```

`processId` lets the caller grant foreground rights before asking for UI (§10.6).

Errors: `handshakeRequired` (another message came first), `incompatibleVersion`,
`invalidArgument` (missing `clientName`). After any of these the server closes the connection.

#### `system.ping`

No payload. Result: `{"time":"2026-11-09T08:30:15.123+00:00"}`.

#### `system.subscribe` / `system.unsubscribe`

Payload: `{"events":["downloadDock.screenshotCaptured","downloadDock.fanOpened"]}`.
Result: `{"subscribed":[…current subscriptions…],"unknown":[…names the server doesn't publish…]}`.
Subscriptions belong to the connection and end with it, so re-subscribe after reconnecting (the
library's `ResilientIntegrationClient` does this for you).

### 5.2 Served by DownloadDock (Finder → DownloadDock)

None of these commands takes a file path, or can delete, modify or run anything.

#### `downloadDock.showFan`

Opens DownloadDock's existing Downloads fan above its tray icon, with the usual animation and
positioning. It works while DownloadDock sits minimised in the tray, and never creates a second fan.

* Payload (optional): `{"anchor":"tray"}`. `"tray"` is the only supported anchor; any other value
  gives `invalidArgument`.
* Result: `{"state":"opened"}` or `{"state":"alreadyOpen"}`.
* Before calling, grant foreground rights: `AllowSetForegroundWindow(hello.processId)` (§10.6).

#### `downloadDock.closeFan`

Result: `{"state":"closed"}` or `{"state":"notOpen"}`.

#### `downloadDock.openSettings`

Opens (or brings forward) DownloadDock's Settings window.
Result: `{"state":"opened"}` or `{"state":"alreadyOpen"}`.

#### `downloadDock.getStatus`

No payload. Result:

```json
{"protocolVersion":"1.0","appVersion":"1.0.0","state":"running",
 "downloadsFolder":"C:\\Users\\Ana\\Downloads",
 "screenshotFolder":"C:\\Users\\Ana\\Pictures\\Screenshots",
 "screenshotsEnabled":true,"fanOpen":false,
 "capabilities":["downloadDock.showFan","downloadDock.closeFan","downloadDock.openSettings","downloadDock.getStatus","events"],
 "events":["downloadDock.downloadDetected","…"]}
```

`screenshotFolder` is omitted when screenshots are turned off. No other personal data is returned.

### 5.3 Served by Finder (DownloadDock → Finder): you implement these

DownloadDock only calls commands listed in Finder's `hello` capabilities.

#### `finder.openFolder`

```json
{"type":"request","id":"5","command":"finder.openFolder",
 "payload":{"path":"\\\\nas\\Team Share\\Projects – 2026","newWindow":false}}
```

* `path`: absolute folder path (drive, mapped drive or UNC, possibly `\\?\`-prefixed), validated by
  DownloadDock before sending. **Finder must validate it again** (`PathRules.Validate`).
* `newWindow`: `true` means always open a new window; `false` means reuse an existing window where
  that makes sense (e.g. focus a window already showing that folder).
* Result: `{"state":"opened"}`, `{"state":"reused"}` or `{"state":"selected"}`.
* Errors: `invalidPath`, `notFound` (folder missing or drive offline), `unavailable` (Finder can't
  show UI right now).
* **Respond quickly:** acknowledge as soon as the UI work is scheduled. DownloadDock waits only
  3 s before opening File Explorer instead.

#### `finder.revealFile`

Payload: `{"path":"G:\\My Drive\\Reports\\Q1 résumé.pdf","newWindow":false}`. Open the containing
folder and **select** the item. It works for files and folders, on local disks, network shares and
Google Drive for desktop (`G:\My Drive\…`). Result: `{"state":"selected"}`. Errors are as for
`finder.openFolder`.

DownloadDock checks the path exists before sending. If the file is gone, DownloadDock opens its
parent folder instead (using `finder.openFolder` when preferred).

#### `finder.filesReceived`

DownloadDock reports files that just became available:

```json
{"type":"request","id":"9","command":"finder.filesReceived","payload":{
  "files":[
    {"path":"C:\\Users\\Ana\\Pictures\\Screenshots\\Screenshot 2026-11-09 08-30-15.png","kind":"screenshot","size":482113},
    {"path":"C:\\Users\\Ana\\Downloads\\Invoice.pdf","kind":"download","size":91231},
    {"path":"C:\\Users\\Ana\\Downloads\\photo (2).png","kind":"addedToDownloads","size":1200344}
  ],
  "highlight":true}}
```

* `kind`: `download` (new file detected in Downloads), `screenshot` (captured or detected by
  DownloadDock), or `addedToDownloads` (a file someone dropped onto the DownloadDock fan or tray icon,
  copied into Downloads).
* `highlight`: a hint that the user just produced these (screenshot or drop). Finder may select or
  briefly highlight them in an open window showing that folder.
* Finder should **refresh any open view of the containing folders** (optionally highlighting the
  files) and must never open windows or steal focus because of it.
* Result: `{"accepted":3}`. Notifications are best-effort; DownloadDock doesn't retry.
* **De-duplication:** notifications are batched (400 ms, ≤ 100 files). DownloadDock does **not** send
  `filesReceived` for a kind whose equivalent event Finder is subscribed to
  (`download` ↔ `downloadDetected`, `screenshot` ↔ `screenshotCaptured`,
  `addedToDownloads` ↔ `fileAddedToDownloads`). Subscribe to events *or* rely on `filesReceived`;
  you never get both for the same file.

## 6. Events and subscriptions

Subscribe with `system.subscribe` after the handshake. Events are pushed on the same connection as
`{"type":"event", …}` messages, in order. They never block DownloadDock: each subscriber has a
256-event queue, and if it stops reading, newer events are **dropped for that subscriber only**.
`seq` is server-wide and increasing, so a gap tells you events were dropped (or went to other
subscribers). After a gap, refresh state with `downloadDock.getStatus` and a folder rescan.

| Event | When | Payload |
|---|---|---|
| `downloadDock.downloadDetected` | A new completed download appears in the Downloads folder. In-progress `.crdownload`/`.part` files are excluded; the moment such a file is renamed to its final name counts. | `FileEventPayload` |
| `downloadDock.screenshotCaptured` | A screenshot was saved (Win+Shift+3/4, or a detected Snipping Tool / Print Screen capture). `source`: `fullscreen`, `region`, `clipboard` or `windows-file`. | `FileEventPayload` |
| `downloadDock.fileAddedToDownloads` | A file dropped onto the DownloadDock fan or tray icon was copied into Downloads. It is *not* also reported as `downloadDetected`. | `FileEventPayload` |
| `downloadDock.downloadsFolderChanged` | The user chose a different Downloads folder. | `{"path":"E:\\Downloads"}` |
| `downloadDock.fanOpened` / `downloadDock.fanClosed` | The fan opened or closed (for any reason). | `{"open":true}` / `{"open":false}` |

`FileEventPayload`:

```json
{"path":"C:\\Users\\Ana\\Downloads\\Invoice.pdf","name":"Invoice.pdf","folder":"C:\\Users\\Ana\\Downloads","size":91231}
```

Events come from DownloadDock's own single file watcher. **Don't add a second watcher on the
Downloads folder just to mirror these;** Finder's own folder views will of course have their own.

## 7. Error codes

| Code | Meaning | Typical handling |
|---|---|---|
| `invalidMessage` | Malformed JSON, missing id/command, or a second hello | Fix the client |
| `messageTooLarge` | Frame over 64 KiB (skipped; ≤ 1 MiB keeps the connection) | Send less (e.g. ≤ 100 files) |
| `handshakeRequired` | First message wasn't `system.hello` | Connection closed |
| `incompatibleVersion` | Different protocol major | Show "update DownloadDock/Finder"; retry rarely |
| `unknownCommand` | Command not served (check capabilities) | Feature-detect via hello |
| `invalidArgument` | Bad or missing payload field | Fix the request |
| `invalidPath` | Path failed validation | Don't retry |
| `notFound` | Folder or file missing, or drive unavailable | Tell the user |
| `unavailable` | The other app isn't running or connected | Fall back (Explorer) |
| `accessDenied` | Pipe owned by another user, or access refused | Treat as unavailable |
| `busy` | Too many concurrent requests | Retry later |
| `timeout` | Handler or client wait exceeded | Fall back |
| `disconnected` | Connection closed while waiting | Reconnect (automatic with the resilient client) |
| `disabled` | The feature is turned off by the user (reserved) | Respect it |
| `internalError` | Handler failed; details are logged, not sent | Fall back |

In the library, every failure surfaces as `IntegrationException` with `.Code` set to one of these.

## 8. Connection lifecycle

```
client                                              server
  │  open pipe (PipeOptions.CurrentUserOnly)          │  (listening, max 8 instances)
  │ ────────────────────────────────────────────────►│
  │  request system.hello  (within 5 s)               │
  │ ────────────────────────────────────────────────►│  validate version + clientName
  │ ◄──────────────────────────────────────────────── │  response: negotiated version, capabilities, events, pid
  │  request system.subscribe {events}  (optional)    │
  │ ◄──────────────────────────────────────────────── │  response
  │  requests … (concurrent, matched by id)           │
  │ ◄───────────── responses / events …───────────── │
  │  close (either side, any time)                    │
```

* **Either app may start first.** The client side retries with exponential backoff (library
  defaults: 1 s → 30 s; DownloadDock uses 2 s → 30 s). Calling `Nudge()` retries immediately.
* **Instant pairing.** When Finder connects to DownloadDock's server and says hello as
  `"FinderForWindows"`, DownloadDock immediately (re)connects its client to Finder's pipe, without
  waiting for its backoff. Finder should do the same in reverse: when `"DownloadDock"` connects to
  Finder's server, nudge your client towards DownloadDock.
* **Restarts and crashes:** pending requests fail with `disconnected`. The resilient client
  reconnects, re-runs hello and re-subscribes automatically, so neither app needs to restart the
  other.
* **Keep one long-lived connection per direction.** Don't open a connection per request (there are
  only 8 slots). `system.ping` exists for health checks; idle connections are not timed out.
* **Never block the UI thread:** all library calls are `async`, and events arrive on a background
  thread. Marshal to your dispatcher with `BeginInvoke` / `InvokeAsync`, never synchronously from a
  pipe thread.

## 9. Security considerations

### 9.1 What protects the pipes

* **DACL** (`PipeSecurityFactory`): the pipe allows only the **logon-session SID** of the hosting
  process (`S-1-5-5-x-y`), falling back to the user SID, and explicitly **denies
  `NT AUTHORITY\NETWORK`**, so remote SMB clients can't connect. Inherited permissions are
  removed, and Everyone, Authenticated Users and Users get nothing.
* **Session-scoped name:** another user, or another session of the same user, computes a different
  pipe name.
* **Anti-squatting:** the server creates its first instance with `FirstPipeInstance`, so if another
  process already owns the name, the server refuses to start rather than sharing it (DownloadDock
  logs "The integration pipe is already in use by another process"). Clients connect with
  `PipeOptions.CurrentUserOnly`, which verifies the pipe server is **owned by the current user**,
  so a pipe planted by a different account is rejected (`accessDenied`).
* **Bounded resources:** 64 KiB messages, 8 connections, 16 in-flight requests per connection,
  64 pending requests per client, bounded event queues, a 5 s handshake deadline and 10 s handler
  timeouts. A misbehaving peer can't exhaust memory or wedge either UI.

### 9.2 The honest boundary

> **Pipe permissions authenticate *who* (the user/logon session), not *which executable*.**
> Any process running as the same user in the same session can open these pipes and claim to be
> `"FinderForWindows"` or `"DownloadDock"`. `clientName` is a label, not a credential.

That is the standard trust boundary for per-user desktop IPC: a process running as you can already
read your files and drive your desktop. The design therefore makes sure the integration grants
**nothing such a process couldn't already do**:

* DownloadDock's commands take **no paths** and can only show or hide its own UI and report two
  folder locations. There are no delete, modify, copy or execute commands, and none will be added.
* Finder's commands ask it to *display* a folder or file. Finder must validate every path and must
  never execute, open-with or modify anything because of an IPC request.
* Neither app ever starts an executable path that came over IPC. Executables are only taken from
  the peer's **registry self-registration** (§15), and only if the file exists, is a local `.exe`
  (never on a share) and the peer allows being started.

Optional hardening (not required): the server records each client's **process id**
(`IntegrationPeer.ProcessId`, from `GetNamedPipeClientProcessId`). Finder may, for example, check
the client's image path against DownloadDock's registered `ExecutablePath` and verify its Authenticode
signature before honouring requests. That raises the bar, but it's still not a security boundary
against same-user code.

### 9.3 Elevation

Don't run either app elevated ("Run as administrator"). A pipe created by an elevated process gets a
High integrity label, and the non-elevated app is then refused access (`accessDenied`). The
integration then simply reports "not running", and DownloadDock falls back to File Explorer.

### 9.4 Path validation rules (`PathRules.Validate`)

Accepted: `C:\…`, mapped drives (`Z:\…`), UNC (`\\server\share\…`), `\\?\C:\…`,
`\\?\UNC\server\share\…`, with Unicode and spaces anywhere. `/` is treated as `\`.

Rejected: relative or drive-relative paths (`foo`, `C:foo`, `\foo`), device paths (`\\.\…`,
`\\?\GLOBALROOT…`, `\??\…`), `.` or `..` segments, empty segments, wildcards and `< > " | ? *`,
alternate data streams (`file:stream`), control characters, leading or trailing whitespace,
segments ending in a space or dot, and paths over 32,767 characters.

## 10. Finder client implementation guide

Finder needs to do six things.

### 10.1 Reference the library

Add `DownloadDock.Integration` (§13). Everything below uses its types.

### 10.2 Host Finder's server

On startup, before or after the main window, start an `IntegrationServer` on
`IntegrationEndpoints.FinderPipeName()`:

* `ServerName = IntegrationProtocol.FinderClientName` (`"FinderForWindows"`).
* `Capabilities` lists exactly the commands you handle (`finder.openFolder`, `finder.revealFile`,
  `finder.filesReceived`).
* Handlers must: validate with `PathRules.Validate` (throw
  `IntegrationException(IntegrationErrorCodes.InvalidPath, …)`), check existence (throw `notFound`),
  **marshal to the UI thread asynchronously**, and return promptly.
* Keep it running for the app's lifetime and dispose it on exit.

### 10.3 Connect to DownloadDock

Create one `ResilientIntegrationClient` on `IntegrationEndpoints.DownloadDockPipeName()` with
`ClientName = "FinderForWindows"`, subscribe to the events you want, and `Start()` it. It handles
DownloadDock starting later, restarting or crashing. Watch `StateChanged` to enable or disable your
"Show Downloads fan" UI.

### 10.4 Pair instantly

When your server's `PeerConnected` fires with `ClientName == "DownloadDock"`, call `Nudge()` on your
client. DownloadDock does the same for you.

### 10.5 Register your installation

On every start, write your registration (HKCU, no admin rights) so DownloadDock can find, and if the
user allows it, start Finder:

```csharp
AppRegistrationStore.Write(AppRegistrationStore.FinderKey, new AppRegistration(
    ExecutablePath: Environment.ProcessPath!, Version: "1.2.0",
    ProtocolVersion: IntegrationProtocol.Version, AllowLaunchByPeer: userAllowsBeingStarted));
```

### 10.6 Support the documented command line

When DownloadDock needs Finder and Finder isn't running, DownloadDock starts the registered
executable with **exactly** one of these (each path is a single argument):

```
FinderForWindows.exe --open-folder "C:\Users\Ana\Downloads"
FinderForWindows.exe --reveal "C:\Users\Ana\Downloads\Invoice.pdf"
```

Finder must:

* Treat these exactly like `finder.openFolder` / `finder.revealFile`, including validating the
  path. They come from DownloadDock, but command lines can come from anywhere.
* Be **single-instance**: a second launch forwards the request to the running instance and exits.
* Ignore unknown switches.

### 10.7 Asking DownloadDock for UI

Before `downloadDock.showFan` or `downloadDock.openSettings`, call
`AllowSetForegroundWindow(hello.ProcessId)` (user32). Your app is in the foreground because the user
just clicked in it; this passes that right to DownloadDock so its window can take focus. Without
it, Windows may show the fan without focus. DownloadDock still closes it on the next click outside.

### 10.8 Starting DownloadDock (optional)

If DownloadDock isn't running and the user asks Finder for the Downloads fan:

1. `AppRegistrationStore.ReadDownloadDock()`. If it's `null`, DownloadDock isn't installed; hide
   the feature.
2. If `AllowLaunchByPeer` is `false`, the user disabled it in DownloadDock's settings. **Do not
   start it.**
3. Otherwise start `ExecutablePath` with `--show-fan` (open the fan), `--background` (just start),
   or `--open-settings`. If DownloadDock is already running, the second instance forwards the request
   over its own pipe and exits, so there's never a duplicate.
4. Your resilient client connects as soon as DownloadDock's server is up (typically < 2 s; call
   `Nudge()`).

## 11. Example: connecting to DownloadDock

```csharp
using DownloadDock.Integration;

public sealed class DownloadDockLink : IAsyncDisposable
{
    private readonly ResilientIntegrationClient _client;
    private readonly Action<Action> _post; // e.g. a => dispatcher.BeginInvoke(a)

    public DownloadDockLink(Action<Action> postToUi)
    {
        _post = postToUi;
        _client = new ResilientIntegrationClient(new IntegrationClientOptions
        {
            PipeName = IntegrationEndpoints.DownloadDockPipeName(),
            ClientName = IntegrationProtocol.FinderClientName,
            ClientVersion = "1.2.0",
            Capabilities = new[] { IntegrationCapabilities.OpenFolder, IntegrationCapabilities.RevealFile, IntegrationCapabilities.FilesReceived },
            RequestTimeout = TimeSpan.FromSeconds(5),
        }, subscribeTo: new[]
        {
            IntegrationEvents.DownloadDetected, IntegrationEvents.ScreenshotCaptured,
            IntegrationEvents.FileAddedToDownloads, IntegrationEvents.DownloadsFolderChanged,
        });
        _client.StateChanged += s => _post(() => DownloadDockAvailable = s == IntegrationConnectionState.Connected);
        _client.EventReceived += OnEvent;          // background thread
        _client.Start();
    }

    public bool DownloadDockAvailable { get; private set; }

    public void Nudge() => _client.Nudge();

    private void OnEvent(IntegrationEvent e)
    {
        switch (e.Name)
        {
            case IntegrationEvents.DownloadDetected:
            case IntegrationEvents.ScreenshotCaptured:
            case IntegrationEvents.FileAddedToDownloads:
                var file = e.GetPayload<FileEventPayload>()!;
                _post(() => RefreshFolderIfOpen(file.Folder, highlight: file.Path));
                break;
            case IntegrationEvents.DownloadsFolderChanged:
                var folder = e.GetPayload<FolderChangedPayload>()!.Path;
                _post(() => UpdateDownloadsSidebarItem(folder));
                break;
        }
    }

    /// <summary>"Show Downloads" button in Finder.</summary>
    public async Task<bool> ShowFanAsync()
    {
        if (_client.Server is not { } server || !server.Capabilities.Contains(IntegrationCapabilities.ShowFan)) return false;
        AllowSetForegroundWindow(server.ProcessId);
        try
        {
            var r = await _client.SendAsync<ShowFanResult>(IntegrationCommands.ShowFan);
            return r?.State is "opened" or "alreadyOpen";
        }
        catch (IntegrationException ex) when (ex.Code is IntegrationErrorCodes.Unavailable or IntegrationErrorCodes.Timeout or IntegrationErrorCodes.Disconnected)
        {
            return false; // DownloadDock not running: offer to start it (§10.8) or hide the button
        }
    }

    public Task<DownloadDockStatus?> GetStatusAsync() => _client.SendAsync<DownloadDockStatus>(IntegrationCommands.GetStatus);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private void RefreshFolderIfOpen(string folder, string highlight) { /* Finder UI */ }
    private void UpdateDownloadsSidebarItem(string folder) { /* Finder UI */ }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
```

## 12. Example: receiving commands from DownloadDock

```csharp
using DownloadDock.Integration;

public sealed class FinderIntegrationHost : IAsyncDisposable
{
    private readonly IntegrationServer _server;

    public FinderIntegrationHost(IFinderShell shell, DownloadDockLink link, string finderVersion)
    {
        _server = new IntegrationServer(new IntegrationServerOptions
        {
            PipeName = IntegrationEndpoints.FinderPipeName(),
            ServerName = IntegrationProtocol.FinderClientName,
            ServerVersion = finderVersion,
            Capabilities = new[] { IntegrationCapabilities.OpenFolder, IntegrationCapabilities.RevealFile, IntegrationCapabilities.FilesReceived },
            Log = m => Trace.WriteLine("Integration: " + m),
        });

        _server.Handle(IntegrationCommands.OpenFolder, async ctx =>
        {
            var req = ctx.GetPayload<OpenFolderRequest>();
            RequireValidPath(req.Path);
            if (!await shell.DirectoryExistsAsync(req.Path, TimeSpan.FromSeconds(2)))   // offline shares can hang: time-box it
                throw new IntegrationException(IntegrationErrorCodes.NotFound, "Folder not found or drive unavailable.");
            var reused = await shell.InvokeOnUiAsync(() => shell.OpenFolder(req.Path, req.NewWindow));  // returns quickly
            return new FinderOpenResult { State = reused ? "reused" : "opened" };
        });

        _server.Handle(IntegrationCommands.RevealFile, async ctx =>
        {
            var req = ctx.GetPayload<RevealFileRequest>();
            RequireValidPath(req.Path);
            if (!await shell.ItemExistsAsync(req.Path, TimeSpan.FromSeconds(2)))
                throw new IntegrationException(IntegrationErrorCodes.NotFound, "File not found or drive unavailable.");
            await shell.InvokeOnUiAsync(() => shell.OpenContainingFolderAndSelect(req.Path, req.NewWindow));
            return new FinderOpenResult { State = "selected" };
        });

        _server.Handle(IntegrationCommands.FilesReceived, ctx =>
        {
            var req = ctx.GetPayload<FilesReceivedRequest>();
            var valid = req.Files.Take(IntegrationProtocol.MaxFilesPerNotification).Where(f => PathRules.IsValidAbsolutePath(f.Path)).ToList();
            shell.PostToUi(() => shell.RefreshViewsContaining(valid.Select(f => f.Path), highlight: req.Highlight)); // never steal focus
            return Task.FromResult<object?>(new FilesReceivedResult { Accepted = valid.Count });
        });

        // Pair instantly when DownloadDock connects to us.
        _server.PeerConnected += peer => { if (peer.ClientName == IntegrationProtocol.DownloadDockClientName) link.Nudge(); };
        _server.Start();
    }

    private static void RequireValidPath(string path)
    {
        if (PathRules.Validate(path) is { } why) throw new IntegrationException(IntegrationErrorCodes.InvalidPath, why);
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
```

## 13. Referencing the shared library

The contract lives in `src/DownloadDock.Integration` (namespace `DownloadDock.Integration`), targets
**net8.0** and works from .NET 8, 9 and 10 apps. Pick one option:

**A. Git submodule (recommended while both apps evolve together)**

```bash
git submodule add https://github.com/ghall702/DownloadDockGit.git external/DownloadDock
```

```xml
<ProjectReference Include="..\external\DownloadDock\src\DownloadDock.Integration\DownloadDock.Integration.csproj" />
```

Pin the submodule to a tagged or known-good commit, and update it deliberately.

**B. NuGet package**

The DownloadDock CI publishes `DownloadDock.Integration.<version>.nupkg` as the
**`DownloadDock.Integration-nupkg`** artifact of the *Build, test and publish* workflow. Or build it
yourself:

```bash
dotnet pack src/DownloadDock.Integration/DownloadDock.Integration.csproj -c Release -o ./nupkgs
```

```bash
dotnet nuget add source ./nupkgs --name downloaddock-local
```

```bash
dotnet add package DownloadDock.Integration --version 1.0.0
```

**Rules for both options:** reference only `DownloadDock.Integration`, never other DownloadDock
projects. The library has no WPF or UI dependencies and doesn't reveal any DownloadDock internals.
Its public API follows semantic versioning together with the protocol: the package major version
matches the protocol major version.

## 14. Native drag-and-drop compatibility

IPC never carries file contents. All file transfer between the apps, and with every other app, uses
standard OLE drag-and-drop.

### What DownloadDock provides as a drag source

Fan items, floating screenshot thumbnails and the screenshot editor's *Drag* handle all start drags
with:

* The **Shell's own data object** for the files (`IShellItemArray` → `BHID_DataObject`): the same
  object File Explorer puts on a drag. It contains `CF_HDROP`, `Shell IDList Array`, `FileNameW`
  and more.
* `Preferred DropEffect = DROPEFFECT_COPY`.
* Allowed effects **`DROPEFFECT_COPY | DROPEFFECT_LINK` only, never MOVE**, so a drop can never move or
  delete the user's download or screenshot.
* A Shell drag image (`IDragSourceHelper`).

**Finder as a drop target must therefore:**

1. Register an OLE drop target on its folder views (WPF `AllowDrop`, WinUI drop handlers, or
   `RegisterDragDrop`).
2. Accept `CF_HDROP` (`DataFormats.FileDrop` / `StandardDataFormats.StorageItems`) and, ideally,
   `Shell IDList Array`.
3. **Perform a copy** when the source allows only COPY. Don't require MOVE and don't fall back to
   "no drop" because MOVE isn't offered. File Explorer behaves this way, and it's what DownloadDock
   relies on.
4. Optionally use `IDropTargetHelper` so the drag image and "Copy to *Folder*" text show over
   Finder.

### What DownloadDock accepts as a drop target

| Target | Accepts | Result |
|---|---|---|
| The open Downloads fan | `CF_HDROP` **or** `Shell IDList Array` (file-system items only), with COPY in the allowed effects | Files **copied** into the Downloads folder (never moved, never overwritten; duplicates become `name (2).ext`); reported as `fileAddedToDownloads` |
| The DownloadDock tray icon (while any app's drag is over it) | Same | Drop on the icon copies into Downloads; holding there springs the fan open |
| Files already in the Downloads folder | — | Refused (no-op) |

**Finder as a drag source must therefore:**

1. Put real file-system paths on the data object. Best: use the Shell's data object
   (`SHCreateDataObject`, or `IShellItemArray.BindToHandler(BHID_DataObject)`). At minimum, provide
   `CF_HDROP`. A `Shell IDList Array`–only object also works with DownloadDock.
2. Include `DROPEFFECT_COPY` in the allowed effects. Offering COPY | MOVE | LINK, as Explorer does,
   is fine; DownloadDock always chooses COPY.
3. Not use virtual-file formats (`FileGroupDescriptorW`/`FileContents`) as the only format;
   DownloadDock needs real paths.

### Interoperability matrix

| # | Path | Mechanism | Status |
|---|---|---|---|
| 1 | DownloadDock → Finder folder | DownloadDock Shell data object → Finder drop target (copy) | Requires Finder to follow the rules above |
| 2 | Finder → DownloadDock fan / tray | Finder data object (`CF_HDROP` or IDList) → DownloadDock targets | ✅ Both formats tested automatically, including an IDList-only drag from a separate source and a drag from a separate process |
| 3 | DownloadDock → File Explorer | Shell data object | ✅ Tested (copy, original untouched) |
| 4 | File Explorer → DownloadDock | `CF_HDROP` + IDList | ✅ Same code path as #2 |
| 5 | DownloadDock → Chrome | `CF_HDROP` | Same data object Explorer uses; manual check listed in `docs/TESTING.md` |
| 6 | DownloadDock → PowerPoint | `CF_HDROP` | Manual check listed in `docs/TESTING.md` |
| 7 | Screenshot thumbnail → Finder | Same as #1 | Requires Finder to follow the rules above |
| 8 | Finder → Downloads (via the dock) | Same as #2 | ✅ |

## 15. Installation and deployment

* **Per-user, no admin.** Both apps run as the logged-in user. Pipes live only while the app runs.
  Nothing is installed system-wide for the integration.
* **Registration keys** (each app writes its own on every start, under HKCU; a per-machine
  installer may also write the HKLM equivalent):

  ```
  HKCU\Software\FinderForWindows\Integration    (written by Finder)
  HKCU\Software\DownloadDock\Integration        (written by DownloadDock)
      ExecutablePath    REG_SZ    C:\Users\Ana\AppData\Local\Programs\…\App.exe
      Version           REG_SZ    1.2.0
      ProtocolVersion   REG_SZ    1.0
      AllowLaunchByPeer REG_DWORD 1   (0 = the other app must never start this one)
  ```

  Readers use HKCU first, then HKLM, and ignore entries whose `ExecutablePath` isn't a valid,
  existing, local `.exe`. Uninstallers should delete their key.
* **DownloadDock's settings** (Settings › Finder Integration): *Enable Finder integration*, *Prefer
  Finder when opening folders*, *Prefer Finder for Show in folder*, *Start Finder when it isn't
  running*, *Fall back to File Explorer* (always on), *Allow Finder to start DownloadDock* (written
  as `AllowLaunchByPeer`). It also shows a live connection status and the detected Finder version.
  With the integration disabled, DownloadDock neither listens nor connects.
* **Updates:** if one app updates to a new *minor* version, nothing changes for the other. A new
  *major* version uses new pipe names; until both are updated, each reports the other as
  "incompatible" or "not running" and DownloadDock uses File Explorer.
* **Start-up order:** irrelevant. Either app may start first or be absent.

## 16. Troubleshooting

| Symptom | Check |
|---|---|
| Finder can't connect | Is DownloadDock running (tray icon)? Is *Settings › Finder Integration › Enable* on? Are both apps running as the same user in the same session, and neither elevated (§9.3)? |
| DownloadDock says "not installed" | Finder hasn't written `HKCU\Software\FinderForWindows\Integration\ExecutablePath`, or the path doesn't exist. |
| "Show in folder" opens File Explorer | Expected when Finder isn't running and starting it isn't allowed, Finder didn't answer within 3 s, Finder's hello lacks `finder.revealFile`, or the preference is off. The DownloadDock log says why. |
| Finder gets no events | Did you `system.subscribe` *after* connecting, and again after reconnecting? (The resilient client does this.) Check `seq` gaps (your event handler may be too slow). |
| "The integration pipe is already in use by another process" in DownloadDock's log | Another process created `DownloadDock.Integration.v1.<key>` first (e.g. a second copy started elevated). Close it and restart DownloadDock. |
| `accessDenied` | The pipe belongs to another account, or an elevated process. |

**Logs.** DownloadDock writes to `%LOCALAPPDATA%\DownloadDock\log.txt`. Integration lines start with
`Integration:` (server side) and `Finder link:` (connector side), and record outcomes such as
`Show in folder -> Explorer`.

**List the pipes (PowerShell):**

```powershell
Get-ChildItem \\.\pipe\ | Where-Object Name -Like '*Integration.v1.*'
```

**Smoke-test DownloadDock from PowerShell 7 (using the library):**

```powershell
Add-Type -Path .\DownloadDock.Integration.dll
$c = [DownloadDock.Integration.IntegrationClient]::new([DownloadDock.Integration.IntegrationClientOptions]@{
  PipeName = [DownloadDock.Integration.IntegrationEndpoints]::DownloadDockPipeName(); ClientName = 'ps-test' })
$null = $c.ConnectAsync().GetAwaiter().GetResult()
$c.SendAsync[DownloadDock.Integration.DownloadDockStatus]('downloadDock.getStatus', $null, $null, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
```

**Mock peers for testing.** The DownloadDock repository's tests contain a ready-made mock Finder server
(`MockFinder` in `tests/DownloadDock.Core.Tests/IntegrationTests.cs`), and an end-to-end test that
drives the real `DownloadDock.exe` as Finder would (`tests/DownloadDock.Windows.Tests/FinderIntegrationTests.cs`).
Both are good templates for Finder's own tests.
