# Manual test checklist

Run before a release on the CI artifact. Use demo text, never real secrets.

## First run and autostart

- [ ] Double-click the EXE from a permanent folder → welcome window, tray icon appears (maybe in the ^ overflow).
- [ ] Task Manager → Startup apps and Settings → Apps → Startup list *Clipboard Manager* (publisher Secoolioo).
- [ ] Sign out and in → no window, no notification, focus stays where it was; the shortcut and capture work right away; Task Manager → Details shows priority *Below normal* for ~45 s, then *Normal* (immediately *Normal* once the history is opened).
- [ ] Disable it in Task Manager → Settings shows "disabled in Windows"; after sign-in the app does not start.
- [ ] Re-enable in Settings → enabled in Task Manager again; "Manage startup apps in Windows" opens Settings → Apps → Startup.
- [ ] Replace the EXE with a newer build at the same path → the startup entry and its on/off state stay unchanged.
- [ ] Run the EXE from inside a ZIP → no autostart entry, hint shown.
- [ ] Move the EXE and start it manually → the autostart entry follows.
- [ ] Start a second time → the running instance opens its history; no second tray icon.

## Capture and history

- [ ] Copy text in Notepad, a browser, VS Code, Windows Terminal, Excel → each appears once, newest on top.
- [ ] Copy the same text again → moves to the top, no duplicate row.
- [ ] Copy an image or files → no entry; the popup shows "not text".
- [ ] KeePass / KeePassXC / Bitwarden / Proton Pass / 1Password / Chrome password field / Edge InPrivate: copy a password → not stored (note results per app in the PR).
- [ ] Pause 5 minutes → copies are not stored, banner and icon show the pause, recording resumes on time (also after sleep).
- [ ] Ignore next copy → exactly one copy is skipped.
- [ ] Copy a GitHub-token-shaped demo string → skipped with "looks like a credential".
- [ ] Reboot → history still there.

## Popup

- [ ] Ctrl+Shift+V opens instantly on the monitor of the active window; typing filters; Enter copies and focus returns; Ctrl+V pastes in the original app.
- [ ] Esc / hotkey again closes and returns focus; clicking elsewhere closes without stealing focus back.
- [ ] Open from the tray icon; clicking the tray icon again does not flicker.
- [ ] Ctrl+P pins / unpins; Shift+Del deletes; Ctrl+Z restores; Tab toggles the preview.
- [ ] Alt+F4 in the popup → the next hotkey still works.
- [ ] Teams/Zoom screen share → the popup is not visible to others.

## Shell integration

- [ ] Hotkey taken by another app → fallback Win+Alt+V announced, or "no hotkey" shown everywhere.
- [ ] Change the hotkey in Settings; Ctrl+C is rejected; the recorder sees Ctrl+Shift+V.
- [ ] Restart Explorer (`taskkill /f /im explorer.exe`, then start it) → tray icon comes back.
- [ ] Switch Windows light/dark → app theme and tray icon follow.
- [ ] Lock/unlock, RDP connect, sleep/resume → popup still opens instantly.

## Updating

- [ ] Settings → About → *Check for updates* with the latest release → "up to date"; with the network off → "Could not reach GitHub" and a releases-page button.
- [ ] Previous release EXE in `%LOCALAPPDATA%\Programs\ClipboardManager` → *Install update* → progress, restart, one "Updated to vX" notification; settings, history, pins, hotkey and autostart entry unchanged; `ClipboardManager.exe.old` gone after the restart.
- [ ] Same in a folder that needs admin rights (e.g. `C:\Program Files\ClipboardManager`) → "not writable" message with a releases-page button, no UAC prompt.
- [ ] Tray → *Check for updates…* opens Settings at the About section and checks; Narrator announces each state.

## Accessibility and display

- [ ] Narrator: opening announces the window; arrow keys read each entry (text, pinned, current, time).
- [ ] All four High Contrast themes are readable.
- [ ] Settings → Accessibility → Text size 150 % → popup scales, nothing clipped.
- [ ] Two monitors with different scaling → popup is sharp and correctly sized on both.

## Endurance (before 1.0)

- [ ] 100,000 captures (script copying demo text), 5,000 popup cycles, 200 settings open/close, 50 Explorer restarts, then force a GC (`--selftest` numbers for comparison): private bytes grow < 5 MB, handle/GDI/USER counts stable.
- [ ] 72 h idle: CPU time stays negligible.
- [ ] Hybrid-CPU laptop on battery: popup still opens instantly.
