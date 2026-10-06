<#
.SYNOPSIS
  Fails if a built binary still contains the build machine's paths (repository location or user
  profile). PathMap/ContinuousIntegrationBuild should have replaced them with /_/.
#>
param([Parameter(Mandatory)][string[]] $Path)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$needles = @($root, $env:USERPROFILE) | Where-Object { $_ } | Select-Object -Unique
$latin1 = [System.Text.Encoding]::GetEncoding(28591) # maps every byte to one char, so string search == byte search
$found = $false
foreach ($file in $Path) {
    $haystack = $latin1.GetString([System.IO.File]::ReadAllBytes((Resolve-Path $file)))
    foreach ($needle in $needles) {
        foreach ($encoding in [System.Text.Encoding]::UTF8, [System.Text.Encoding]::Unicode) {
            $pattern = $latin1.GetString($encoding.GetBytes($needle))
            if ($haystack.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                Write-Host "::error::$file contains a build-machine path ($($encoding.WebName))"
                $found = $true
            }
        }
    }
}

if ($found) { exit 1 }
Write-Host "No build-machine paths found."
