[CmdletBinding()]
param(
    [string]$PackageRoot = $PSScriptRoot,

    [string]$EnrollmentFile,

    [string]$DeviceConfigFile,

    [uri]$ServerUrl,

    [guid]$DeviceId,

    [string]$DeviceToken,

    [string]$IpcToken,

    [string]$AgentVersion = "0.5.37",

    [string]$LogPath,

    [string]$InstallRoot = (Join-Path $env:ProgramFiles "Blossom Classroom Student"),

    [switch]$SkipDesktopStartup,

    [switch]$SkipDesktopLaunch,

    [switch]$UpgradeOnly
)

$ErrorActionPreference = "Stop"

function Write-ClassroomInstallLog {
    param([string]$Message)

    if ([string]::IsNullOrWhiteSpace($LogPath)) {
        return
    }

    try {
        $logDirectory = Split-Path -Parent $LogPath
        if ($logDirectory) {
            New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        }
        "[$([DateTimeOffset]::Now.ToString('O'))] $Message" | Add-Content -LiteralPath $LogPath -Encoding UTF8
    }
    catch {
        # Logging must never prevent the actual installation from continuing.
    }
}

function Invoke-ClassroomServiceControl {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [switch]$AllowFailure
    )

    $scExecutable = Join-Path $env:WINDIR "System32\sc.exe"
    $output = (& $scExecutable @Arguments 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        $operation = if ($Arguments.Count -gt 0) { $Arguments[0] } else { "unknown" }
        $detail = if ($output) { " $output" } else { "" }
        if ($AllowFailure) {
            Write-ClassroomInstallLog "선택적 Windows 서비스 작업($operation)을 건너뜀: 종료 코드 $LASTEXITCODE.$detail"
            return $output
        }
        throw "Windows 서비스 작업($operation)에 실패했습니다.$detail"
    }
    return $output
}

trap {
    Write-ClassroomInstallLog "설치 실패: $($_.Exception.Message) 위치: $($_.InvocationInfo.PositionMessage)"
    exit 1
}

function Get-FirstExistingFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string[]]$Candidates
    )

    foreach ($candidate in $Candidates) {
        $path = Join-Path $Root $candidate
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    return $null
}

function New-ClassroomToken {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }

    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-EnrollmentEndpoint {
    param(
        [Parameter(Mandatory = $true)]
        [uri]$WebSocketUrl
    )

    $builder = [UriBuilder]::new($WebSocketUrl)
    if ($builder.Scheme -eq "wss") {
        $builder.Scheme = "https"
        $builder.Port = if ($WebSocketUrl.IsDefaultPort) { -1 } else { $WebSocketUrl.Port }
    }
    elseif ($builder.Scheme -eq "ws") {
        $builder.Scheme = "http"
        $builder.Port = if ($WebSocketUrl.IsDefaultPort) { -1 } else { $WebSocketUrl.Port }
    }
    elseif ($builder.Scheme -notin @("http", "https")) {
        throw "지원하지 않는 서버 URL 형식입니다: $($builder.Scheme)"
    }

    $path = $builder.Path.TrimEnd('/')
    foreach ($suffix in @("/ws/student", "/student/ws")) {
        if ($path.EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase)) {
            $path = $path.Substring(0, $path.Length - $suffix.Length)
            break
        }
    }

    $builder.Path = "$path/api/devices/enroll"
    $builder.Query = ""
    $builder.Fragment = ""
    return $builder.Uri
}

function Copy-ClassroomPayload {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DestinationDirectory
    )

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceDirectory '*') -Destination $DestinationDirectory -Recurse -Force
}

Write-ClassroomInstallLog "설치 스크립트 시작: 관리자 권한 확인 중"
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "학생용 Classroom 설치는 관리자 권한 PowerShell에서 실행해야 합니다."
}

$resolvedPackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
Write-ClassroomInstallLog "설치 시작: 패키지 확인 완료 ($resolvedPackageRoot)"
if ($EnrollmentFile -and $DeviceConfigFile) {
    throw "-EnrollmentFile과 -DeviceConfigFile은 함께 사용할 수 없습니다."
}
if (-not $EnrollmentFile -and -not $DeviceConfigFile -and -not $UpgradeOnly) {
    $enrollmentCandidates = @(Get-ChildItem -LiteralPath $resolvedPackageRoot -Filter "classroom-enrollment-*.json" -File -ErrorAction SilentlyContinue)
    if ($enrollmentCandidates.Count -eq 1) {
        $EnrollmentFile = $enrollmentCandidates[0].FullName
        Write-Host "등록 파일을 자동으로 찾았습니다: $($enrollmentCandidates[0].Name)"
    }
    elseif ($enrollmentCandidates.Count -eq 0) {
        throw "패키지 폴더에서 classroom-enrollment-*.json 등록 파일을 찾지 못했습니다. 교사 콘솔에서 내려받은 등록 파일을 이 폴더에 넣어 주세요."
    }
    else {
        $names = ($enrollmentCandidates | ForEach-Object Name) -join ", "
        throw "등록 파일이 여러 개입니다 ($names). Install-ClassroomStudent.cmd에 사용할 등록 파일을 끌어다 놓거나 -EnrollmentFile을 지정해 주세요."
    }
}
$serviceExecutable = Get-FirstExistingFile -Root $resolvedPackageRoot -Candidates @(
    "Classroom.Student.Service.exe",
    "student-service\Classroom.Student.Service.exe",
    "student\service\Classroom.Student.Service.exe"
)
$desktopExecutable = Get-FirstExistingFile -Root $resolvedPackageRoot -Candidates @(
    "Classroom.Student.Desktop.exe",
    "student-desktop\Classroom.Student.Desktop.exe",
    "student\desktop\Classroom.Student.Desktop.exe"
)

