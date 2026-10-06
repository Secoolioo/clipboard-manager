<#
.SYNOPSIS
  Guards the maintainer's privacy: every commit and tag by the maintainer account must use the
  GitHub noreply address. Contributors may use whatever address they chose to publish.
  An optional local, untracked list of forbidden patterns (.git/forbidden-identities, one regex per
  line) is checked too; it is used by the pre-push hook and never committed.
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

$tags = git for-each-ref refs/tags --format='%(refname:short)%09%(taggername)%09%(taggeremail)'
foreach ($tag in $tags | Where-Object { $_ }) {
    $name, $tagger, $email = $tag -split "`t"
    if ($tagger -eq $maintainerName -and $email.Trim('<', '>') -ne $maintainerEmail) { $problems += "tag $name has a non-noreply tagger email" }
}

$private = Join-Path (git rev-parse --git-dir) 'forbidden-identities'
if (Test-Path $private) {
    $patterns = Get-Content $private | Where-Object { $_ -and -not $_.StartsWith('#') }
    $everything = (git log --all --format='%an %ae %cn %ce %B') + $tags
    foreach ($pattern in $patterns) {
        if ($everything -match $pattern) { $problems += "history matches a forbidden identity pattern" }
    }
}

if ($problems) {
    $problems | ForEach-Object { Write-Host "::error::$_" }
    exit 1
}

Write-Host "Commit identities are clean."
