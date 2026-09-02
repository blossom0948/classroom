[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:ProgramFiles "Blossom Classroom Student"),

    [switch]$KeepConfiguration,

    [switch]$KeepFiles
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Classroom 학생 앱 제거는 관리자 권한 PowerShell에서 실행해야 합니다."
}

$resolvedProgramFiles = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')
$resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$isInsideProgramFiles = $resolvedInstallRoot.StartsWith(
    "$resolvedProgramFiles\",
    [StringComparison]::OrdinalIgnoreCase)
if ($resolvedInstallRoot -eq $resolvedProgramFiles -or -not $isInsideProgramFiles) {
    throw "안전하지 않은 설치 경로이므로 제거를 중단했습니다: $resolvedInstallRoot"
}

$serviceName = "ClassroomStudentService"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
    }

    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Classroom 학생 Windows 서비스를 제거하지 못했습니다."
    }
}

Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try {
        $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith(
            "$resolvedInstallRoot\",
            [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        $false
    }
} | Stop-Process -Force -ErrorAction SilentlyContinue

if (-not $KeepConfiguration) {
    foreach ($name in @(
        "CLASSROOM_DEVICE_ID",
        "CLASSROOM_IPC_TOKEN",
        "CLASSROOM_AGENT_VERSION",
        "CLASSROOM_APPROVED_APPS"
    )) {
        [Environment]::SetEnvironmentVariable($name, $null, "User")
    }
}

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
foreach ($runValueName in @("BlossomClassroomStudent", "ClassroomStudentDesktop")) {
    Remove-ItemProperty -LiteralPath $runKey -Name $runValueName -ErrorAction SilentlyContinue
}

$machineRunKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
Remove-ItemProperty -LiteralPath $machineRunKey -Name "BlossomClassroomStudent" -ErrorAction SilentlyContinue

if (-not $KeepConfiguration) {
    $desktopConfigurationPath = Join-Path $env:ProgramData "Blossom Classroom Student\desktop-config.json"
    Remove-Item -LiteralPath $desktopConfigurationPath -Force -ErrorAction SilentlyContinue
}

$startMenuShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Classroom Student.lnk"
Remove-Item -LiteralPath $startMenuShortcut -Force -ErrorAction SilentlyContinue

if (-not $KeepFiles -and (Test-Path -LiteralPath $resolvedInstallRoot -PathType Container)) {
    Remove-Item -LiteralPath $resolvedInstallRoot -Recurse -Force
}

Write-Host "Classroom 학생 앱 제거가 완료되었습니다." -ForegroundColor Green
if ($KeepFiles) {
    Write-Host "프로그램 파일은 유지했습니다: $resolvedInstallRoot"
}
