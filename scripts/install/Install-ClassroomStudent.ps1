[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [string]$EnrollmentFile,

    [uri]$ServerUrl,

    [guid]$DeviceId,

    [string]$DeviceToken,

    [string]$IpcToken,

    [string]$AgentVersion = "0.2.0",

    [string]$InstallRoot = (Join-Path $env:ProgramFiles "Blossom Classroom Student"),

    [switch]$SkipDesktopStartup,

    [switch]$SkipDesktopLaunch,

    [switch]$UpgradeOnly
)

$ErrorActionPreference = "Stop"

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

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "학생용 Classroom 설치는 관리자 권한 PowerShell에서 실행해야 합니다."
}

$resolvedPackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
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

$serviceName = "ClassroomStudentService"
if ($UpgradeOnly) {
    if ($EnrollmentFile -or $ServerUrl -or $DeviceId -ne [guid]::Empty -or $DeviceToken) {
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

if ($EnrollmentFile) {
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
    throw "-EnrollmentFile 또는 -ServerUrl을 지정해야 합니다."
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

$serviceInstallRoot = Join-Path $InstallRoot "service"
$desktopInstallRoot = Join-Path $InstallRoot "desktop"
$installedServiceExecutable = Join-Path $serviceInstallRoot "Classroom.Student.Service.exe"
$installedDesktopExecutable = Join-Path $desktopInstallRoot "Classroom.Student.Desktop.exe"

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    if ($existingService.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        $existingService.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
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

if ($existingService) {
    & sc.exe config $serviceName binPath= ('"{0}"' -f $installedServiceExecutable) start= auto DisplayName= "Blossom Classroom Student Service" | Out-Null
}
else {
    & sc.exe create $serviceName binPath= ('"{0}"' -f $installedServiceExecutable) start= auto DisplayName= "Blossom Classroom Student Service" | Out-Null
}
if ($LASTEXITCODE -ne 0) {
    throw "Classroom 학생 Windows 서비스를 만들거나 업데이트하지 못했습니다."
}

& sc.exe description $serviceName "Blossom Classroom 학생 기기 연결 및 상태 제공 서비스" | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$serviceEnvironment = @(
    "CLASSROOM_SERVER_URL=$($ServerUrl.AbsoluteUri)",
    "CLASSROOM_DEVICE_ID=$DeviceId",
    "CLASSROOM_DEVICE_TOKEN=$DeviceToken",
    "CLASSROOM_IPC_TOKEN=$IpcToken",
    "CLASSROOM_AGENT_VERSION=$AgentVersion"
)
New-ItemProperty -Path $serviceRegistryPath -Name "Environment" -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null

[Environment]::SetEnvironmentVariable("CLASSROOM_IPC_TOKEN", $IpcToken, "User")
[Environment]::SetEnvironmentVariable("CLASSROOM_DEVICE_ID", $DeviceId.ToString(), "User")

$desktopRunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if ($SkipDesktopStartup) {
    Remove-ItemProperty -Path $desktopRunKey -Name "BlossomClassroomStudent" -ErrorAction SilentlyContinue
}
else {
    New-Item -Path $desktopRunKey -Force | Out-Null
    New-ItemProperty -Path $desktopRunKey -Name "BlossomClassroomStudent" -PropertyType String -Value ('"{0}"' -f $installedDesktopExecutable) -Force | Out-Null
}

Start-Service -Name $serviceName
$installedService = Get-Service -Name $serviceName
$installedService.WaitForStatus("Running", [TimeSpan]::FromSeconds(20))

if (-not $SkipDesktopLaunch) {
    Start-Process -FilePath $installedDesktopExecutable
}

Write-Host ""
Write-Host "Classroom 학생 앱 설치가 완료되었습니다." -ForegroundColor Green
Write-Host "  서비스: $($installedService.Status)"
Write-Host "  서버: $($ServerUrl.AbsoluteUri)"
Write-Host "  기기 ID: $DeviceId"
Write-Host "  설치 위치: $InstallRoot"
