# Security policy

Clipboard Manager handles whatever you copy – including secrets – so security reports are taken
seriously.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's private
[**Report a vulnerability**](https://github.com/Secoolioo/clipboard-manager/security/advisories/new)
form instead. You can expect a first answer within a week.

Helpful information: affected version, Windows version, steps to reproduce and the impact you see.
Never include real clipboard content, passwords or personal data – use made-up examples.

## Supported versions

Only the latest release receives security fixes.

## Scope

In scope, for example:

- clipboard content leaving the machine or ending up in logs, crash data or temporary files
- content from password managers or other marked sources being stored
- deleted entries remaining readable in the app's data files
- DLL planting or other ways to run code through the app
- the in-app updater contacting anything but GitHub, sending data about the user or the clipboard,
  or installing a file that is not the verified release EXE

Out of scope: attacks that already require running code as the same Windows user (such software can
read the clipboard and the app's data directly), and physical access to an unencrypted disk.

## Verifying releases

Every release EXE comes with a SHA-256 hash (`SHA256SUMS.txt`) and a GitHub build provenance
attestation:

```bash
gh attestation verify ClipboardManager.exe --repo Secoolioo/clipboard-manager
```

The in-app updater (*Settings → About → Check for updates*, never automatic) downloads only over
HTTPS from GitHub and installs the EXE only if its size and SHA-256 match the release's
`SHA256SUMS.txt`.
