[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator

$providerKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$($script:PhoneUnlockProviderGuid)"
$classKey = "HKLM:\SOFTWARE\Classes\CLSID\$($script:PhoneUnlockProviderGuid)"
if (Test-Path -LiteralPath $providerKey) { Remove-Item -LiteralPath $providerKey -Recurse -Force }
if (Test-Path -LiteralPath $classKey) { Remove-Item -LiteralPath $classKey -Recurse -Force }

Write-Host 'Phone Unlock 로그인 타일을 비활성화했습니다. 기본 로그인 옵션은 변경하지 않았습니다.' -ForegroundColor Green
