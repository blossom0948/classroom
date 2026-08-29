[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageRoot,

    [Parameter(Mandatory)]
    [uri]$ServerUrl,

    [Parameter(Mandatory)]
    [guid]$DeviceId,

    [Parameter(Mandatory)]
    [guid]$SessionId,

    [Parameter(Mandatory)]
    [string]$DeviceToken,

    [Parameter(Mandatory)]
    [string]$IpcToken,

    [string]$AgentVersion = "0.1.0-dev",
    [switch]$SkipDesktopStartup
)

$ErrorActionPreference = "Stop"
$resolvedRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$serviceExe = Join-Path $resolvedRoot "Classroom.Student.Service.exe"
$desktopExe = Join-Path $resolvedRoot "Classroom.Student.Desktop.exe"
if (-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)) {
    throw "Classroom.Student.Service.exe was not found under $resolvedRoot. Publish the service first."
}
if (-not $SkipDesktopStartup -and -not (Test-Path -LiteralPath $desktopExe -PathType Leaf)) {
    throw "Classroom.Student.Desktop.exe was not found under $resolvedRoot."
}
if ($ServerUrl.Scheme -notin @("ws", "wss", "http", "https")) {
    throw "ServerUrl must use ws://, wss://, http://, or https://."
}
if ($DeviceToken.Length -lt 16 -or $IpcToken.Length -lt 16) {
    throw "DeviceToken and IpcToken must each contain at least 16 characters."
}

$serviceName = "ClassroomStudentService"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
    }
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Milliseconds 300
}

New-Service -Name $serviceName `
    -BinaryPathName "`"$serviceExe`"" `
    -DisplayName "Classroom Student Service" `
    -Description "Classroom school device connection and heartbeat service." `
    -StartupType Automatic | Out-Null

$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$serviceEnvironment = @(
    "CLASSROOM_SERVER_URL=$ServerUrl",
    "CLASSROOM_DEVICE_ID=$DeviceId",
    "CLASSROOM_SESSION_ID=$SessionId",
    "CLASSROOM_DEVICE_TOKEN=$DeviceToken",
    "CLASSROOM_IPC_TOKEN=$IpcToken",
    "CLASSROOM_AGENT_VERSION=$AgentVersion"
)
New-ItemProperty -LiteralPath $serviceKey -Name Environment -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null

if (-not $SkipDesktopStartup) {
    $userEnvironment = "HKCU:\Environment"
    if (-not (Test-Path -LiteralPath $userEnvironment)) {
        New-Item -Path $userEnvironment -Force | Out-Null
    }
    New-ItemProperty -LiteralPath $userEnvironment -Name CLASSROOM_DEVICE_ID -Value $DeviceId -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $userEnvironment -Name CLASSROOM_IPC_TOKEN -Value $IpcToken -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $userEnvironment -Name CLASSROOM_AGENT_VERSION -Value $AgentVersion -PropertyType String -Force | Out-Null

    $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    if (-not (Test-Path -LiteralPath $runKey)) {
        New-Item -Path $runKey -Force | Out-Null
    }
    New-ItemProperty -LiteralPath $runKey -Name ClassroomStudentDesktop -Value "`"$desktopExe`"" -PropertyType String -Force | Out-Null
}

Start-Service -Name $serviceName
Write-Host "Classroom Student Service installed and started."
if (-not $SkipDesktopStartup) {
    Write-Host "Student Desktop will start at the next user logon. Sign out and in once to load its environment."
}
