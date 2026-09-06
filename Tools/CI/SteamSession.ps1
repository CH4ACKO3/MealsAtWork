# Encrypted SteamCMD config snapshots. The high-entropy refresh token derives the encryption key.
# Only the ciphertext is uploaded as an Actions artifact; plaintext stays in the runner temp directory.
function Protect-SteamSession([byte[]]$Data, [byte[]]$Key, [byte[]]$Context) {
    $nonce=[Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
    $cipher=[byte[]]::new($Data.Length)
    $tag=[byte[]]::new(16)
    $aes=[Security.Cryptography.AesGcm]::new($Key,16)
    try { $aes.Encrypt($nonce,$Data,$cipher,$tag,$Context) } finally { $aes.Dispose() }
    @{version=1; keyId=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Key));
      nonce=[Convert]::ToBase64String($nonce); tag=[Convert]::ToBase64String($tag);
      ciphertext=[Convert]::ToBase64String($cipher)} | ConvertTo-Json -Compress
}
function Unprotect-SteamSession([string]$Envelope, [byte[]]$Key, [byte[]]$Context) {
    $e=$Envelope | ConvertFrom-Json
    if ($e.version -ne 1) { throw 'Unsupported Steam session envelope.' }
    $cipher=[Convert]::FromBase64String($e.ciphertext)
    $data=[byte[]]::new($cipher.Length)
    $aes=[Security.Cryptography.AesGcm]::new($Key,16)
    try { $aes.Decrypt([Convert]::FromBase64String($e.nonce),$cipher,[Convert]::FromBase64String($e.tag),$data,$Context) }
    finally { $aes.Dispose() }
    return ,$data
}
function Get-SteamSessionKey {
    return ,[Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("MAW Steam session v1`0$env:STEAM_REFRESH_TOKEN"))
}
function Get-SteamSessionContext {
    return ,[Text.Encoding]::UTF8.GetBytes("294100/3796904995/$($env:STEAM_USERNAME.Trim().ToLowerInvariant())")
}
function Restore-SteamSession([string]$SteamRoot) {
    if (!$env:GH_TOKEN -or !$env:GH_REPO) { return }
    $raw=gh api "repos/$env:GH_REPO/actions/artifacts?name=steam-session-state&per_page=100"
    if ($LASTEXITCODE) { throw 'Cannot discover the encrypted Steam session cache.' }
    $items=@(($raw | ConvertFrom-Json).artifacts | Where-Object { !$_.expired -and $_.name -eq 'steam-session-state' } | Sort-Object id -Descending)
    if (!$items.Count) { Write-Host 'Steam session cache: seed configuration (first run).'; return }
    $archive=Join-Path $SteamRoot 'session-cache.zip'
    Invoke-WebRequest "https://api.github.com/repos/$env:GH_REPO/actions/artifacts/$($items[0].id)/zip" -Headers @{Authorization="Bearer $env:GH_TOKEN"} -OutFile $archive
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry=$zip.GetEntry('maw-session.json')
        if ($zip.Entries.Count -ne 1 -or !$entry -or $entry.Length -gt 1048576) { throw 'Invalid encrypted Steam cache archive.' }
        $reader=[IO.StreamReader]::new($entry.Open())
        try { $envelope=$reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
    $key=Get-SteamSessionKey
    $e=$envelope | ConvertFrom-Json
    if ($e.keyId -cne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($key))) {
        Write-Host 'Steam session cache: refresh token rotated; using seed configuration.'
        return
    }
    $data=Unprotect-SteamSession $envelope $key (Get-SteamSessionContext)
    [IO.File]::WriteAllBytes((Join-Path $SteamRoot 'config/config.vdf'),$data)
    Write-Host 'Steam session cache: authenticated snapshot restored.'
}
function Save-SteamSession([string]$SteamRoot) {
    if (!$env:STEAM_SESSION_OUTPUT) { return }
    $data=[IO.File]::ReadAllBytes((Join-Path $SteamRoot 'config/config.vdf'))
    $envelope=Protect-SteamSession $data (Get-SteamSessionKey) (Get-SteamSessionContext)
    [IO.File]::WriteAllText($env:STEAM_SESSION_OUTPUT,$envelope,[Text.UTF8Encoding]::new($false))
    Write-Host 'Steam session cache: encrypted refreshed state ready for the next run.'
}
