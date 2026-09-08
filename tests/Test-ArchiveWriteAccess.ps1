[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
# Use the exact access/sharing flags of experimental overwrite mode, but NEVER write,
# truncate, create, rename or replace anything. Run after stopping emulation.
$archiveFile = (Resolve-Path -LiteralPath $ArchivePath).Path
$before = (Get-FileHash -LiteralPath $archiveFile -Algorithm SHA256).Hash
$probe = $null
try {
    $probe = [IO.File]::Open($archiveFile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
    Write-Output 'PASS: an overwrite-compatible handle can be opened. No bytes were written.'
}
finally {
    if ($null -ne $probe) { $probe.Dispose() }
    $after = (Get-FileHash -LiteralPath $archiveFile -Algorithm SHA256).Hash
    if ($before -ne $after) { throw 'Archive contents changed during the probe (this script does not write them).' }
    Write-Output "Verified unchanged SHA256: $after"
}
