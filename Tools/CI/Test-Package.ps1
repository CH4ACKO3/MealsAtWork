param([Parameter(Mandatory=$true)][string]$ArtifactRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=(Resolve-Path -LiteralPath $ArtifactRoot).Path
$publisher=Join-Path $PSScriptRoot 'PublishWorkshop.ps1'
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('maw-package-test-'+[guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $root -Destination $testRoot -Recurse
try {
    $manifestPath=Join-Path $testRoot 'manifest.json'
    $original=Get-Content -LiteralPath $manifestPath -Raw
    $manifest=$original | ConvertFrom-Json
    $stage=Join-Path $testRoot "MealsAtWork-$($manifest.version)"
    function Assert-Rejected([string]$expected) {
        $rejected=$false
        try { & $publisher -ArtifactRoot $testRoot -DryRun } catch {
            if ($_.Exception.Message -notlike "*$expected*") { throw }
            $rejected=$true
        }
        if (!$rejected) { throw "Expected rejection: $expected" }
    }
    & $publisher -ArtifactRoot $testRoot -DryRun
    $manifest.publishedfileid='0'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath
    Assert-Rejected 'existing MAW Workshop item'
    Set-Content $manifestPath $original -NoNewline
    $manifest=$original | ConvertFrom-Json
    $manifest.tag='v999.0.0'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath
    Assert-Rejected 'explicit release tag'
    Set-Content $manifestPath $original -NoNewline
    $manifest=$original | ConvertFrom-Json
    $manifest.files[0].path='../outside.txt'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath
    Assert-Rejected 'Invalid/duplicate manifest path'
    Set-Content $manifestPath $original -NoNewline
    $license=Join-Path $stage 'LICENSE'
    $originalLicense=[IO.File]::ReadAllBytes($license)
    Add-Content $license 'tampered'
    Assert-Rejected 'Content checksum mismatch'
    [IO.File]::WriteAllBytes($license,$originalLicense)
    Set-Content (Join-Path $stage 'unexpected.txt') 'unexpected'
    Assert-Rejected 'Unexpected files'
    Remove-Item -LiteralPath (Join-Path $stage 'unexpected.txt')
    $localized=$manifest.PSObject.Properties.Name -contains 'localizedNotes'
    $notes=Join-Path $testRoot $(if ($localized) { 'release-notes.en.md' } else { 'release-notes.md' })
    Set-Content $notes "引号 `"quote`" / path C:\test`nEnglish update"
    $manifest=$original | ConvertFrom-Json
    if ($localized) { $manifest.localizedNotes.en=(Get-FileHash $notes).Hash } else { $manifest.notesSha256=(Get-FileHash $notes).Hash }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath
    & $publisher -ArtifactRoot $testRoot -DryRun
    $vdf=Get-Content (Join-Path $testRoot 'workshop-upload.vdf') -Raw
    if ($vdf -notmatch '\\"quote\\"' -or $vdf -notmatch 'C:\\\\test' -or $vdf -notmatch "`nEnglish") { throw 'VDF escaping failed.' }
    if ($vdf -match '"(title|description|visibility|tags)"') { throw 'Unexpected Workshop metadata update.' }
    if ($vdf -notmatch '"previewfile"\s+"[^"\r\n]+/About/preview\.png"') { throw 'Missing Workshop preview upload.' }
    Write-Host 'PASS: invalid identity/tag/path, modified/extra files, VDF escaping and metadata preservation.'
} finally {
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved) -match '^maw-package-test-[0-9a-f]{32}$') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
