# Code signing policy

Release binaries are planned to be signed through the
[SignPath Foundation](https://signpath.org/) open-source program. Until then, releases are
unsigned; their integrity can be checked with `SHA256SUMS.txt` and the GitHub build provenance
attestation (`gh attestation verify`).

## What gets signed

Only binaries built from this repository by its GitHub Actions release workflow on GitHub-hosted
runners: `ClipboardManager.exe` (x64) and `ClipboardManager-arm64.exe`. Third-party components
bundled inside the single-file EXE keep their original signatures (the .NET and WPF native
libraries are signed by Microsoft); SQLite is used from Windows (`winsqlite3.dll`) and is not shipped.

## Roles

| Role | Who |
|---|---|
| Author / committer | [Secoolioo](https://github.com/Secoolioo) and contributors via reviewed pull requests |
| Reviewer | [Secoolioo](https://github.com/Secoolioo) |
| Approver (release signing) | [Secoolioo](https://github.com/Secoolioo) |

All maintainer accounts use multi-factor authentication. Every release is approved manually.

## Privacy

Clipboard Manager does not connect to any network service and collects no data. See
[docs/privacy.md](docs/privacy.md).

## Licensing

The project is licensed under GPL-3.0-or-later. The bundled .NET runtime and WPF native libraries
are treated as System Libraries of the runtime (GPLv3 §1); see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the decision log in
[docs/architecture.md](docs/architecture.md#decision-log).
