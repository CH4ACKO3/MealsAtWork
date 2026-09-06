param([Parameter(Mandatory=$true)][string]$ArtifactRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=(Resolve-Path -LiteralPath $ArtifactRoot).Path
$m=Get-Content "$root/manifest.json" -Raw | ConvertFrom-Json
if ($m.publishedfileid -ne '0000000000') { throw 'Bootstrap is only allowed before an item ID is pinned.' }
foreach ($key in @('STEAM_USERNAME','STEAM_PASSWORD','STEAM_CONFIG_VDF_BASE64')) {
    if (![Environment]::GetEnvironmentVariable($key)) { throw "Missing $key" }
}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('maw-bootstrap-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$temp/config" -Force | Out-Null
function Escape-Vdf([string]$text) { $text.Replace('\','\\').Replace('"','\"').Replace("`r",'') }
try {
    Invoke-WebRequest 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip' -OutFile "$temp/steamcmd.zip"
    Expand-Archive "$temp/steamcmd.zip" $temp
    $bootstrap=& "$temp/steamcmd.exe" '+quit' 2>&1
    if ($LASTEXITCODE -notin @(0,7)) { throw 'SteamCMD initialization failed.' }
    [IO.File]::WriteAllBytes("$temp/config/config.vdf",[Convert]::FromBase64String($env:STEAM_CONFIG_VDF_BASE64))
    $stage=Join-Path $root "MealsAtWork-$($m.version)"
    $vdf='"workshopitem"'+"`n{`n"+'"appid" "294100"'+"`n"+'"publishedfileid" "0"'+"`n"+
        '"visibility" "2"'+"`n"+'"title" "Meals at Work"'+"`n"+
        '"description" "'+(Escape-Vdf (Get-Content "$root/Description.en.bbcode" -Raw))+'"'+"`n"+
        '"contentfolder" "'+(Escape-Vdf $stage.Replace('\','/'))+'"'+"`n}`n"
    [IO.File]::WriteAllText("$root/bootstrap.vdf",$vdf,[Text.UTF8Encoding]::new($false))
    Push-Location $temp
    try {
        $probe=& "$temp/steamcmd.exe" '+@ShutdownOnFailedCommand' '1' '+@NoPromptForPassword' '1' '+login' $env:STEAM_USERNAME '+quit' 2>&1
        $cached=$LASTEXITCODE -eq 0 -and ($probe -join "`n") -match 'Waiting for user info\.\.\.\s*OK'
        $login=@('+@ShutdownOnFailedCommand','1','+@NoPromptForPassword','1','+login',$env:STEAM_USERNAME)
        if (!$cached) { $login+=$env:STEAM_PASSWORD }
        $output=& "$temp/steamcmd.exe" @login '+workshop_build_item' "$root/bootstrap.vdf" '+quit' 2>&1
        $code=$LASTEXITCODE
    } finally { Pop-Location }
    $updated=Get-Content "$root/bootstrap.vdf" -Raw
    if ($updated -match '"publishedfileid"\s+"([1-9][0-9]+)"') {
        Set-Content "$root/created-workshop-id.txt" $Matches[1] -NoNewline
        Write-Host "Created private Workshop item: $($Matches[1])"
    }
    if ($code -ne 0 -or ($output -join "`n") -notmatch '(?i)Success\.\s+(?:Published|Updated)' -or !(Test-Path "$root/created-workshop-id.txt")) {
        throw 'Workshop bootstrap did not confirm success. Inspect any created ID before retrying; raw login output withheld.'
    }
} finally {
    $resolved=[IO.Path]::GetFullPath($temp)
    if ($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -match '^maw-bootstrap-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
