# Architecture

Clipboard Manager is deliberately small: two production projects, no UI or dependency-injection
framework, one runtime dependency (`Microsoft.Data.Sqlite.Core` with the SQLitePCLRaw provider for
Windows' own `winsqlite3.dll`).

```
src/ClipboardManager.Core   net10.0          history store, capture policy, search, previews, settings, log, update rules
src/ClipboardManager        net10.0-windows  WPF app: Win32 interop, image codec, windows, tray, updater, composition root
tests/ClipboardManager.Tests                  unit, store, privacy, Windows integration tests
tools/AssetGen                                icon, README screenshots, QR code, search benchmark (never shipped)
build/                                        release, self-test and hygiene scripts used by CI
```

## Runtime structure

Three execution contexts, all idle without CPU load:

```
 UI thread (WPF dispatcher)                 Clipboard reader thread           DB worker (async, no thread while idle)
 ───────────────────────────                ───────────────────────           ──────────────────────────────────────
 HostWindow (hidden top-level)              waits on an event                 single-consumer channel
   WM_CLIPBOARDUPDATE ── pause check ──►    sleeps 50 ms (coalesce)           SQLite (WAL, secure_delete,
   WM_HOTKEY ─► PopupController             one OpenClipboard session:          exclusive lock, FULL sync)
   tray callbacks ─► TrayMenu                 markers → owner → exclusion     ── versioned change batches ──►
   TaskbarCreated, settings, session          → text (size-bounded)           HistoryIndex (UI thread)
 PopupWindow (pre-built, reused)              or picture: header check, copy
 SettingsWindow (on demand)                 close clipboard, then decode
                                              picture → PNG + thumbnail
                                            policy (secrets, blank, size)
                                            ──── capture ─────────────────►
```

- **HostWindow** is a hidden *top-level* window, not a message-only window: message-only windows do
  not receive `TaskbarCreated`, `WM_SETTINGCHANGE` or session-end messages.
- **ClipboardReader** never runs on the UI thread. Delayed rendering (Excel, RDP, VMs) can block
  `GetClipboardData` for up to 30 seconds; only the reader waits. All checks happen inside a single
  `OpenClipboard` session, so content cannot change between the privacy-marker check and the read.
  "Latest wins": bursts of notifications collapse into one read. A flood breaker pauses reading when
  a sync tool ping-pongs identical content.
- **Pictures** are read only when the copy has no text (text wins, e.g. for Excel cells): the
  registered `"PNG"` format first, then `CF_DIB`, which Windows synthesizes from `CF_BITMAP` and
  `CF_DIBV5`, so no GDI is needed. Inside the clipboard session the header is checked against the
  limits (16,384 px per side, 25 MP) and the bytes are copied into native memory; `ImageCodec`
  decodes them after the clipboard was released. 24/32-bit DIBs are parsed directly (32-bit alpha
  counts only for `BI_RGB` or an explicit alpha mask, and an all-zero alpha means "opaque"); other
  layouts go through WIC's BMP decoder. The result is always encoded anew – a PNG at 96 DPI without
  metadata, 24-bit when opaque – plus a thumbnail of at most 96×56 px, so the same picture copied as
  PNG or as bitmap gives the same entry. WPF imaging (WIC) runs on the reader thread and on
  thread-pool threads; everything that crosses threads is frozen.
- **ClipboardWriter** writes only on explicit user action (never in reaction to a clipboard change),
  so the app cannot start a sync loop. An in-process gate keeps our reader and writer from racing.
  A picture goes back as `"PNG"` (the stored bytes, with transparency) plus a 24-bit `CF_DIB`
  composited on white for apps that only read bitmaps; the DIB is built off the UI thread.
- **DbWorker** is the only code that touches SQLite. Every change is published as a versioned
  batch; the UI applies batches strictly in order, so a capture and a delete can never resurrect an
  entry.
- **PopupWindow** is created once, pre-warmed invisibly (DWM-cloaked) and then only shown and
  hidden. While open it works on a snapshot, so the list never shifts under the cursor. Image rows
  decode their thumbnail (kept in the index) the first time they are shown; the preview pane shows
  it at once and loads the picture itself off the UI thread, one at a time and never during the
  pre-warm.
- **UpdateService** (`ClipboardManager.Updates`) is the only networking code; an instance exists
  only for one click on *Check for updates* or *Install update*. It asks the GitHub API for the
  latest release, then streams the EXE for the process architecture to `<exe>.new` while hashing
  it, and keeps it only if size and SHA-256 match `SHA256SUMS.txt`. Redirects are followed by hand
  and only to GitHub hosts. No running image is ever renamed – a single-file app keeps reading
  its assemblies from its own path while it runs. Instead the running version starts the download
  as it is (`<exe>.new --finish-update <pid>`) and exits; the download waits for it, moves the
  previous `<exe>` to `<exe>.old`, copies itself to `<exe>` (rolled back on failure) and starts that
  with `--updated-from <pid>` without ever loading the UI. The final process waits for the helper,
  takes the single-instance mutex and deletes `<exe>.new` and `<exe>.old`.

## Startup

`AppController.Start` makes the app reachable first and does everything optional last:

1. Settings, theme, host window, hotkey, tray icon and clipboard listener. The store opens on the
   DB worker; after an unclean shutdown it runs `PRAGMA quick_check` there. That check stays in
   the startup path on purpose: it decides whether the database must be quarantined before the
   first write, and it never runs on the UI thread.
2. The popup window is created (not yet rendered).
3. First-run tasks. A manual first start registers autostart, creates the Start menu entry and
   shows the welcome window; a later manual start opens the history. Autostart is registered only
   from a permanent folder: `%TEMP%` (ZIP and archive-tool extraction), UNC paths and removable,
   network or optical drives are skipped because the entry would soon point to nothing or the
   drive may be missing at sign-in. Downloads and Desktop count as permanent (Downloads gets a
   tip to move the EXE). A user's "off" – in the app, in Task Manager or in Settings – is never
   overwritten, and an updated EXE at the same path keeps the entry as it is.
4. Deferred work: popup pre-warming and removal of old single-file extraction folders
   (`%TEMP%\.net\ClipboardManager\*`).

**Quiet start.** With `--autostart` (the Run entry's command), `StartupWarmup` holds step 4 back
for one 45-second one-shot `DispatcherTimer`, lowers the process to `BelowNormal` until then and
restores `Normal` afterwards (a priority set by someone else is left alone). Notices raised in that
phase (store recovery, hotkey taken) wait until the user first opens the popup or the tray menu,
at the latest until the delay ends. The priority is lowered only after capture, hotkey and tray icon are up.
No window is shown and nothing is activated. Capture and the hotkey work from the first second;
pressing the hotkey, clicking the tray icon or starting the EXE again ends the quiet phase at once
(the popup opens directly, the pre-warm is then no longer needed).

## Data

`%LOCALAPPDATA%\Secoolioo\ClipboardManager\`: `history.db` (+ `-wal`), `settings.json`,
`logs\app.log` (1 MB + one backup), `session.active` (unclean-shutdown marker), and at most one
`history.corrupt.db` after a recovery. `CLIPBOARDMANAGER_DATA_DIR` overrides the folder (tests,
self-test). An update briefly leaves `<exe>.new` / `<exe>.old` next to the EXE; the next start
deletes them.

```sql
CREATE TABLE entries (                                    -- schema version 2
  id INTEGER PRIMARY KEY, hash BLOB NOT NULL UNIQUE,      -- SHA-256 of the UTF-16 text, or of a domain prefix + the PNG
  kind INTEGER NOT NULL DEFAULT 0,                        -- 0 text, 1 image
  char_count INTEGER NOT NULL, line_count INTEGER NOT NULL,
  width INTEGER NULL, height INTEGER NULL, byte_count INTEGER NOT NULL DEFAULT 0,  -- images: pixels, PNG size
  created_at INTEGER NOT NULL, last_used_at INTEGER NOT NULL, pinned_at INTEGER NULL,  -- Unix ms
  search_head TEXT NULL,                                  -- first 4,096 chars, only for longer texts
  thumb BLOB NULL,                                        -- images: PNG thumbnail, part of the index
  text TEXT NOT NULL,                                     -- '' for images; loading the index never reads long texts
  image BLOB NULL                                         -- images: the PNG; read only to copy or preview
);
```

Large columns come last, so building the index never walks the overflow pages of a long text or a
picture; the view the index reads (`temp.all_entries`) does not even contain `image`. Memory-only
mode attaches an in-memory database (`mem.entries`) for unpinned entries; pins stay in
`main.entries`. Migrations use `PRAGMA user_version`; a database from a newer version is never
touched (the app falls back to memory). Version 1 → 2 (0.11) rebuilds the table inside the migration
transaction instead of `ALTER TABLE … ADD COLUMN`, which would append the new columns after `text`;
`secure_delete` zeroes the old pages and the WAL is truncated afterwards.

Retention keeps the newest unpinned entry in any case and then, newest first: at most *maximum
entries* in total, texts within 20 million characters and pictures within 128 MB of PNG – each
budget strictly oldest first within its kind, so pictures never push texts out and vice versa. The registry holds only autostart state:
`HKCU\…\Run\Secoolioo.ClipboardManager` and `HKCU\Software\Secoolioo\ClipboardManager`
(first-run and autostart decision).

## Tests

| Category | What | Where it runs |
|---|---|---|
| Unit | policy, secrets, search, previews, settings, hotkeys, popup selection rules and image preview loading, quiet-start rules, localization | everywhere |
| Image codec | DIB layouts (24/32 bit, top-down, bit fields, V5 alpha, palettes), PNG, broken and oversized headers, metadata removal, same picture as PNG and DIB, thumbnails, the bitmap written back | everywhere |
| Store | real SQLite: dedupe, retention + size and image budgets, pins, undo, images, the schema 1 → 2 migration, recovery, exclusive lock, ordering | everywhere |
| Privacy | log never contains content, deleted/memory-only text and pictures never on disk, no networking outside the updater, no unexpected native calls | everywhere |
| Update | SemVer order, asset choice, `SHA256SUMS.txt` and release JSON parsing, downloads against a fake GitHub (hash/size mismatch, foreign redirects, rate limit, offline), EXE swap and rollback on temp files | everywhere |
| Windows | autostart against an isolated registry subtree, popup window behavior (cloaked) | everywhere |
| Integration | the real clipboard: Unicode round trip, own writes, all privacy markers, locked clipboard, size limit; pictures: capture, `"PNG"` before `CF_DIB`, text wins, images off, exclusion and markers, oversized headers, write → read round trip | CI (`CM_INTEGRATION=1`) |

The published EXE is additionally run with `--selftest` in CI on x64 and ARM64; it includes the image
codec (a 4K screenshot), and on x64 an image round trip through the real clipboard.

## Decision log

| # | Decision | Why | Alternatives considered |
|---|---|---|---|
| 1 | **.NET 10 LTS + WPF** with the Fluent theme | only option combining a true single EXE, mature UI Automation accessibility, live light/dark/system theming and zero UI dependencies; supported until Nov 2028 | WinUI 3 (extracts its whole payload, servicing cadence), Avalonia (native Skia DLLs, accessibility gaps), WinForms (no live dark mode, no size win), plain Win32 (accessibility effort) |
| 2 | **Software rendering** (`RenderMode.SoftwareOnly`) | the hardware D3D path cost ~90 MB private memory for this small UI; software rendering keeps the app at ~35 MB and opens just as fast (measured) | hardware rendering |
| 3 | **Uncompressed single-file EXE** | compressed bundles are inflated into private memory: +70 MB measured for an always-running app; a zip asset covers download size | `EnableCompressionInSingleFile` |
| 4 | **Windows' `winsqlite3.dll`** via SQLitePCLRaw | nothing unsigned to extract (Smart App Control, AV heuristics), Microsoft-signed, loaded explicitly from System32 | bundled `e_sqlite3.dll` |
| 5 | **SQLite** instead of a JSON file | atomic per-change commits, secure delete, migrations, no full rewrites | JSON + atomic rename |
| 6 | **Autostart via HKCU Run**, `StartupApproved` only read (own value cleared on explicit re-enable) | visible and switchable in Task Manager; no admin; Task Scheduler entries are hidden from users | Startup folder shortcut, Task Scheduler |
| 7 | **No `RegisterApplicationRestart`** | needs WER consent and 60 s uptime; Windows' "restart apps after sign-in" could start the app although autostart is off | restart registration |
| 8 | **Native tray menu** | the rescue path when the hotkey is taken: correct DPI, keyboard and screen-reader behavior; light-only in dark mode is accepted | WPF ContextMenu |
| 9 | **Fixed popup position** (upper third of the active monitor) | predictable for muscle memory; caret positions are unavailable in Chromium/Electron/Terminal | caret- or mouse-anchored |
| 10 | **Always merge duplicates** (no setting) | predictable "moves to the top" behavior; identical rows would make selection ambiguous | optional setting |
| 11 | **Memory-only history** instead of "clear on exit" | unpinned entries never reach the disk, no work during shutdown, covers crashes | clear at exit |
| 12 | **GPL-3.0-or-later** | keeps derivatives open and credits intact; the bundled .NET runtime and WPF native libraries are treated as System Libraries of the runtime (GPLv3 §1) and their notices ship with every release | MIT, Apache-2.0 |
| 13 | **Copy, not auto-paste**, in v1 | predictable; focus is returned to the previous app so Ctrl+V lands right; `Shift+Enter` is reserved for a later "copy and paste" | auto-paste via `SendInput` |
| 14 | **Exit on WM_QUERYENDSESSION** | WPF calls `Shutdown()` when `SessionEnding` is not cancelled, and cancelling would block logoff (or get the background app killed). Every capture is already committed, so the app closes its services in a bounded time there; if the user aborts the shutdown, the app starts again at the next sign-in | wait for WM_ENDSESSION |
| 15 | **No initial capture at startup** | content copied while the app was not running (possibly during a pause or an "ignore next copy" of an earlier run) is not recorded | read the clipboard once at start |
| 16 | **Updates only on an explicit click, swapped in place** | the privacy promise allows network access only when the user asks for it, and then only to GitHub; one EXE without an installer updates without admin rights: the verified download installs itself once the running version has exited – it moves the previous EXE to `*.old` and copies itself to the same path, so autostart entry and shortcuts stay valid; settings and history live in `%LOCALAPPDATA%`. Renaming the running EXE instead would leave a single-file process reading assemblies from the wrong file. `--finish-update <pid>` and `--updated-from <pid>` are a contract every later version must keep, or an update would leave no instance running | automatic or periodic checks, a separate updater EXE, MSIX/winget, an elevated installer |
| 17 | **Quiet autostart** (below-normal priority, optional work and notices deferred) | sign-in is when every startup app competes for CPU and disk; in the first minute only capture and the hotkey matter, and a toast or window at login is noise | Task Scheduler with a start delay (hidden from users), a fixed sleep before starting (hotkey and capture would be missing), full start at normal priority |
| 18 | **Pictures as normalized PNG**; text wins when a copy has both; `"PNG"` + 24-bit `CF_DIB` on white when copying back | one lossless, compact format whatever the source offered (PNG, bitmap, synthesized from GDI), so duplicates match and no metadata of the source survives; spreadsheet and document copies keep behaving as before; PNG keeps transparency for modern apps, the DIB on white looks right in Paint and older programs, and Windows synthesizes the other bitmap formats from it | storing the source bytes as they came (keeps metadata, bitmaps are ~4× larger, duplicates by source), images over text, writing `CF_DIBV5` (stops Windows from synthesizing), delayed rendering on paste (exit handling) |
