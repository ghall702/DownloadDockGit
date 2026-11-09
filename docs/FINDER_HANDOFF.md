# Handoff: integrating Finder for Windows with DownloadDock

**Audience:** the agent or engineer building *Finder for Windows* in its own repository.
**Goal:** make Finder talk to DownloadDock over its local integration protocol (v1.0), and make
drag-and-drop work both ways. You don't need DownloadDock's source, only this page, the shared
library, and `docs/FINDER_INTEGRATION.md` for details.

## What DownloadDock already does (done, tested; don't re-implement)

* Hosts a named-pipe server `DownloadDock.Integration.v1.<session-key>` with commands
  `downloadDock.showFan`, `downloadDock.closeFan`, `downloadDock.openSettings` and
  `downloadDock.getStatus`, plus events `downloadDetected`, `screenshotCaptured`,
  `fileAddedToDownloads`, `downloadsFolderChanged`, `fanOpened` and `fanClosed` (all prefixed
  `downloadDock.`).
* Connects (with automatic reconnect) to **Finder's** pipe `FinderForWindows.Integration.v1.<session-key>`
  and calls `finder.openFolder`, `finder.revealFile` and `finder.filesReceived` if Finder
  advertises them.
* Falls back to File Explorer whenever Finder is missing, not running, slow (> 3 s), failing or
  incompatible.
* Starts Finder (if registered and allowed) with `--open-folder <path>` or `--reveal <path>`.
* Accepts file drops from Finder on its fan and tray icon (`CF_HDROP` or `Shell IDList Array`), and
  starts copy-only drags with the Shell's own data object.

## What Finder must implement (checklist)

1. **Reference the contract library** `DownloadDock.Integration` (net8.0, no dependencies). Use a git
   submodule + `ProjectReference` to `src/DownloadDock.Integration/DownloadDock.Integration.csproj`,
   or the `DownloadDock.Integration` NuGet package (CI artifact *DownloadDock.Integration-nupkg*, or
   `dotnet pack`). Reference nothing else from DownloadDock.

