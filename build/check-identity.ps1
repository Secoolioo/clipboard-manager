<#
.SYNOPSIS
  Guards the maintainer's privacy in the public history.
  1. Every commit and tag by the maintainer account must use the GitHub noreply address.
  2. No author, committer, tagger or message may match a private list of forbidden identities
     (one regex per line). The list never lives in the repository: locally it is
     .git/forbidden-identities (used by the pre-push hook), in CI the FORBIDDEN_IDENTITIES secret.
  Contributors may use whatever address they chose to publish.
#>
$ErrorActionPreference = 'Stop'
$maintainerName = 'Secoolioo'
$maintainerEmail = '149840727+Secoolioo@users.noreply.github.com'
$problems = @()

$lines = git log --all --format='%h%x09%an%x09%ae%x09%cn%x09%ce'
foreach ($line in $lines) {
    $hash, $authorName, $authorEmail, $committerName, $committerEmail = $line -split "`t"
    if ($authorName -eq $maintainerName -and $authorEmail -ne $maintainerEmail) { $problems += "$hash author email is not the noreply address" }
    if ($committerName -eq $maintainerName -and $committerEmail -ne $maintainerEmail) { $problems += "$hash committer email is not the noreply address" }
}

$tags = @(git for-each-ref refs/tags --format='%(refname:short)%09%(taggername)%09%(taggeremail)' | Where-Object { $_ })
foreach ($tag in $tags) {
    $name, $tagger, $email = $tag -split "`t"
    if ($tagger -eq $maintainerName -and $email.Trim('<', '>') -ne $maintainerEmail) { $problems += "tag $name has a non-noreply tagger email" }
}

# Shared .git directory, also from a linked worktree.
$private = Join-Path (git rev-parse --git-common-dir) 'forbidden-identities'
$patterns = @()
if (Test-Path $private) { $patterns += Get-Content $private }
if ($env:FORBIDDEN_IDENTITIES) { $patterns += $env:FORBIDDEN_IDENTITIES -split "`r?`n" }
$patterns = @($patterns | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })

if ($patterns.Count -gt 0) {
    $everything = @(git log --all --format='%an %ae %cn %ce %B') + $tags
    foreach ($pattern in $patterns) {
        if (@($everything | Where-Object { $_ -match $pattern }).Count -gt 0) { $problems += "history matches a forbidden identity pattern" }
    }
}
else {
    Write-Host "No private identity patterns configured; only the noreply rule was checked."
}

if ($problems) {
    $problems | Select-Object -Unique | ForEach-Object { Write-Host "::error::$_" }
    exit 1
}

Write-Host "Commit identities are clean."
