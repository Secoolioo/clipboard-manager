# Contributing

Thanks for helping to make Clipboard Manager better! This project values a small, fast and
trustworthy tool over a long feature list, so please read the principles before starting larger work.

## Principles

- **Performance and reliability first.** No polling, no periodic timers while idle, no work on the
  UI thread that could block. Measure before and after (see [docs/performance.md](docs/performance.md)).
- **Privacy is a hard requirement.** No network code, no telemetry, never log clipboard content
  (not even in exception messages), and keep the guarantees in [docs/privacy.md](docs/privacy.md) true.
- **Keep it simple.** Two production projects, no MVVM/DI frameworks, dependencies only when they
  bring a real benefit. Discuss new dependencies in an issue first.
- **Honest UX.** Never promise what Windows cannot guarantee.

## Getting started

```bash
git clone https://github.com/Secoolioo/clipboard-manager.git
cd clipboard-manager
dotnet test --solution ClipboardManager.sln
```

The SDK version is pinned in [`global.json`](global.json). Tests that touch the real Windows
clipboard are skipped locally unless you set `CM_INTEGRATION=1` – they would overwrite your
clipboard. CI always runs them.

Useful commands:

| Task | Command |
|---|---|
| Format code | `dotnet format ClipboardManager.sln` |
| Publish a single-file EXE | `dotnet publish src/ClipboardManager -c Release -r win-x64 -o publish` |
| Self-test a published EXE (invisible) | `pwsh build/selftest.ps1 -Exe publish/ClipboardManager.exe` |
| Regenerate README images and icon | `dotnet run --project tools/AssetGen -- all` |
| Measure search latency | `dotnet run --project tools/AssetGen -c Release -- bench` |
| Use a separate data folder | set `CLIPBOARDMANAGER_DATA_DIR` |
| Verbose log | set `CLIPBOARDMANAGER_LOG=debug` |

## Updating the .NET SDK

Bump `global.json` and regenerate the lock files in the same commit (the SDK version also
changes an implicit package that the lock files record):

```bash
dotnet restore ClipboardManager.sln --force-evaluate
```

## Pull requests

- One topic per PR; include tests for behavior changes (see the test categories in
  [docs/architecture.md](docs/architecture.md#tests)).
- `dotnet build` must be warning-free (warnings are errors) and `dotnet format --verify-no-changes`
  must pass.
- UI changes: attach a screenshot (use demo data, never real clipboard content) and run through the
  relevant part of [docs/manual-test-checklist.md](docs/manual-test-checklist.md).
- Update [CHANGELOG.md](CHANGELOG.md) under *Unreleased*.

## Translations

UI text lives in `src/ClipboardManager/Localization/Strings*.cs` with German and English side by side.
If you would like to add a language, open an issue first – a third language is the point where the
project should move to resource files.

## License of contributions

By contributing you agree that your contribution is licensed under the
[GNU GPL v3.0 or later](LICENSE), the license of this project.
