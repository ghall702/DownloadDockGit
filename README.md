# DownloadDock

**The macOS Dock "Downloads" fan, brought to the Windows 11 system tray.**

Click the Downloads icon next to Wi-Fi, volume and battery and your most recent downloads rise from
it in a curved, overlapping fan. Each one shows a real preview of its contents: the first page of a
PDF, the photo itself, the first slide of a deck. Click a file to open it, or drag it straight into
Gmail, Canva, Spark, PowerPoint, File Explorer or any other app that accepts files.

DownloadDock is a native C#/WPF app on .NET 10. It isn't Electron and needs no account, cloud service
or subscription.

<p align="center">
  <img src="docs/images/fan-dark.jpg" width="320" alt="DownloadDock fan, dark theme">
  <img src="docs/images/fan-light.jpg" width="320" alt="DownloadDock fan, light theme">
</p>

<sub>These images are not mock-ups. The published `DownloadDock.exe` rendered them on a Windows CI runner
(`--fan-snapshot`) from sample downloads: PDF first pages via PDFium, DOCX/PPTX/XLSX rendered without
Office, photos, a Markdown page, and the Shell's own zip and folder icons. The taskbar strip is drawn
in for context.</sub>

---

## Features

| Area | What you get |
|---|---|
| **Fan** | Transparent, borderless, topmost window. Items sit on a large-radius arc that bends away from the icon, and each card rotates to follow it, like the macOS stack. Newest file is at the bottom. Cards overlap; the hovered card lifts above its neighbours and grows slightly. Opening and closing are staggered, eased animations. |
| **Placement** | Anchored to the real tray-icon rectangle (`Shell_NotifyIconGetRect`). Falls back to the click point, then to the notification area. Handles a taskbar at the top or bottom, auto-hide, per-monitor DPI (PerMonitorV2) and multiple monitors. Near the screen edge the curve flattens or mirrors so the fan stays fully on screen. |
| **Overflow** | Shows 9 files by default (3–20). If more exist than fit, the mouse wheel or PgUp/PgDn slides the fan through older files. The design stays a fan; the top item shows "*N more — scroll for older*". |
| **Previews** | PDF first page (PDFium, so no Acrobat needed). Images through WIC with EXIF rotation. DOCX first page, PPTX first slide and XLSX first sheet, with no Office required. Text and Markdown pages. Shell thumbnails for video and everything else. A file-type icon only when nothing better exists. |
| **Drag and drop** | Genuine OLE drag of the real file, using the same Shell data object File Explorer uses (`CF_HDROP`, `Shell IDList Array`, `FileNameW`…). Only **copy/link** effects are offered, so the original can never be moved or deleted. Shows a native drag image with "Copy to …" text. |
| **Tray** | Native notification-area icon whose glyph follows the light/dark taskbar and DPI. Left-click toggles the fan. Right-click menu: *Open Downloads Folder, Settings…, Refresh, Start with Windows, Exit*. The icon comes back after Explorer restarts. |
| **Monitoring** | Resolves the Downloads folder through `FOLDERID_Downloads`, so OneDrive and drive redirection work. `FileSystemWatcher` with debouncing and automatic recovery. Hides `.crdownload`, `.part`, `.partial`, `.tmp`, Office lock files and similar, plus files that are still being written. Handles renames, deletes and moves. |
| **Settings** | Folder, visible files, thumbnail size, animation speed (Off/Fast/Normal/Slow), theme (System/Light/Dark), start with Windows, show extensions, include subfolders, close after drop. Stored in `%APPDATA%\DownloadDock\settings.json`. |
| **Keyboard** | ↑/↓ to move, Enter/Space to open, Esc to close, PgUp/PgDn to page, Menu key for the context menu. |
| **Item menu** | Right-click a card for Open, Open with…, Show in folder, Copy (puts the real file on the clipboard) and Copy as path. |

---

## Install and use

