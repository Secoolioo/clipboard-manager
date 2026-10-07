# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.10.2] - 2026-10-07

Faster search while typing and more resilience, after a 2,000-cycle stress test of the popup.

### Changed

- Typing in the search box is several times faster: the list is updated in place and keeps its
  rows instead of rebuilding all of them (150 entries: from 80–120 ms to 12–35 ms per keystroke).
- The compact popup shows only the copy, preview and close hints, so the hint bar stays on one line.

### Fixed

- If the popup keeps failing, it pauses for a few minutes with one notice and then tries again,
  instead of staying unavailable until a restart; errors in other windows no longer affect it.
- A blocked autostart entry (security software, policy) no longer stops the app from starting.
- Opening the data folder no longer fails when Explorer is blocked.

## [0.10.1] - 2026-10-07

More stability: the app keeps running even if part of its window ever fails again.

### Fixed

- Copying an image no longer crashes the app (#6, same cause as #1 and #2, fixed since 0.10.0).
  Images stay untouched on the clipboard and can be pasted as usual; the history keeps text only
  and shows "Last copy not saved – not text".
- A popup whose content fails is now closed and replaced immediately instead of failing again on
  every layout pass, and a burst of identical errors counts once, so one broken state can no
  longer end the app.
- Refreshing an open popup after a new capture is protected like opening and closing.
- For images, files and other non-text content the clipboard is released right after the format
  check, before any other work.

## [0.10.0] - 2026-10-07

Crash fixes from the first testers, a built-in update button and a quieter start with Windows.

### Added

- In-app update check: *Settings → About → Check for updates* (also *Check for updates…* in the tray
  menu). Runs only on an explicit click and contacts only GitHub; *Install update* downloads the
  EXE for this PC, verifies its size and SHA-256 against the release's `SHA256SUMS.txt`, replaces
  the EXE in place and restarts. Settings, history and autostart are kept. Folders that need
  administrator rights get a link to the releases page instead.

### Changed

- The privacy guarantee is now "no network connections unless you click *Check for updates*"; a
  test keeps networking code confined to the updater.
- The About section shows the version without the build commit.
- Quiet start at sign-in: when Windows starts the app, it shows no window or notification and
  never takes the focus. Clipboard capture and the shortcut work immediately; for 45 seconds (or
  until the app is first opened) it runs at below-normal priority and defers popup pre-warming and
  cleanup. Notices from that phase appear when the history or the tray menu is first opened, at
  the latest after those 45 seconds.
- Windows lists the app as "Clipboard Manager" (instead of "ClipboardManager") in Startup apps,
  Task Manager and on notifications.
- Settings: a link next to "Start with Windows" opens the Windows startup-apps page, where the same
  entry can be switched off.

### Fixed

- The app no longer crashes when the history popup is opened and closed quickly, or after Windows
  was locked and unlocked (#1, #2). A hint row in the popup (empty history, skipped copy, no
  results, loading) used a theme color that the Fluent theme only defines as an alias; every time
  the row was built it threw, and repeated errors ended the app.
- A popup that fails is now replaced instead of taking the app down, unlock, resume and display
  changes warm the popup up only once, and the popup always hides even if closing fails.
- The error message after a crash now names the log file and offers to open its folder, and is no
  longer the "could not start" text when the app had been running.

## [0.9.0] - 2026-10-06

First public release.

### Added

- Text clipboard history that survives restarts (100 entries by default, up to 5,000; pins never
  expire).
- Global shortcut <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>V</kbd> (changeable; automatic fallback to
  <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>V</kbd> when taken) opening a pre-built, keyboard-first popup
  with instant search, preview pane, pins, delete with undo and focus return.
- Duplicates move to the top instead of adding rows.
- Notification-area icon with pause (5/30/60 minutes or until resumed), "ignore next copy", clear
  history, settings and exit.
- Respect for the Windows "do not record" clipboard markers used by password managers and private
  browser windows; excluded apps; optional skipping of detected credentials; memory-only history;
  hiding from screen capture.
- Autostart via the per-user Run entry (also manageable in Task Manager), Start menu entry, welcome
  window, light/dark/system theme, English and German.
- Single self-contained EXE for x64 and ARM64, self-test mode, SHA-256 hashes and build provenance
  attestations for releases.

[Unreleased]: https://github.com/Secoolioo/clipboard-manager/compare/v0.10.2...HEAD
[0.10.2]: https://github.com/Secoolioo/clipboard-manager/compare/v0.10.1...v0.10.2
[0.10.1]: https://github.com/Secoolioo/clipboard-manager/compare/v0.10.0...v0.10.1
[0.10.0]: https://github.com/Secoolioo/clipboard-manager/compare/v0.9.0...v0.10.0
[0.9.0]: https://github.com/Secoolioo/clipboard-manager/releases/tag/v0.9.0
