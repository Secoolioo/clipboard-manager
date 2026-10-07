# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/Secoolioo/clipboard-manager/compare/v0.9.0...HEAD
[0.9.0]: https://github.com/Secoolioo/clipboard-manager/releases/tag/v0.9.0