1. Download `DownloadDock.exe` (self-contained win-x64; .NET doesn't need to be installed). Get it from
   the **DownloadDock-win-x64** artifact of the latest *Build, test and publish* workflow run, or build
   it yourself (below).
2. Put it somewhere permanent, for example `%LOCALAPPDATA%\Programs\DownloadDock\DownloadDock.exe`, and
   run it. On first run it registers itself to start with Windows (you can change this in Settings).
3. **Keep the icon visible:** Windows 11 hides new tray icons in the **^** overflow at first. To pin it
   next to Wi-Fi, volume and battery, open *Settings › Personalization › Taskbar › Other system tray
   icons* and turn **DownloadDock** on, or drag the icon out of the overflow onto the taskbar.
   *Settings…* in the app has a shortcut to that page.
4. Click the icon to open the fan. Click outside it, press Esc or click the icon again to close it.
   Closing the fan doesn't exit the app; use **Exit** in the right-click menu.

Launching the exe again while it's running just opens the fan of the running instance.

Windows SmartScreen may warn about an unsigned executable. Choose *More info › Run anyway*, or sign the
binary with your own certificate.

---

## Build

Requirements: Windows 10 1809+ or Windows 11, and the .NET 10 SDK (or Visual Studio 2022 17.14+ /
Visual Studio 2026 with the *.NET desktop development* workload).

```bash
dotnet build DownloadDock.sln -c Release
```

```bash
dotnet test tests/DownloadDock.Core.Tests -c Release
```

```bash
dotnet test tests/DownloadDock.Windows.Tests -c Release
```

```bash
dotnet publish src/DownloadDock/DownloadDock.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

The last command produces `publish\DownloadDock.exe` (about 65 MB, with PDFium included). For ARM64
devices use `-r win-arm64`.

In Visual Studio, open `DownloadDock.sln`, set **DownloadDock** as the startup project, pick
**Release** and run it.

The projects also compile on Linux and macOS (`EnableWindowsTargeting`), which is how the Core tests
run cross-platform. The Windows tests and the app itself need Windows.

### Diagnostics

`DownloadDock.exe` has two headless modes, used by CI and handy for troubleshooting a file whose
preview looks wrong:

```bash
DownloadDock.exe --thumbnail "C:\Users\me\Downloads\file.pdf" out.png 256
```

```bash
DownloadDock.exe --fan-snapshot "C:\Users\me\Downloads" fan.png dark 9 96
```

Each one writes a `.json` report next to the PNG; for thumbnails the report names the pipeline stage
that produced the image. The runtime log is at `%LOCALAPPDATA%\DownloadDock\log.txt`.

---

## How it works

```
src/
  DownloadDock.Core/          net10.0, platform-neutral, fully unit-tested
    Downloads/                FolderScanner (top-K heap), DownloadFileFilter, CompletionChecker, DownloadsMonitor
    Fan/                      FanLayoutCalculator (arc geometry, fit, mirroring), FanPager (overflow)
    Previews/                 SafePackage (zip-bomb/XXE-safe), DocxParser, PptxParser, XlsxParser,
                              EmbeddedThumbnail, TextPreviewReader
    Settings/                 AppSettings, SettingsStore
  DownloadDock/               net10.0-windows WPF app
    Tray/                     TrayIcon (Shell_NotifyIcon v4), TrayIconRenderer, NativeMenu
    Fan/                      FanWindow (layout, animation, hit-test, drag), FanItemView, FanAnchorResolver
    Dragging/                 FileDragSource (Shell data object + SHDoDragDrop + IDragSourceHelper)
    Thumbnails/               ThumbnailService (STA pool, LRU + disk cache), PdfThumbnailer (PDFium),
                              ImageThumbnailer (WIC), ShellThumbnailer, DocumentPreviewRenderer
    Settings/                 SettingsWindow (WPF Fluent theme) + view model
tests/
  DownloadDock.Core.Tests     cross-platform (101 tests)
  DownloadDock.Windows.Tests  PDFium, WIC, Shell, OLE drag end-to-end, fan window, Explorer drop
```

### Drag and drop

When the pointer moves past the system drag threshold (`SM_CXDRAG`/`SM_CYDRAG`), the fan:

1. Asks the Shell for the file's data object (`SHCreateShellItemArrayFromIDLists` → `BindToHandler(BHID_DataObject)`).
   This is the object Explorer itself drags, so any app that accepts a drag from Explorer accepts this one.
2. Sets `Preferred DropEffect = DROPEFFECT_COPY`.
3. Attaches a drag image with `IDragSourceHelper2` (drop-description text enabled).
4. Calls `SHDoDragDrop` with **`DROPEFFECT_COPY | DROPEFFECT_LINK` only**. A target can't move the
   file, because OLE never offers MOVE.

The drag runs in OLE's modal loop, so it keeps going after the pointer leaves the fan. The fan ignores
deactivation while a drag is in progress, then closes after a successful drop (configurable). If the
Shell can't provide a data object, a WPF `DataObject` with `FileDrop` (`CF_HDROP`) is used instead. The
drag never uses the clipboard, simulated input or keyboard shortcuts.

### Preview pipeline

| Type | Order of attempts |
|---|---|
| Images (jpg, png, gif, bmp, tiff, webp, heic, avif…) | WIC decode at reduced size + EXIF orientation → Shell thumbnail |
| PDF | **PDFium** first page (streamed, never loaded fully into memory) → Shell thumbnail |
| DOCX / PPTX / XLSX (and macro/template variants) | Shell thumbnail (Office's own renderer, if Office is installed) → thumbnail embedded in the file → **built-in renderer** (Word first page; PowerPoint first slide with theme colours, gradients, pictures and placeholders inherited from layout/master; Excel grid with headers, fills, bold and number/date formats) |
| ODT / ODS / ODP | Embedded `Thumbnails/thumbnail.png` → Shell |
| Text, Markdown, code, CSV, JSON… | Shell → built-in text-page renderer |
| Video and everything else | Shell thumbnail → Shell icon → drawn generic icon |

Rendering runs on two below-normal-priority STA threads. Identical requests are merged. Results are
kept in a memory LRU, and the expensive ones (PDF, Office, video) are also cached as PNG in
`%LOCALAPPDATA%\DownloadDock\ThumbCache`, keyed by path, size and modified time. New downloads are
pre-rendered as soon as they land, so the fan usually opens with every preview ready. The UI never
waits on rendering: a card shows a placeholder and fades in its preview when ready.

Office files are parsed defensively. ZIP entries are size-capped (against zip bombs), DTDs are
prohibited (against XXE), and relationship targets can't escape the package. Macros, OLE objects and
external links are never touched.

---

## Known limitations

- **Office previews without Office are approximations.** The built-in renderer handles text,
  headings, lists, tables, pictures, theme colours, gradients and simple shapes. It does not handle
  charts, SmartArt, WordArt, custom geometry, columns, headers/footers, or exact line breaking and
  pagination. With Microsoft Office installed, Office's own thumbnail provider is used instead. Many
  PPTX files (and DOCX files saved with "Save preview picture") also embed a thumbnail, which is used
  before the renderer.
- **Encrypted or password-protected** PDFs and Office files show an icon.
- **WebP/HEIC/AVIF** previews need the matching Windows codec (WebP is built into Windows 11;
  HEIF/AV1 come from the Microsoft Store "extensions"). Without the codec the Shell icon is shown.
- **SVG, RTF, legacy .doc/.xls/.ppt**: Shell thumbnail if a provider is installed, otherwise an icon.
- **Tray icon position:** when the icon sits in the hidden-icons (^) overflow, Windows reports the
  overflow flyout's position. The fan then rises from the click point, which is close by. Pinning
  the icon to the taskbar gives exact anchoring.
- **Vertical taskbars** (only possible with third-party tools on Windows 11) are handled as a
  bottom taskbar: the fan rises from the icon.
- **Receiving apps decide what they do with a drop.** For example, PowerPoint inserts images and
  embeds other files as objects; web pages only accept drops on their own upload areas. See
  [docs/TESTING.md](docs/TESTING.md) for which targets have been verified and how.
- The executable isn't code-signed.

## Third-party components

- **PDFium** (BSD-3-Clause / Apache-2.0), shipped as `pdfium.dll` via the
  [`bblanchon.PDFium.Win32`](https://github.com/bblanchon/pdfium-binaries) NuGet package.
- .NET 10 / WPF (MIT).

## Uninstall

1. Right-click the tray icon and choose **Exit**.
2. Untick *Start with Windows* first, or delete the `DownloadDock` value under
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
3. Delete the exe, `%APPDATA%\DownloadDock` and `%LOCALAPPDATA%\DownloadDock`.