2. **Host Finder's server** at startup:

   ```csharp
   var server = new IntegrationServer(new IntegrationServerOptions {
       PipeName = IntegrationEndpoints.FinderPipeName(),
       ServerName = IntegrationProtocol.FinderClientName,          // "FinderForWindows"
       ServerVersion = finderVersion,
       Capabilities = new[] { IntegrationCapabilities.OpenFolder, IntegrationCapabilities.RevealFile, IntegrationCapabilities.FilesReceived },
   });
   server.Handle(IntegrationCommands.OpenFolder,   ctx => /* validate, open folder (reuse window unless NewWindow), return new FinderOpenResult{State="opened"|"reused"} */);
   server.Handle(IntegrationCommands.RevealFile,   ctx => /* validate, open parent + select item, return new FinderOpenResult{State="selected"} */);
   server.Handle(IntegrationCommands.FilesReceived, ctx => /* refresh open views of those folders, optional highlight, NEVER steal focus; return new FilesReceivedResult{Accepted=n} */);
   server.PeerConnected += p => { if (p.ClientName == IntegrationProtocol.DownloadDockClientName) dockClient.Nudge(); };
   server.Start();
   ```

   In every handler:
   * Validate the path: `if (PathRules.Validate(path) is {} why) throw new IntegrationException(IntegrationErrorCodes.InvalidPath, why);`
   * Check existence time-boxed (~2 s; offline shares hang); if missing, throw
     `IntegrationErrorCodes.NotFound`.
   * Do UI work through your dispatcher **asynchronously**, and answer within ~1 s. DownloadDock
     waits only 3 s for open and reveal before using File Explorer.
   * **Never** execute, open-with, modify or delete anything because of a request.
   * Support local, UNC, mapped-drive, `\\?\` and Google Drive (`G:\My Drive\…`) paths, with
     spaces and Unicode.

3. **Connect to DownloadDock** with one long-lived resilient client:

   ```csharp
   var dockClient = new ResilientIntegrationClient(new IntegrationClientOptions {
       PipeName = IntegrationEndpoints.DownloadDockPipeName(),
       ClientName = IntegrationProtocol.FinderClientName,
       ClientVersion = finderVersion,
       Capabilities = new[] { IntegrationCapabilities.OpenFolder, IntegrationCapabilities.RevealFile, IntegrationCapabilities.FilesReceived },
   }, subscribeTo: new[] { IntegrationEvents.DownloadDetected, IntegrationEvents.ScreenshotCaptured, IntegrationEvents.FileAddedToDownloads, IntegrationEvents.DownloadsFolderChanged });
   dockClient.EventReceived += e => dispatcher.BeginInvoke(() => Handle(e));   // events arrive on a background thread
   dockClient.StateChanged  += s => dispatcher.BeginInvoke(() => showDownloadsButton.IsEnabled = s == IntegrationConnectionState.Connected);
   dockClient.Start();
   ```

   * The "Show Downloads fan" button: call `AllowSetForegroundWindow(dockClient.Server.ProcessId)`,
     then `await dockClient.SendAsync<ShowFanResult>(IntegrationCommands.ShowFan)`. The result is
     `"opened"` or `"alreadyOpen"`.
   * Settings: `IntegrationCommands.OpenSettings` (also call `AllowSetForegroundWindow` first).
     Status: `IntegrationCommands.GetStatus` returns a `DownloadDockStatus` (Downloads folder,
     screenshot folder, version, fan open).
   * Subscribing to an event means DownloadDock **won't** also send `finder.filesReceived` for that
     kind (no duplicates). Choose one route per kind.
   * Catch `IntegrationException`. Its `.Code` is one of `IntegrationErrorCodes`; `unavailable`,
     `disconnected` and `timeout` mean "DownloadDock isn't available right now".

4. **Register your installation** on every start (HKCU, no admin):

   ```csharp
   AppRegistrationStore.Write(AppRegistrationStore.FinderKey,
       new AppRegistration(Environment.ProcessPath!, finderVersion, IntegrationProtocol.Version, AllowLaunchByPeer: settings.AllowDownloadDockToStartFinder));
   ```

   Delete `HKCU\Software\FinderForWindows\Integration` on uninstall.

5. **Command line and single instance.** Support `--open-folder "<path>"` and `--reveal "<path>"`,
   with the same validation as the IPC handlers. A second launch must forward to the running
   instance and exit. Ignore unknown switches.

6. **Starting DownloadDock (optional "Show Downloads" when it isn't running).**
   `AppRegistrationStore.ReadDownloadDock()`: if `null`, DownloadDock isn't installed (hide the
   button). If `AllowLaunchByPeer == false`, **don't start it**. Otherwise run `ExecutablePath`
   with `--show-fan` (or `--background` / `--open-settings`); a running instance forwards and exits.
   Never take an executable path from IPC.

7. **Native drag-and-drop:**
   * *Drops into Finder* (from DownloadDock's fan, screenshot thumbnails or Explorer): accept
     `CF_HDROP` (and ideally `Shell IDList Array`), and **copy when only COPY is allowed**.
     DownloadDock offers COPY | LINK, never MOVE. Use `IDropTargetHelper` for drag images.
   * *Drags out of Finder* (to DownloadDock, Explorer, Chrome…): use the Shell's data object
     (`SHCreateDataObject` / `BHID_DataObject`), or at least `CF_HDROP` with real paths; include
     `DROPEFFECT_COPY` in the allowed effects; don't rely on virtual-file formats alone.
   * Don't send file contents over IPC, ever.

8. **Don't run elevated.** An elevated Finder can't be reached by non-elevated DownloadDock
   (integrity levels); it just looks "not running".

## Acceptance tests to write in Finder

Copy the patterns from DownloadDock's tests: `MockFinder` and `FakeSurface` in
`tests/DownloadDock.Core.Tests/IntegrationTests.cs`, and the real-exe test in
`tests/DownloadDock.Windows.Tests/FinderIntegrationTests.cs`.

* [ ] Finder starts before DownloadDock → connects within a few seconds of DownloadDock starting.
* [ ] DownloadDock restarts → Finder reconnects and re-subscribes automatically.
* [ ] `showFan` returns `opened`, then `alreadyOpen`; `getStatus.fanOpen` is `true`.
* [ ] Handlers reject `..\x`, `C:relative`, `\\.\PhysicalDrive0` and `C:\a\*.txt` with `invalidPath`,
      and missing paths with `notFound`.
* [ ] `finder.revealFile` selects files with spaces and Unicode on a local disk, a UNC share and
      Google Drive.
* [ ] A file dragged from DownloadDock's fan into a Finder window is **copied** (the original stays in
      Downloads).
* [ ] A file dragged from Finder onto DownloadDock's open fan or tray icon appears in Downloads.
* [ ] Killing Finder mid-request: DownloadDock opens File Explorer instead (verify in DownloadDock's
      log: `Show in folder -> Explorer`).

## Key facts

| Item | Value |
|---|---|
| Protocol | 1.0. Compatible when majors match; the lower minor is used. |
| Framing | 4-byte LE length + UTF-8 JSON, ≤ 64 KiB |
| First message | `system.hello` within 5 s |
| Pipe names | `IntegrationEndpoints.DownloadDockPipeName()` / `FinderPipeName()` (per user + logon session) |
| Pipe access | Logon session only, network denied, owner verified by the client |
| Trust | Same-user processes can impersonate either app: no command may be dangerous |
| DownloadDock log | `%LOCALAPPDATA%\DownloadDock\log.txt` (`Integration:` / `Finder link:` lines) |
| Full reference | `docs/FINDER_INTEGRATION.md` |
