<#
.SYNOPSIS
  Builds the release files: single-file EXEs for x64 and ARM64, license files, third-party
  notices (including the .NET runtime's own notices), a zip and SHA256SUMS.txt.
  Used by .github/workflows/release.yml and for local dry runs.
#>
param(
    [Parameter(Mandatory)][string] $Version,
    [string] $Output = "artifacts/release"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' is not SemVer" }
    if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }
    New-Item -ItemType Directory -Force $Output | Out-Null

    foreach ($rid in 'win-x64', 'win-arm64') {
        $dir = "artifacts/publish/$rid"
        dotnet publish src/ClipboardManager -c Release -r $rid -o $dir "-p:Version=$Version" -p:ContinuousIntegrationBuild=true
        if ($LASTEXITCODE -ne 0) { throw "publish $rid failed" }
        $name = if ($rid -eq 'win-x64') { 'ClipboardManager.exe' } else { 'ClipboardManager-arm64.exe' }
        Copy-Item "$dir/ClipboardManager.exe" "$Output/$name"
    }

    Copy-Item LICENSE "$Output/LICENSE.txt"

    # Our notices plus the runtime pack's notices (it bundles further third-party components).
    $packages = Join-Path $env:USERPROFILE '.nuget/packages'
    $sources = @(
        Get-ChildItem "$packages/microsoft.netcore.app.runtime.win-x64" -Recurse -Include 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT' -ErrorAction SilentlyContinue
        Get-ChildItem "$packages/microsoft.windowsdesktop.app.runtime.win-x64" -Recurse -Include 'LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT' -ErrorAction SilentlyContinue
        Get-ChildItem "$packages/sqlitepclraw.core" -Recurse -Include 'LICENSE.TXT', 'NOTICE.TXT' -ErrorAction SilentlyContinue
    ) | Where-Object { $_ }
    if (-not $sources) { throw "Runtime pack license files not found; was the publish restored into $packages?" }
    $notices = [System.Text.StringBuilder]::new()
    [void]$notices.AppendLine((Get-Content THIRD-PARTY-NOTICES.md -Raw -Encoding UTF8))
    foreach ($file in $sources) {
        [void]$notices.AppendLine("`n`n==== $($file.Directory.Parent.Name) $($file.Directory.Name) / $($file.Name) ====`n")
        [void]$notices.AppendLine((Get-Content $file.FullName -Raw -Encoding UTF8))
    }
    [System.IO.File]::WriteAllText("$root/$Output/THIRD-PARTY-NOTICES.txt", $notices.ToString(), [System.Text.UTF8Encoding]::new($false))

    Compress-Archive -Path "$Output/ClipboardManager.exe", "$Output/LICENSE.txt", "$Output/THIRD-PARTY-NOTICES.txt" -DestinationPath "$Output/ClipboardManager-x64.zip" -CompressionLevel Optimal

    $sums = Get-ChildItem $Output -File | Sort-Object Name | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    [System.IO.File]::WriteAllLines("$root/$Output/SHA256SUMS.txt", $sums, [System.Text.UTF8Encoding]::new($false))

    # Release notes: the CHANGELOG section of this version.
    $changelog = Get-Content CHANGELOG.md -Raw -Encoding UTF8
    $pattern = "(?ms)^## \[$([regex]::Escape($Version))\][^\n]*\n(.*?)(?=^## \[|\z)"
    $match = [regex]::Match($changelog, $pattern)
    $notes = if ($match.Success) { $match.Groups[1].Value.Trim() } else { "See CHANGELOG.md." }
    $base = "https://github.com/Secoolioo/clipboard-manager/releases/download/v$Version"
    $notes += @"


### Download

| File | For |
|---|---|
| [**ClipboardManager.exe**]($base/ClipboardManager.exe) | Windows 10 / 11, x64 – just run it, no installer, no admin rights |
| [ClipboardManager-arm64.exe]($base/ClipboardManager-arm64.exe) | Windows 11 on ARM |
| [ClipboardManager-x64.zip]($base/ClipboardManager-x64.zip) | the x64 EXE plus license files, smaller download |

Already installed? *Settings → About → Check for updates* installs new versions in place (from
0.10.0 on); settings and history are kept.

> [!NOTE]
> The EXE is not code-signed yet, so Windows SmartScreen may show *Windows protected your PC*:
> click *More info → Run anyway*. Verify the download with ``SHA256SUMS.txt`` or
> ``gh attestation verify ClipboardManager.exe -R Secoolioo/clipboard-manager``.

Clipboard Manager is free and stays free. If it saves you time and you'd like to support further
development, a small [Solana donation](https://github.com/Secoolioo/.github/blob/main/DONATE.md) is
welcome – entirely optional; a ⭐, a bug report or telling a friend helps just as much.
"@
    [System.IO.File]::WriteAllText("$root/artifacts/release-notes.md", $notes + "`n", [System.Text.UTF8Encoding]::new($false))

    Get-ChildItem $Output | ForEach-Object { "{0,-32} {1,10:N1} MB" -f $_.Name, ($_.Length / 1MB) }
}
finally {
    Pop-Location
}
