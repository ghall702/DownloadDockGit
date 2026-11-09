# DownloadDock testing report

This report separates what has been **verified by automated tests on Windows** from what still needs
a person at a real Windows 11 desktop. Nothing below is marked verified unless a test actually
exercised it.

## Where the automated tests ran

| Environment | What ran |
|---|---|
| Linux dev container (.NET 10.0.403) | Full solution build, including WPF via `EnableWindowsTargeting`; 101 Core tests; self-contained `win-x64` single-file publish |
| GitHub Actions `windows-latest` (Windows Server 2025, interactive desktop session), workflow `.github/workflows/build.yml` | Release build; 101 Core tests; 29 Windows tests (PDFium, WIC, Shell, real OLE drags, File Explorer drop, fan window); self-contained publish; smoke test of the **published exe** (`scripts/snapshot.ps1`) |

The CI run for the final commit passed every step. Test results (`.trx`) and rendered snapshots are
uploaded as the `test-results-and-snapshots` artifact, and the executable as `DownloadDock-win-x64`.

## Verified automatically

### Native drag-and-drop (highest priority)

| Check | Test | Result |
|---|---|---|
| Drag data object comes from the Shell and exposes the **real path** as `CF_HDROP` | `Shell_data_object_exposes_the_real_file_as_CF_HDROP` | ✅ Windows |
| It also carries `Shell IDList Array` and `FileNameW` (formats Explorer, Office and Chromium read) | same | ✅ Windows |
| Multiple files in one data object | `Multiple_files_are_supported` | ✅ Windows |
| `Preferred DropEffect` = COPY | `Preferred_drop_effect_is_copy` | ✅ Windows |
| Fallback WPF data object carries `CF_HDROP` | `Fallback_data_object_also_carries_CF_HDROP` | ✅ Windows |
| **End to end:** a real OLE drag (driven by `SendInput`) from the drag source to an OLE drop target in another COM apartment delivers the file path with effect COPY. The target sees COPY offered and MOVE not offered. The original file is unchanged afterwards. | `End_to_end_drag_delivers_the_file_with_copy_semantics_and_keeps_the_original` | ✅ Windows |
| A target that only accepts MOVE gets nothing, and the original stays put | `A_target_that_wants_to_move_cannot_take_the_original` | ✅ Windows |
| **End to end into File Explorer:** a real drag onto an open Explorer folder window copies the file there; the source remains, with identical bytes | `Dropping_on_file_explorer_copies_the_file` | ✅ Windows |
| A drag image is produced from the thumbnail | `Drag_image_is_created_from_a_thumbnail` | ✅ Windows |

### Previews

| Check | Test | Result |
|---|---|---|
| PDF first page rendered by **PDFium** (no Acrobat on the runner): correct size and aspect, page content visible (red block in the centre, white margin) | `Pdf_first_page_is_rendered_by_pdfium`, `Landscape_pdf_keeps_its_aspect` | ✅ Windows |
| Corrupt PDF doesn't crash and falls back to an icon | `Corrupt_pdf_does_not_crash_and_falls_back_to_an_icon` | ✅ Windows |
| Images decoded at reduced size | `Images_are_decoded_at_reduced_size` | ✅ Windows |
| EXIF orientation applied (pixel-checked) | `Exif_orientation_is_applied` | ✅ Windows |
| DOCX, PPTX and XLSX get **content** previews with no Office installed | `Office_documents_get_content_previews_without_office` | ✅ Windows |
| Built-in renderers: page aspect, slide aspect, theme gradient colour (pixel-checked), sheet grid | `Built_in_renderers_draw_the_documents` | ✅ Windows |
| Text/Markdown render as a page | `Text_files_render_as_pages` | ✅ Windows |
| Unknown types use the Shell icon | `Unknown_formats_use_the_shell_icon` | ✅ Windows |
| Memory and disk caching; identical concurrent requests merged | `Service_caches_in_memory_and_on_disk`, `Concurrent_requests_are_deduplicated` | ✅ Windows |
| DOCX/PPTX/XLSX parsing (styles, theme colours, placeholders, groups, dates, shared strings…) | `DocxParserTests`, `PptxParserTests`, `XlsxParserTests` | ✅ Linux + Windows |
| Zip-bomb cap, XXE rejected, relationship path traversal neutralised | `SafePackageTests` | ✅ Linux + Windows |
| **Published single-file exe** renders every sample type. Sources reported: PDF → `pdfium`, JPG/PNG → `wic`, DOCX/PPTX/XLSX → `rendered`, MD → `text`, ZIP → `shell-icon` | `scripts/snapshot.ps1` in CI (fails the build if a previewable type falls back to an icon) | ✅ Windows |

### Fan, geometry and window