if (-not $serviceExecutable) {
    throw "패키지에서 Classroom.Student.Service.exe를 찾지 못했습니다: $resolvedPackageRoot"
}
if (-not $desktopExecutable) {
    throw "패키지에서 Classroom.Student.Desktop.exe를 찾지 못했습니다: $resolvedPackageRoot"
}

Get-ChildItem -LiteralPath $resolvedPackageRoot -Recurse -File -ErrorAction SilentlyContinue |
    Unblock-File -ErrorAction SilentlyContinue
Write-ClassroomInstallLog "다운로드 차단 표시 확인 완료"

$serviceName = "ClassroomStudentService"
if ($UpgradeOnly) {
    if ($EnrollmentFile -or $DeviceConfigFile -or $ServerUrl -or $DeviceId -ne [guid]::Empty -or $DeviceToken) {
        throw "-UpgradeOnly은 새 등록 옵션과 함께 사용할 수 없습니다."
    }

    $existingEnvironment = (Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment -ErrorAction Stop).Environment
    $existingValues = @{}
    foreach ($entry in $existingEnvironment) {
        $separator = $entry.IndexOf('=')
        if ($separator -gt 0) {
            $existingValues[$entry.Substring(0, $separator)] = $entry.Substring($separator + 1)
        }
    }

    $ServerUrl = [uri]$existingValues["CLASSROOM_SERVER_URL"]
    $DeviceId = [guid]$existingValues["CLASSROOM_DEVICE_ID"]
    $DeviceToken = [string]$existingValues["CLASSROOM_DEVICE_TOKEN"]
    $IpcToken = [string]$existingValues["CLASSROOM_IPC_TOKEN"]
}

if ($DeviceConfigFile) {
    if ($UpgradeOnly) {
        throw "-UpgradeOnly은 새 장치 등록 옵션과 함께 사용할 수 없습니다."
    }

    $resolvedDeviceConfigFile = (Resolve-Path -LiteralPath $DeviceConfigFile).Path
    $deviceConfig = Get-Content -LiteralPath $resolvedDeviceConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($deviceConfig.format -ne "BLOSSOM-CLASSROOM-DEVICE-V1") {
        throw "지원하지 않는 장치 설정 파일입니다. Classroom 학생 등록 앱에서 다시 등록해 주세요."
    }

    $ServerUrl = [uri][string]$deviceConfig.serverUrl
    $DeviceId = [guid][string]$deviceConfig.deviceId
    $DeviceToken = [string]$deviceConfig.deviceToken
    $IpcToken = [string]$deviceConfig.ipcToken
}
elseif ($EnrollmentFile) {
    $resolvedEnrollmentFile = (Resolve-Path -LiteralPath $EnrollmentFile).Path
    $bundle = Get-Content -LiteralPath $resolvedEnrollmentFile -Raw -Encoding UTF8 | ConvertFrom-Json

    if ($bundle.format -ne "BLOSSOM-CLASSROOM-ENROLLMENT-V1") {
        throw "지원하지 않는 등록 파일입니다. 교사 콘솔에서 새 등록 파일을 내려받아 주세요."
    }

    $expiresAtUtc = [DateTimeOffset]::Parse([string]$bundle.expiresAtUtc)
    if ($expiresAtUtc -le [DateTimeOffset]::UtcNow) {
        throw "등록 파일의 유효 시간이 지났습니다. 교사 콘솔에서 새 파일을 발급해 주세요."
    }

    $ServerUrl = [uri][string]$bundle.serverUrl
    $DeviceId = [guid][string]$bundle.deviceId
    $enrollmentToken = [string]$bundle.enrollmentToken
    if (-not $enrollmentToken) {
        throw "등록 파일에 일회용 등록 토큰이 없습니다."
    }

    $enrollmentEndpoint = Get-EnrollmentEndpoint -WebSocketUrl $ServerUrl
    $requestBody = @{
        deviceId       = $DeviceId
        enrollmentToken = $enrollmentToken
        deviceName     = $env:COMPUTERNAME
        agentVersion   = $AgentVersion
    } | ConvertTo-Json

    Write-Host "Classroom 서버에 이 PC를 등록하는 중..."
    try {
        $enrollment = Invoke-RestMethod -Method Post -Uri $enrollmentEndpoint -ContentType "application/json" -Body $requestBody
    }
    catch {
        throw "Classroom 서버 등록에 실패했습니다 ($enrollmentEndpoint): $($_.Exception.Message)"
    }

    if ([string]$enrollment.deviceId -ne $DeviceId.ToString() -or -not [string]$enrollment.deviceToken) {
        throw "서버가 올바른 기기 등록 응답을 반환하지 않았습니다."
    }

    $DeviceToken = [string]$enrollment.deviceToken
}

