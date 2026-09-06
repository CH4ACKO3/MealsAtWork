$ErrorActionPreference='Stop'
. "$PSScriptRoot/SteamSession.ps1"
$key=[Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$context=[Text.Encoding]::UTF8.GetBytes('test-account')
$data=[Text.Encoding]::UTF8.GetBytes('test-only session data / 测试')
$envelope=Protect-SteamSession $data $key $context
$restored=Unprotect-SteamSession $envelope $key $context
if ([Convert]::ToBase64String($restored) -cne [Convert]::ToBase64String($data)) { throw 'Session round trip failed.' }
foreach ($mode in @('wrong-key','wrong-account','tamper')) {
    $testKey=[byte[]]$key.Clone(); $testContext=$context; $testEnvelope=$envelope
    if ($mode -eq 'wrong-key') { $testKey[0]=$testKey[0] -bxor 1 }
    if ($mode -eq 'wrong-account') { $testContext=[Text.Encoding]::UTF8.GetBytes('other-account') }
    if ($mode -eq 'tamper') {
        $e=$envelope | ConvertFrom-Json; $cipher=[Convert]::FromBase64String($e.ciphertext); $cipher[0]=$cipher[0] -bxor 1
        $e.ciphertext=[Convert]::ToBase64String($cipher); $testEnvelope=$e | ConvertTo-Json
    }
    $rejected=$false
    try { $null=Unprotect-SteamSession $testEnvelope $testKey $testContext } catch { $rejected=$true }
    if (!$rejected) { throw "Session authentication accepted $mode" }
}
Write-Host 'PASS: encrypted session round trip; wrong key/account and altered ciphertext rejected.'
