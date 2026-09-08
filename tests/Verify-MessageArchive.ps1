[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [string]$StageName = 'Special2WorldHomeStage'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$editorDirectory = Join-Path $repoRoot 'artifacts\Debug-Moonlight\bin\Spotlight'
$archiveFile = (Resolve-Path -LiteralPath $ArchivePath).Path
# Only the isolated build receives a native DLL; the provided archive is read-only throughout.
$architecture = if ([IntPtr]::Size -eq 8) { 'x64' } else { 'x86' }
Copy-Item -LiteralPath (Join-Path $editorDirectory "nativelib\$architecture\yaz0.dll") -Destination (Join-Path $editorDirectory 'nativelib\yaz0.dll')
foreach ($dll in @('Syroot.BinaryData.dll', 'FileFormats3DW.dll', 'SARCExt.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $editorDirectory "lib\$dll"))
}
$previousDirectory = [Environment]::CurrentDirectory
try {
    [Environment]::CurrentDirectory = $editorDirectory
    $inputBytes = [IO.File]::ReadAllBytes($archiveFile)
    $archive = [SARCExt.SARC]::UnpackRamN([SZS.YAZ0]::Decompress($inputBytes))
    $checked = 0
    foreach ($member in $archive.Files.Keys) {
        if (-not $member.EndsWith('.msbt')) { continue }
        $file = [Spotlight.FileFormats.MsbtFile]::Read($archive.Files[$member])
        if ([Convert]::ToBase64String($file.Write()) -ne [Convert]::ToBase64String($archive.Files[$member])) {
            throw "No-edit round trip changed $member"
        }
        $checked++
    }
    $memberName = $StageName + '.msbt'
    if (-not $archive.Files.ContainsKey($memberName)) { throw "No $memberName in archive." }
    $file = [Spotlight.FileFormats.MsbtFile]::Read($archive.Files[$memberName])
    $template = $file.Labels | Where-Object { $_.StartsWith('ScenarioName_') } | Select-Object -First 1
    $label = 'ScenarioName_objSpotlightTest'
    $file.SetPlainText($label, 'Spotlight round-trip test', $template)
    $archive.Files[$memberName] = $file.Write()
    $outputBytes = [SZS.YAZ0]::Compress([SARCExt.SARC]::PackN($archive))
    $reopened = [SARCExt.SARC]::UnpackRamN([SZS.YAZ0]::Decompress($outputBytes))
    foreach ($member in $archive.Files.Keys) {
        if ([Convert]::ToBase64String($archive.Files[$member]) -ne [Convert]::ToBase64String($reopened.Files[$member])) {
            throw "Archive round trip changed $member"
        }
    }
    $title = ''
    if (-not [Spotlight.FileFormats.MsbtFile]::Read($reopened.Files[$memberName]).TryGetPlainText($label, [ref]$title) -or $title -ne 'Spotlight round-trip test') {
        throw 'Inserted title did not survive the archive round trip.'
    }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($archiveFile)) -ne [Convert]::ToBase64String($inputBytes)) {
        throw 'Source archive changed during verification.'
    }
    Write-Output "PASS: $checked MSBT no-edit round trips; inserted title and all archive members survived SARC/Yaz0 repacking. Source archive unchanged."
}
finally { [Environment]::CurrentDirectory = $previousDirectory }