if (-not $ServerUrl) {
    throw "-DeviceConfigFile, -EnrollmentFile 또는 -ServerUrl을 지정해야 합니다."
}
if ($ServerUrl.Scheme -notin @("ws", "wss")) {
    throw "학생 에이전트 서버 URL은 ws:// 또는 wss:// 형식이어야 합니다."
}
if ($DeviceId -eq [guid]::Empty) {
    throw "유효한 DeviceId가 필요합니다."
}
if ([string]::IsNullOrWhiteSpace($DeviceToken)) {
    throw "유효한 DeviceToken이 필요합니다."
}
if ([string]::IsNullOrWhiteSpace($IpcToken)) {
    $IpcToken = New-ClassroomToken
}
Write-ClassroomInstallLog "장치 설정 확인 완료"

$serviceInstallRoot = Join-Path $InstallRoot "service"
$desktopInstallRoot = Join-Path $InstallRoot "desktop"
$installedServiceExecutable = Join-Path $serviceInstallRoot "Classroom.Student.Service.exe"
$installedDesktopExecutable = Join-Path $desktopInstallRoot "Classroom.Student.Desktop.exe"

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    if ($existingService.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        $existingService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
    }
}

$resolvedDesktopInstallRoot = [IO.Path]::GetFullPath($desktopInstallRoot).TrimEnd('\')
Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try {
        $processPath = [IO.Path]::GetFullPath($_.Path)
        $processPath.StartsWith("$resolvedDesktopInstallRoot\", [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        $false
    }
} | Stop-Process -Force -ErrorAction SilentlyContinue

Copy-ClassroomPayload -SourceDirectory (Split-Path -Parent $serviceExecutable) -DestinationDirectory $serviceInstallRoot
Copy-ClassroomPayload -SourceDirectory (Split-Path -Parent $desktopExecutable) -DestinationDirectory $desktopInstallRoot
Write-ClassroomInstallLog "학생용 파일 복사 완료"

$uninstallSource = Join-Path $resolvedPackageRoot "Uninstall-ClassroomStudent.ps1"
if (Test-Path -LiteralPath $uninstallSource -PathType Leaf) {
    Copy-Item -LiteralPath $uninstallSource -Destination (Join-Path $InstallRoot "Uninstall-ClassroomStudent.ps1") -Force
}

if (-not (Test-Path -LiteralPath $installedServiceExecutable -PathType Leaf)) {
    throw "학생 서비스 파일 복사에 실패했습니다: $installedServiceExecutable"
}
if (-not (Test-Path -LiteralPath $installedDesktopExecutable -PathType Leaf)) {
    throw "학생 데스크톱 파일 복사에 실패했습니다: $installedDesktopExecutable"
}

$serviceBinaryArgument = ('"{0}"' -f $installedServiceExecutable)
$serviceDisplayNameArgument = "Blossom Classroom Student Service"
if ($existingService) {
    Invoke-ClassroomServiceControl @("config", $serviceName, "binPath=", $serviceBinaryArgument, "start=", "auto", "DisplayName=", $serviceDisplayNameArgument) | Out-Null
}
else {
    Invoke-ClassroomServiceControl @("create", $serviceName, "binPath=", $serviceBinaryArgument, "start=", "auto", "DisplayName=", $serviceDisplayNameArgument) | Out-Null
}

Invoke-ClassroomServiceControl @("description", $serviceName, "Blossom Classroom 학생 기기 연결 및 상태 제공 서비스") -AllowFailure | Out-Null
Invoke-ClassroomServiceControl @("failure", $serviceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000") -AllowFailure | Out-Null
Write-ClassroomInstallLog "Windows 서비스 등록 완료"

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$serviceEnvironment = @(
    "CLASSROOM_SERVER_URL=$($ServerUrl.AbsoluteUri)",
    "CLASSROOM_DEVICE_ID=$DeviceId",
    "CLASSROOM_DEVICE_TOKEN=$DeviceToken",
    "CLASSROOM_IPC_TOKEN=$IpcToken",
    "CLASSROOM_AGENT_VERSION=$AgentVersion"
)
New-ItemProperty -Path $serviceRegistryPath -Name "Environment" -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null

# Keep a machine-level copy for the interactive tray process. User environment
# variables are still written for compatibility, but a school image or a
# different logon policy must not force the student to enroll again after reboot.
$desktopConfigurationDirectory = Join-Path $env:ProgramData "Blossom Classroom Student"
$desktopConfigurationPath = Join-Path $desktopConfigurationDirectory "desktop-config.json"
New-Item -ItemType Directory -Path $desktopConfigurationDirectory -Force | Out-Null
[ordered]@{
    format = "BLOSSOM-CLASSROOM-DESKTOP-V1"
    deviceId = $DeviceId.ToString()
    ipcToken = $IpcToken
    agentVersion = $AgentVersion
    savedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
} | ConvertTo-Json -Compress | Set-Content -LiteralPath $desktopConfigurationPath -Encoding UTF8
Write-ClassroomInstallLog "학생 화면 시작 설정 저장 완료: $desktopConfigurationPath"

[Environment]::SetEnvironmentVariable("CLASSROOM_AGENT_VERSION", $AgentVersion, "User")
[Environment]::SetEnvironmentVariable("CLASSROOM_IPC_TOKEN", $IpcToken, "User")
[Environment]::SetEnvironmentVariable("CLASSROOM_DEVICE_ID", $DeviceId.ToString(), "User")
$env:CLASSROOM_AGENT_VERSION = $AgentVersion
$env:CLASSROOM_IPC_TOKEN = $IpcToken
$env:CLASSROOM_DEVICE_ID = $DeviceId.ToString()

$desktopRunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$machineDesktopRunKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
if ($SkipDesktopStartup) {
    Remove-ItemProperty -Path $desktopRunKey -Name "BlossomClassroomStudent" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $machineDesktopRunKey -Name "BlossomClassroomStudent" -ErrorAction SilentlyContinue
}
else {
    New-Item -Path $desktopRunKey -Force | Out-Null
    New-ItemProperty -Path $desktopRunKey -Name "BlossomClassroomStudent" -PropertyType String -Value ('"{0}" --classroom-watchdog' -f $installedDesktopExecutable) -Force | Out-Null
    New-Item -Path $machineDesktopRunKey -Force | Out-Null
    New-ItemProperty -Path $machineDesktopRunKey -Name "BlossomClassroomStudent" -PropertyType String -Value ('"{0}" --classroom-watchdog' -f $installedDesktopExecutable) -Force | Out-Null
}

# Keep a visible way to reopen the installed app after the setup window closes.
$startMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$startMenuShortcut = Join-Path $startMenuDirectory "Classroom Student.lnk"
try {
    New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startMenuShortcut)
    $shortcut.TargetPath = $installedDesktopExecutable
    $shortcut.Arguments = "--classroom-watchdog"
    $shortcut.WorkingDirectory = Split-Path -Parent $installedDesktopExecutable
    $shortcut.Description = "Classroom 학교 학생 상태 앱"
    $shortcut.IconLocation = "$installedDesktopExecutable,0"
    $shortcut.Save()
} catch {
    Write-ClassroomInstallLog "시작 메뉴 바로가기를 만들지 못했지만 자동 시작은 유지됩니다: $($_.Exception.Message)"
}

try {
    Start-Service -Name $serviceName
    $installedService = Get-Service -Name $serviceName
    $installedService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(20))
}
catch {
    $serviceDiagnostics = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    $diagnosticText = if ($serviceDiagnostics) {
        "상태=$($serviceDiagnostics.State), 종료코드=$($serviceDiagnostics.ExitCode), 서비스코드=$($serviceDiagnostics.ServiceSpecificExitCode)"
    }
    else {
        "서비스 진단 정보를 읽지 못했습니다."
    }
    Write-ClassroomInstallLog "서비스 시작 실패: $diagnosticText"
    throw "학생 서비스가 시작되지 않았습니다. $diagnosticText"
}
Write-ClassroomInstallLog "서비스 실행 상태 확인 완료"

if (-not $SkipDesktopLaunch) {
    Start-Process -FilePath $installedDesktopExecutable -ArgumentList '--classroom-watchdog'
}

Write-Host ""
Write-Host "Classroom 학생 앱 설치가 완료되었습니다." -ForegroundColor Green
Write-Host "  서비스: $($installedService.Status)"
Write-Host "  서버: $($ServerUrl.AbsoluteUri)"
Write-Host "  기기 ID: $DeviceId"
Write-Host "  설치 위치: $InstallRoot"
Write-ClassroomInstallLog "설치 완료"
