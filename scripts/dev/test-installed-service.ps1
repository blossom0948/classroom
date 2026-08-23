[CmdletBinding()]
param(
    [string]$OutputPath = "$env:ProgramData\PhoneUnlock\diagnostics\installed-service-test.json"
)

$ErrorActionPreference = 'Stop'
. "$env:ProgramFiles\PhoneUnlock\Common.ps1"

$diagnosticDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $diagnosticDirectory -Force | Out-Null
$logPath = Join-Path $diagnosticDirectory 'installed-service-test.log'
Start-Transcript -LiteralPath $logPath -Force | Out-Null

try {
$status = Send-PhoneUnlockSetupRequest -Request @{ command = 'STATUS' } -TimeoutMilliseconds 5000
if (-not $status.success) {
    throw "Service status failed: $($status.message)"
}
$statusData = $status.data | ConvertFrom-Json

$pairing = Send-PhoneUnlockSetupRequest -Request @{ command = 'CREATE_PAIRING' } -TimeoutMilliseconds 5000
if (-not $pairing.success) {
    throw "Pairing creation failed: $($pairing.message)"
}
$pairingData = $pairing.data | ConvertFrom-Json

$result = [ordered]@{
    testedAt = [DateTimeOffset]::Now.ToString('o')
    statusSuccess = [bool]$status.success
    computerName = [string]$statusData.computerName
    phones = @($statusData.phones).Count
    credentialConfigured = [bool]$statusData.credentialConfigured
    pairingSuccess = [bool]$pairing.success
    pairingComputer = [string]$pairingData.computerName
    hostCandidateCount = @($pairingData.hosts).Count
    port = [int]$pairingData.port
    tokenPresent = -not [string]::IsNullOrWhiteSpace([string]$pairingData.pairingToken)
}

$result | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$result | ConvertTo-Json
}
catch {
    [ordered]@{
        testedAt = [DateTimeOffset]::Now.ToString('o')
        success = $false
        error = $_.Exception.Message
    } | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    Write-Error $_
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
