# Release process

1. Update `CHANGELOG.md`: move *Unreleased* entries under a new `## [x.y.z] - YYYY-MM-DD` heading
   and update the compare links. Update the version on the website as well: the download button
   and `softwareVersion` in [`site/index.html`](../site/index.html).
2. Run the manual checks in [manual-test-checklist.md](manual-test-checklist.md) on the CI artifact
   of the release commit.
3. Tag the commit on `main` and push the tag:

   ```bash
   git tag -a v1.2.3 -m "v1.2.3"
   git push origin v1.2.3
   ```

4. The [release workflow](../.github/workflows/release.yml) then
   - verifies the tag is SemVer and points to a commit on `main`,
   - restores with locked dependencies, builds and runs all tests (including real-clipboard tests),
   - publishes `ClipboardManager.exe` (x64) and `ClipboardManager-arm64.exe`, self-tests both
     (ARM64 on a native ARM runner) and checks them for build-machine paths,
   - writes `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` (including the .NET runtime notices),
     `ClipboardManager-x64.zip` and `SHA256SUMS.txt`,
   - creates a build provenance attestation and the GitHub release, with the notes taken from the
     changelog section of the version. Tags with a suffix (`v1.2.3-rc.1`) become pre-releases.

   Only the final job has write permissions, and it runs no repository code.

5. Optional: scan the EXE on VirusTotal; if Microsoft Defender flags it, submit it as a software
   developer at <https://www.microsoft.com/wdsi/filesubmission>.

The in-app updater reads GitHub's *latest* release (never pre-releases) and relies on the asset
names `ClipboardManager.exe`, `ClipboardManager-arm64.exe` and `SHA256SUMS.txt`; keep them stable.

## Dry run

`build/publish-release.ps1 -Version 0.0.0-dryrun` produces the same files locally under
`artifacts/release`.

Builds are deterministic (pinned SDK, locked packages, `Deterministic` + `PathMap`): two consecutive
local release builds of the same commit produced byte-identical `ClipboardManager.exe` files. This
is measured, not guaranteed – a future code-signing step makes signed files differ by their
signature.

## Signing

Releases are not signed yet. The plan is the SignPath Foundation program (free for open source); a
signing job will slot in between *build* and *publish*. See [CODE_SIGNING_POLICY.md](../CODE_SIGNING_POLICY.md).
