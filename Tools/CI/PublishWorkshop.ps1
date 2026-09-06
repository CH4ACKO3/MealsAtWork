param([Parameter(Mandatory=$true)][string]$ArtifactRoot, [switch]$DryRun, [switch]$CheckOnly, [switch]$VerifyPublished)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/SteamSession.ps1"
$root=(Resolve-Path -LiteralPath $ArtifactRoot).Path
$m=Get-Content -LiteralPath "$root/manifest.json" -Raw | ConvertFrom-Json
if ($m.appid -cne '294100' -or $m.publishedfileid -cne '0000000000' -or
    $m.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$' -or
    ($m.tag -cne "v$($m.version)" -and !(($DryRun -or $CheckOnly) -and !$m.tag))) { throw 'Manifest must identify an explicit release tag and the existing MAW Workshop item.' }
$stage=Join-Path $root "MealsAtWork-$($m.version)"
$archive=Join-Path $root "MealsAtWork-$($m.version).zip"
if ((Get-FileHash $archive).Hash -cne $m.archiveSha256 -or
    (Get-FileHash "$root/release-notes.md").Hash -cne $m.notesSha256) { throw 'Archive/notes checksum mismatch.' }
foreach ($language in @('en','zh-CN')) {
    $description=Join-Path $root "Description.$language.bbcode"
    if ((Get-FileHash -LiteralPath $description).Hash -cne $m.descriptions.$language) { throw "Description checksum mismatch: $language" }
}
$hasLocalizedNotes=$m.PSObject.Properties.Name -contains 'localizedNotes'
if ($hasLocalizedNotes) {
    foreach ($language in @('en','zh-CN')) {
        if ((Get-FileHash "$root/release-notes.$language.md").Hash -cne $m.localizedNotes.$language) { throw "Localized release notes checksum mismatch: $language" }
    }
} elseif (!$DryRun -and !$CheckOnly -and !$VerifyPublished) { throw 'Publishing requires separate English and Chinese release notes.' }
$changeNote=if ($hasLocalizedNotes) { Get-Content "$root/release-notes.en.md" -Raw } else { Get-Content "$root/release-notes.md" -Raw }
$seen=@{}
foreach ($f in $m.files) {
    if ($f.path -match '(^/|:|\\|(^|/)\.\.(/|$))' -or $seen.ContainsKey($f.path)) { throw 'Invalid/duplicate manifest path.' }
    $seen[$f.path]=$true
    if ((Get-FileHash -LiteralPath (Join-Path $stage $f.path)).Hash -cne $f.sha256) { throw "Content checksum mismatch: $($f.path)" }
}
if (@(Get-ChildItem -LiteralPath $stage -File -Recurse).Count -ne $seen.Count) { throw 'Unexpected files in package.' }
$about=[xml](Get-Content -LiteralPath "$stage/About/About.xml" -Raw)
if ($about.ModMetaData.modVersion -cne $m.version -or
    (Get-Content "$stage/About/PublishedFileId.txt" -Raw).Trim() -cne '0000000000') { throw 'Package identity mismatch.' }
