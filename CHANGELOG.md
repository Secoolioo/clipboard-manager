# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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
