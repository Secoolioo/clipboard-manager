# Privacy

Clipboard Manager processes everything you copy. This page lists exactly what it stores, where, and
what it can and cannot protect.

## What is stored

| File | Content |
|---|---|
| `%LOCALAPPDATA%\Secoolioo\ClipboardManager\history.db` (+ `-wal`) | your text history and pins (SQLite) |
| `…\settings.json` | settings (no clipboard content) |
| `…\logs\app.log`, `app.1.log` | technical warnings and errors only – never clipboard content, previews, hashes, window titles or exception messages; at most 2 MB |
| `…\session.active` | empty marker used to detect an unclean shutdown |
| `…\history.corrupt.db` | only after a damaged database was recovered; deleted by *Clear history* |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `Secoolioo.ClipboardManager` | autostart command |
| `HKCU\Software\Secoolioo\ClipboardManager` | "first run done" and your autostart decision |
| Start menu → `Clipboard Manager.lnk` | optional shortcut |
| `%TEMP%\.net\ClipboardManager\…` | Microsoft's WPF runtime DLLs, extracted by the single-file EXE; old versions are cleaned up |

*Settings → About → Remove everything* deletes all of the above except the extracted runtime DLLs
of the running version, then exits.

## What is never stored

- Content marked by the source app as "do not record": `ExcludeClipboardContentFromMonitorProcessing`
  (any data), `CanIncludeInClipboardHistory` = 0, or `Clipboard Viewer Ignore`. This is the same
  rule Windows' own clipboard history follows, and the app does not even read the text of such
  content. KeePass (2.44+), KeePassXC, Bitwarden, Proton Pass and Chrome/Edge password fields set
  these markers; other apps may not.
- `CanUploadToCloudClipboard = 0` alone is *not* treated as private – it only forbids cloud sync and
  is set, for example, on content coming from Remote Desktop.
- Copies while recording is paused, and the next copy after *Ignore next copy*. While paused the
  clipboard is not even opened.
- Copies from excluded apps (best effort, see below).
- With *Don't save detected credentials* (on by default): texts containing private-key blocks or
  tokens with unambiguous formats (GitHub, AWS access key IDs, Slack, Stripe live keys, GitLab).
- Text larger than 256K characters, images, files.
- Whatever is already on the clipboard when the app starts: only copies made while it runs are recorded.
- With *Keep history in memory only*: unpinned entries never reach the disk while the mode is on.
  When you turn it off, the app asks whether the entries collected in memory should be saved or
  discarded.

## Guarantees checked by automated tests

- No networking library and no unexpected native library is referenced by the app's code.
- A canary string never appears in the log, also not through exception messages.
- After deleting an entry or clearing the history, its text is no longer present in any file in the
  data folder (SQLite `secure_delete` plus WAL truncation) or in SQLite temp files.
- In memory-only mode the text never appears in the data folder, including when switching the
  mode on, and when switching it off with "discard".
- Lowering the maximum number of entries scrubs the removed texts like a manual delete.
- Our own writes, an emptied clipboard and repeated notifications for one copy never use up a
  pending *Ignore next copy*; a skipped copy is never read later by a pending read.

## Limits – please read

- **Not encrypted at rest.** The database is protected by Windows file permissions, like other app
  data. Any software running under your Windows account can read it – and could read the clipboard
  directly anyway. Use BitLocker / device encryption against offline access.
- **Physical remnants.** Overwriting a file does not guarantee that old copies vanish from SSD
  cells, Volume Shadow Copies, backups, `pagefile.sys` or `hiberfil.sys`.
- **Source detection is best effort.** Windows does not always say which app put something on the
  clipboard (e.g. some password managers clear the owner; browser extensions copy through the
  browser). If the owner is unknown, the foreground app is assumed. For a single sensitive copy, use
  *Ignore next copy*.
- **Passwords cannot be recognized.** Only unambiguous token formats are detected; there is
  deliberately no "looks random" heuristic that would silently drop legitimate text.
- **Crash dumps.** The app tells Windows Error Reporting not to include memory contents. Company
  policies that collect full dumps (`LocalDumps`) or a dump you create yourself in Task Manager can
  still contain history – never attach dumps to public issues.
- **Screen sharing.** The history window asks Windows to exclude it from screen capture (Windows 10
  2004+); older systems show a black box instead. Tools that capture differently may still see it.
- **Windows itself** (SmartScreen, Defender) may contact Microsoft about the EXE file. The app
  makes no network connections; you can verify this with an outbound firewall rule.
