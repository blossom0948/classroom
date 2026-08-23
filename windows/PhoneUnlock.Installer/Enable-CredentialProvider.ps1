[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator

$installRoot = Get-PhoneUnlockInstallRoot
$providerDll = Join-Path $installRoot 'PhoneUnlock.CredentialProvider.dll'
if (-not (Test-Path -LiteralPath $providerDll -PathType Leaf)) {
    throw "Credential Provider DLL이 없습니다: $providerDll"
}
if ((Get-Service -Name $script:PhoneUnlockServiceName -ErrorAction Stop).Status -ne 'Running') {
    throw 'Phone Unlock 서비스가 실행 중이 아닙니다.'
}

$response = Send-PhoneUnlockSetupRequest -Request @{ command = 'STATUS' }
if (-not $response.success) {
    throw "서비스 상태 확인 실패: $($response.message)"
}
$status = $response.data | ConvertFrom-Json
if (-not $status.readyToEnableCredentialProvider) {
    throw '활성화 조건이 충족되지 않았습니다. 계정 저장, 휴대폰 페어링, 최근 10분 이내 인증 테스트를 완료하세요.'
}

$providerKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$($script:PhoneUnlockProviderGuid)"
$classKey = "HKLM:\SOFTWARE\Classes\CLSID\$($script:PhoneUnlockProviderGuid)"
$serverKey = Join-Path $classKey 'InprocServer32'
New-Item -Path $providerKey -Force | Out-Null
Set-Item -Path $providerKey -Value 'Phone Unlock'
New-Item -Path $classKey -Force | Out-Null
Set-Item -Path $classKey -Value 'Phone Unlock Credential Provider'
New-Item -Path $serverKey -Force | Out-Null
Set-Item -Path $serverKey -Value $providerDll
New-ItemProperty -Path $serverKey -Name 'ThreadingModel' -Value 'Apartment' -PropertyType String -Force | Out-Null

# Keep all Microsoft providers installed, but ask Windows to select Phone Unlock first.
New-Item -Path $script:PhoneUnlockLogonPolicyPath -Force | Out-Null
New-ItemProperty `
    -Path $script:PhoneUnlockLogonPolicyPath `
    -Name $script:PhoneUnlockDefaultProviderValue `
    -Value $script:PhoneUnlockProviderGuid `
    -PropertyType String `
    -Force | Out-Null

Write-Host 'Phone Unlock을 잠금화면 기본 로그인으로 활성화했습니다.' -ForegroundColor Green
Write-Host '기본 PIN, 비밀번호, Windows Hello 공급자는 변경하지 않았습니다.' -ForegroundColor Green
Write-Host '잠금 전 설정 앱의 인증 테스트가 실제 휴대폰에서 성공했는지 다시 확인하세요.' -ForegroundColor Yellow