| Check | Test | Result |
|---|---|---|
| Items rise from the anchor along an arc bending right; rotation increases; drift accelerates (true curve, not a line) | `FanLayoutTests` | ✅ |
| Neighbours overlap but stay individually selectable; items nearer the icon sit on top | `FanLayoutTests` | ✅ |
| The whole fan, rotated labels included, stays inside the work area for icons anywhere along the taskbar; the curve flattens or mirrors near the right edge | `Fan_stays_within_the_work_area`, `Icon_near_the_right_edge_flattens_or_mirrors_the_curve` | ✅ |
| Fewer items on short screens or with bigger thumbnails; top-taskbar (downward) fan; high-DPI DIP space | `FanLayoutTests` | ✅ |
| Overflow paging (wheel / PgUp/PgDn) clamps correctly | `FanPagerTests` | ✅ |
| Opening the fan lays it out exactly once, and each card has real layout sizes (name pill wide enough for the text; card sized to its preview) | `Opens_a_curved_fan_with_one_view_per_file_plus_the_folder_item`, `Item_view_binds_name_and_card_size_to_its_view_model` | ✅ Windows |
| Fan window opens with one card per file plus the folder item; newest file at the bottom; rotation and height increase upward; **hit-testing the centre of each overlapping, rotated card returns that card** | `Opens_a_curved_fan_with_one_view_per_file_plus_the_folder_item` | ✅ Windows |
| Empty folder shows only the folder item ("No downloads yet") | `Empty_folder_shows_only_the_folder_item` | ✅ Windows |
| Closing the fan (animated) only hides it; it reopens; the app keeps running | `Closing_the_fan_only_hides_it` | ✅ Windows |
| Tray glyph renders at the requested size and converts to an HICON | `Tray_glyph_is_rendered_at_the_requested_size` | ✅ Windows |
| Anchor fallback when the icon rectangle is unknown | `Anchor_falls_back_when_the_icon_position_is_unknown` | ✅ Windows |
| Downloads known folder resolves | `Downloads_known_folder_resolves` | ✅ Windows |

### Downloads monitoring

| Check | Test | Result |
|---|---|---|
| `.crdownload`, `.part`, `.partial`, `.download`, `.tmp`, `~$` lock files, `desktop.ini` and others are hidden | `DownloadFileFilterTests` | ✅ |
| Chrome-style `.crdownload` → final-name rename appears; a later rename and delete are tracked | `Detects_new_renamed_and_deleted_files` | ✅ Linux + Windows |
| Newest first; "date added" = later of creation/write time | `Newest_download_is_first`, `FolderScannerTests` | ✅ |
| Files still open for writing are held back; Firefox `.part` placeholders held back | `CompletionCheckerTests` (real lock check on Windows) | ✅ |
| Bursts of 100 file events are debounced into a handful of rescans | `Bursts_of_changes_are_debounced` | ✅ |
| Missing folder is picked up when it appears | `Missing_folder_is_picked_up_when_created` | ✅ |
| **5,000-file folder** scanned for the newest 200 in well under the 1.5 s budget | `Scanning_thousands_of_files_is_fast` | ✅ |

### Settings

Round-trip, defaults, corrupt-file recovery (backed up as `.corrupt`) and range clamping:
`SettingsStoreTests` ✅.

### Visual check of the published exe

CI runs `DownloadDock.exe --fan-snapshot` on a folder of sample downloads and renders the real fan
window offscreen. The step fails if any card's name label wasn't laid out, or if opening the fan
rebuilt the layout more than once. The latest snapshot is in
[`docs/images/fan-light.jpg`](images/fan-light.jpg) and [`docs/images/fan-dark.jpg`](images/fan-dark.jpg).

**A bug this caught:** the first snapshots showed empty name pills and sliver-thin cards, even though
every unit test passed. Probing the layout from inside the snapshot showed the window's `DpiChanged`
handler re-ran the whole layout, whose `SetWindowPos` raised `DpiChanged` again. That rebuilt the fan
hundreds of times a second (918 layouts during one snapshot), so the cards were never measured with
their data and the CPU stayed busy. The handler now reacts only to a real DPI change, deferred and
coalesced, and only repositions the window. Regression tests assert exactly one layout per open
and real element sizes, and the smoke-test step got 3–5× faster.

## Not verified automatically: manual checklist

These need a real Windows 11 desktop with the target apps signed in. **I have not run them**, so
treat them as untested until someone ticks them off. Run them with the published `DownloadDock.exe`.

### Tray and fan

