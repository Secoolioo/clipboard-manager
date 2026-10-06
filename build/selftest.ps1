<#
.SYNOPSIS
  Runs a published EXE with --selftest (invisible: temp data folder, cloaked window) and fails on
  any failed check. -Clipboard also round-trips the real clipboard (CI only).
#>
param(
    [Parameter(Mandatory)][string] $Exe,
    [switch] $Clipboard
)

$ErrorActionPreference = 'Stop'
$report = Join-Path ([System.IO.Path]::GetTempPath()) "cm-selftest-$PID.txt"
$env:CLIPBOARDMANAGER_SELFTEST_OUT = $report
$arguments = if ($Clipboard) { '--selftest-clipboard' } else { '--selftest' }
$process = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru
if (-not $process.WaitForExit(180000)) {
    $process.Kill()
    throw "Self-test timed out"
}

if (Test-Path $report) { Get-Content $report }
if ($process.ExitCode -ne 0) { throw "Self-test failed with exit code $($process.ExitCode)" }
