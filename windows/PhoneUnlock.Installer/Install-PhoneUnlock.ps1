[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'Common.ps1')

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $PSCommandPath
        $elevated = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -Wait -PassThru -ArgumentList $arguments
        exit $elevated.ExitCode
    }
    catch {
        Write-Host "관리자 확인 창을 열지 못했습니다: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

Assert-Administrator

$logDirectory = Join-Path $env:ProgramData 'PhoneUnlock\logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory 'install-latest.log'
Start-Transcript -LiteralPath $logPath -Force | Out-Null

try {
$installRoot = Get-PhoneUnlockInstallRoot
$serviceSource = Join-Path $PSScriptRoot 'service'
$setupSource = Join-Path $PSScriptRoot 'setup'
$providerSource = Join-Path $PSScriptRoot 'credential-provider\PhoneUnlock.CredentialProvider.dll'

foreach ($required in @(
    (Join-Path $serviceSource 'PhoneUnlock.Service.exe'),
    (Join-Path $setupSource 'PhoneUnlock.Setup.exe'),
    $providerSource
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "릴리스 파일이 없습니다: $required"
    }
}

$existing = Get-Service -Name $script:PhoneUnlockServiceName -ErrorAction SilentlyContinue
if ($null -ne $existing -and $existing.Status -ne 'Stopped') {
    Stop-Service -Name $script:PhoneUnlockServiceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
}

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
$serviceTarget = Join-Path $installRoot 'service'
$setupTarget = Join-Path $installRoot 'setup'
New-Item -ItemType Directory -Path $serviceTarget -Force | Out-Null
New-Item -ItemType Directory -Path $setupTarget -Force | Out-Null
Copy-Item -Path (Join-Path $serviceSource '*') -Destination $serviceTarget -Recurse -Force
Copy-Item -Path (Join-Path $setupSource '*') -Destination $setupTarget -Recurse -Force
Copy-Item -LiteralPath $providerSource -Destination (Join-Path $installRoot 'PhoneUnlock.CredentialProvider.dll') -Force
foreach ($scriptName in @('Common.ps1', 'Enable-CredentialProvider.ps1', 'Disable-CredentialProvider.ps1', 'Uninstall-PhoneUnlock.ps1', 'RECOVERY.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Destination $installRoot -Force
}

$serviceExe = Join-Path $serviceTarget 'PhoneUnlock.Service.exe'
if ($null -eq $existing) {
    New-Service `
        -Name $script:PhoneUnlockServiceName `
        -BinaryPathName ('"{0}"' -f $serviceExe) `
        -DisplayName 'Phone Unlock Service' `
        -Description 'Receives pinned Android biometric approvals for Windows login.' `
        -StartupType Automatic | Out-Null
    & sc.exe description $script:PhoneUnlockServiceName 'Receives pinned Android biometric approvals for the Phone Unlock credential provider.' | Out-Null
    & sc.exe failure $script:PhoneUnlockServiceName 'reset= 86400' 'actions= restart/5000/restart/15000/none/0' | Out-Null
}
else {
    $quotedBinaryPath = '"{0}"' -f $serviceExe
    & sc.exe config $script:PhoneUnlockServiceName "binPath= $quotedBinaryPath" 'start= auto' 'obj= LocalSystem' 'DisplayName= Phone Unlock Service' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Windows 서비스 구성을 갱신하지 못했습니다 (sc.exe $LASTEXITCODE)." }
}

Get-NetFirewallRule -DisplayName $script:PhoneUnlockFirewallName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
    -DisplayName $script:PhoneUnlockFirewallName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort 48231 `
    -RemoteAddress LocalSubnet `
    -Program $serviceExe `
    -Profile Private,Domain | Out-Null

Start-Service -Name $script:PhoneUnlockServiceName
(Get-Service -Name $script:PhoneUnlockServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))

Write-Host 'Phone Unlock 서비스와 설정 앱을 설치했습니다.' -ForegroundColor Green
Write-Host 'Credential Provider 타일은 아직 활성화하지 않았습니다.' -ForegroundColor Yellow
Start-Process -FilePath (Join-Path $setupTarget 'PhoneUnlock.Setup.exe')
}
catch {
    Write-Host ''
    Write-Host "설치 실패: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "설치 로그: $logPath" -ForegroundColor Yellow
    throw
}
finally {
    Stop-Transcript | Out-Null
}
