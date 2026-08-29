[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:ProgramFiles "Blossom Classroom Server"),

    [string]$DataRoot = (Join-Path $env:ProgramData "Blossom Classroom"),

    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Classroom 서버 제거는 관리자 권한 PowerShell에서 실행해야 합니다."
}

$serviceName = "ClassroomServer"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
    }
    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Classroom 서버 Windows 서비스를 제거하지 못했습니다."
    }
}

$resolvedProgramFiles = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')
$resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$isInstalledUnderProgramFiles = $resolvedInstallRoot.StartsWith(
    "$resolvedProgramFiles\",
    [StringComparison]::OrdinalIgnoreCase)
$installDirectoryExists = Test-Path -LiteralPath $resolvedInstallRoot -PathType Container
if ($isInstalledUnderProgramFiles -and $installDirectoryExists) {
    Remove-Item -LiteralPath $resolvedInstallRoot -Recurse -Force
}

if ($RemoveData) {
    $resolvedProgramData = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')
    $resolvedDataRoot = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\')
    if (-not $resolvedDataRoot.StartsWith("$resolvedProgramData\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "안전하지 않은 데이터 경로이므로 삭제하지 않았습니다: $resolvedDataRoot"
    }
    if (Test-Path -LiteralPath $resolvedDataRoot -PathType Container) {
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
    }
}

Write-Host "Classroom 서버 프로그램 제거가 완료되었습니다." -ForegroundColor Green
if (-not $RemoveData) {
    Write-Host "데이터베이스는 복구를 위해 유지했습니다: $DataRoot"
}
