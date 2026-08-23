[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator

& (Join-Path $PSScriptRoot 'Disable-CredentialProvider.ps1')

$service = Get-Service -Name $script:PhoneUnlockServiceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    try {
        $deleteResponse = Send-PhoneUnlockSetupRequest -Request @{ command = 'DELETE_CREDENTIAL' }
        if (-not $deleteResponse.success) { Write-Warning $deleteResponse.message }
    }
    catch {
        Write-Warning "저장 자격 증명 삭제 요청 실패: $($_.Exception.Message)"
    }
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $script:PhoneUnlockServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
    & sc.exe delete $script:PhoneUnlockServiceName | Out-Null
}
Get-NetFirewallRule -DisplayName $script:PhoneUnlockFirewallName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

$installRoot = Get-PhoneUnlockInstallRoot
$expectedRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'PhoneUnlock'))
if ((Test-Path -LiteralPath $installRoot) -and $installRoot.Equals($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $installRoot -Recurse -Force
}

Write-Host 'Phone Unlock 서비스, 로그인 타일, 방화벽 규칙과 설치 파일을 제거했습니다.' -ForegroundColor Green
Write-Host 'ProgramData\PhoneUnlock의 서비스 구성/인증서는 복구 가능성을 위해 남겨 두었습니다.' -ForegroundColor Yellow