- [ ] After launch the icon appears in the notification area. Pin it via *Settings › Personalization › Taskbar › Other system tray icons*.
- [ ] The glyph is white on a dark taskbar and dark on a light taskbar; switching the Windows theme updates it live.
- [ ] Clicking the icon opens the fan directly above it; the bottom card sits just above the icon.
- [ ] Opening animation: cards rise from the icon one after another; closing collapses them back.
- [ ] Clicking the icon again closes the fan (it must not reopen immediately).
- [ ] Clicking the desktop or another window closes the fan. The app stays in the tray.
- [ ] Hovering a card lifts it above its neighbours, enlarges it slightly and highlights its name.
- [ ] Clicking a PDF opens it in the default PDF app; clicking an image opens Photos; the top item opens the Downloads folder.
- [ ] Right-click a card: Open / Open with… / Show in folder / Copy / Copy as path all work.
- [ ] Keyboard: ↑/↓ moves the highlight, Enter opens, Esc closes.
- [ ] With 30+ files in Downloads, the wheel scrolls to older files and the top label shows the counts.
- [ ] 125 %, 150 % and 200 % scaling: thumbnails sharp, fan anchored correctly.
- [ ] Two monitors with different scaling, taskbar on the secondary monitor: the fan opens on the correct monitor at the correct size.
- [ ] Icon left in the ^ overflow flyout: the fan opens near the click point.
- [ ] Restart Explorer (Task Manager › Windows Explorer › Restart): the icon comes back.
- [ ] Right-click menu: Open Downloads Folder, Settings…, Refresh, Start with Windows (toggles a check mark), Exit (removes the icon and ends the process).
- [ ] Sign out and in: DownloadDock starts automatically when *Start with Windows* is on.

### Drag-and-drop targets

For each target: start dragging a card, move slowly into the target (the fan must stay open while
dragging), drop, then confirm the original file is still in Downloads.

| # | Target | Steps | Expected | Status |
|---|---|---|---|---|
| 1 | Chrome, HTML upload area | Open any page with a drop zone (e.g. a file-upload test page); drag a PDF onto it | Page receives the file | ☐ not yet tested |
| 2 | **Gmail in Chrome** | Compose a new message; drag a PDF onto the compose body | "Drop files here" overlay appears; PDF attaches | ☐ not yet tested |
| 3 | **Canva in Chrome** | Open a design; drag a JPG/PNG (and a PDF) onto the editor or *Uploads* panel | Upload starts; the image appears in Uploads | ☐ not yet tested |
| 4 | **Spark Mail** | New email; drag a PDF onto the message | File attaches | ☐ not yet tested |
| 5 | **PowerPoint** | Open a slide; drag a PNG/JPG onto it | Picture inserted on the slide | ☐ not yet tested |
| 5b | PowerPoint | Drag a PDF onto a slide | Inserted as an embedded object/icon (PowerPoint's normal behaviour) | ☐ not yet tested |
| 6 | **File Explorer** | Drag onto a folder window and onto a desktop folder icon | File **copied** (not moved); "Copy to …" shown under the cursor | ✅ folder window verified automatically in CI; ☐ desktop icon not yet tested |
| 7 | Same-drive Explorer target | Drag into another folder on the same drive as Downloads | Still a **copy** (Explorer would normally move on the same drive) | ☐ not yet tested (MOVE is never offered; covered by test) |
| 8 | Esc during drag | Press Esc mid-drag | Drag cancelled; fan still open; nothing copied | ☐ not yet tested |

Why rows 1–5 should work: DownloadDock drags the same Shell data object File Explorer drags, and
Chrome, Gmail, Canva, Spark and PowerPoint all accept drags from Explorer. The automated tests show
the payload is a genuine `CF_HDROP` + Shell IDList file drop that a separate OLE target receives
correctly and that Explorer accepts. Each receiving app still decides for itself what to do with a
drop, so these rows stay unchecked until someone actually tries them.

### Monitoring

- [ ] Download a file in Chrome: no `.crdownload` card while downloading; the finished file appears at the bottom of the open fan within about a second.
- [ ] Same in Edge and Firefox (`.part` hidden).
- [ ] Rename, delete and move a file in Downloads: the fan updates without a restart.
- [ ] A Downloads folder with thousands of files: the fan opens instantly, CPU near 0 % when idle.

### Previews on a real desktop

- [ ] PDF, JPG, PNG, WebP, GIF show the actual content.
- [ ] DOCX / PPTX / XLSX: with Office installed you should see Office's own thumbnails; without Office, DownloadDock's rendered previews.
- [ ] MP4 shows a video frame (Shell thumbnail).

## Reproducing the automated runs

```bash
dotnet test tests/DownloadDock.Core.Tests -c Release
```

```bash
dotnet test tests/DownloadDock.Windows.Tests -c Release
```

The drag tests move the real mouse pointer for a few seconds, so don't touch the mouse while they
run. They are skipped automatically when there's no interactive desktop.

```powershell
./scripts/snapshot.ps1 -Exe publish/DownloadDock.exe -Samples <folder with sample files> -Out snapshots
```