function Escape-Vdf([string]$value) { $value.Replace('\','\\').Replace('"','\"').Replace("`r",'') }
$vdf = '"workshopitem"' + "`n{`n" + '  "appid" "294100"' + "`n" + '  "publishedfileid" "0000000000"' + "`n" +
    '  "contentfolder" "' + (Escape-Vdf $stage.Replace('\','/')) + '"' + "`n" +
    '  "changenote" "' + (Escape-Vdf $changeNote) + '"' + "`n}`n"
$vdfPath=Join-Path $root 'workshop-upload.vdf'
[IO.File]::WriteAllText($vdfPath,$vdf,[Text.UTF8Encoding]::new($false))
if ($DryRun) { Write-Host 'PASS: checksums, release identity and content VDF and bilingual payload validated. No Steam login/upload performed.'; return }
foreach ($key in @('STEAM_USERNAME','STEAM_PASSWORD','STEAM_CONFIG_VDF_BASE64','STEAM_REFRESH_TOKEN')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($key))) { throw "Missing steam-workshop environment secret: $key" }
}
$publisher=Join-Path $root 'publisher/WorkshopPublisher.dll'
dotnet $publisher check $root
if ($LASTEXITCODE) { throw 'Bilingual authorization/ownership preflight failed; no files uploaded.' }
if (!$CheckOnly -and !$VerifyPublished) {
    dotnet $publisher prepare-notes $root
    if ($LASTEXITCODE) { throw 'Change-history pre-upload marker failed; no files uploaded.' }
}
$steam=Join-Path ([IO.Path]::GetTempPath()) ('maw-steam-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$steam/config" -Force | Out-Null
try {
    Invoke-WebRequest -Uri 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip' -OutFile "$steam/steamcmd.zip"
    Expand-Archive -LiteralPath "$steam/steamcmd.zip" -DestinationPath $steam
    # Finish first-run self-update before installing the saved login state.
    $bootstrap = & "$steam/steamcmd.exe" '+quit' 2>&1
    if ($LASTEXITCODE -notin @(0,7)) { throw "SteamCMD initialization failed (exit $LASTEXITCODE). No login attempted." }
    [IO.File]::WriteAllBytes("$steam/config/config.vdf",[Convert]::FromBase64String($env:STEAM_CONFIG_VDF_BASE64))
    Restore-SteamSession $steam
    # Try the persisted login token before password authentication. This probe performs no writes to Workshop.
    Push-Location -LiteralPath $steam
    try {
        $probe=& "$steam/steamcmd.exe" '+@ShutdownOnFailedCommand' '1' '+@NoPromptForPassword' '1' '+login' $env:STEAM_USERNAME '+quit' 2>&1
        $cachedLogin=$LASTEXITCODE -eq 0 -and ($probe -join "`n") -match 'Waiting for user info\.\.\.\s*OK'
    } finally { Pop-Location }
    $probeGuard=($probe -join "`n") -match '(?i)Steam Guard|two.factor|AccountLogonDenied|auth.*code|confirm.*sign.in'
    Write-Host "SteamCMD cached login accepted: $cachedLogin; guardRequested=$probeGuard"
    # Capture output instead of streaming login/session details into public Actions logs.
    $arguments=@('+@ShutdownOnFailedCommand','1','+@NoPromptForPassword','1','+login',$env:STEAM_USERNAME)
    if (!$cachedLogin) { $arguments+=$env:STEAM_PASSWORD }
    if (!$CheckOnly -and !$VerifyPublished) { $arguments+=@('+workshop_build_item',$vdfPath) }
    if ($VerifyPublished) { $arguments+=@('+workshop_download_item','294100','0000000000','validate') }
    $arguments+='+quit'
    Push-Location -LiteralPath $steam
    try {
        $output = & "$steam/steamcmd.exe" @arguments 2>&1
        $exitCode=$LASTEXITCODE
    } finally { Pop-Location }
    $text=$output -join "`n"
    if ($exitCode -eq 0 -and $text -match 'Waiting for user info\.\.\.\s*OK') { Save-SteamSession $steam }
    # Emit only fixed diagnostic categories; raw authentication output remains private.
    Write-Host ("SteamCMD diagnostics: exit={0}; userInfoComplete={1}; guardRequested={2}; invalidPassword={3}; networkFailure={4}" -f
        $exitCode, ($text -match 'Waiting for user info\.\.\.\s*OK'),
        ($text -match '(?i)Steam Guard|two.factor|AccountLogonDenied|auth.*code|confirm.*sign.in'),
        ($text -match '(?i)InvalidPassword|Invalid Password'),
        ($text -match '(?i)NoConnection|No Connection|Failed to connect|timeout'))
    if ($CheckOnly) {
        if ($exitCode -ne 0 -or $text -notmatch 'Waiting for user info\.\.\.\s*OK') { throw 'SteamCMD login check failed. Refresh Steam Guard locally; no Workshop content was changed.' }
        Write-Host 'PASS: SteamCMD login and bilingual ownership verified. No Workshop writes performed.'
        return
    }
    if ($VerifyPublished) {
        if ($exitCode -ne 0) { throw 'Published content download failed.' }
        $download=Join-Path $steam 'steamapps/workshop/content/294100/0000000000'
        foreach ($f in $m.files) {
            if ((Get-FileHash -LiteralPath (Join-Path $download $f.path)).Hash -cne $f.sha256) { throw "Published file checksum mismatch: $($f.path)" }
        }
        Write-Host 'PASS: downloaded Workshop files match the release manifest.'
    } elseif ($exitCode -ne 0 -or $text -notmatch '(?i)\bSuccess\.\s+(?:Published|Updated)[^\r\n]*\b0000000000\b') {
        throw 'Steam did not confirm the Workshop update. Validate login/Steam Guard locally and refresh the environment secrets; raw authentication output is withheld.'
    }
    dotnet $publisher publish $root
    if ($LASTEXITCODE) { throw 'Workshop files uploaded, but bilingual description verification failed. Inspect both languages before retrying.' }
    if ($hasLocalizedNotes) {
        dotnet $publisher $(if ($VerifyPublished) { 'repair-notes' } else { 'publish-notes' }) $root
        if ($LASTEXITCODE) { throw 'Workshop files and descriptions are current, but localized change-note verification failed.' }
    }
    Write-Host "Steam confirmed published Workshop item 0000000000 for $($m.tag)."
} finally {
    # Only this script-created temporary directory is removed; no credentials enter artifacts/caches.
    if ($steam.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($steam) -match '^maw-steam-[0-9a-f]{32}$') { Remove-Item -LiteralPath $steam -Recurse -Force }
}
