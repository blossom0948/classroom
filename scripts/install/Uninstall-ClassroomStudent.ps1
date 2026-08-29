[CmdletBinding()]
param(
    [switch]$KeepConfiguration
)

$ErrorActionPreference = "Stop"
$serviceName = "ClassroomStudentService"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
    }
    sc.exe delete $serviceName | Out-Null
}

if (-not $KeepConfiguration) {
    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    if (Test-Path -LiteralPath $serviceKey) {
        Remove-ItemProperty -LiteralPath $serviceKey -Name Environment -ErrorAction SilentlyContinue
    }
    $userEnvironment = "HKCU:\Environment"
    foreach ($name in @("CLASSROOM_DEVICE_ID", "CLASSROOM_IPC_TOKEN", "CLASSROOM_AGENT_VERSION")) {
        Remove-ItemProperty -LiteralPath $userEnvironment -Name $name -ErrorAction SilentlyContinue
    }
}

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Remove-ItemProperty -LiteralPath $runKey -Name ClassroomStudentDesktop -ErrorAction SilentlyContinue
Write-Host "Classroom Student components removed."
