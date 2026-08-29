[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [Parameter(Mandatory = $true)]
    [string]$BootstrapTeacherPassword,

    [string]$BootstrapTeacherLogin = "teacher",

    [string]$ConsoleOrigins = "https://classroom-2en.pages.dev",

    [uri]$ListenUrl = "http://127.0.0.1:48240",

    [string]$InstallRoot = (Join-Path $env:ProgramFiles "Blossom Classroom Server"),

    [string]$DatabasePath = (Join-Path $env:ProgramData "Blossom Classroom\data\classroom.db")
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Classroom 서버 설치는 관리자 권한 PowerShell에서 실행해야 합니다."
}
if ($BootstrapTeacherPassword.Length -lt 12) {
    throw "초기 교사 비밀번호는 12자 이상이어야 합니다."
}
if ($ListenUrl.Scheme -ne "http" -or $ListenUrl.Host -notin @("127.0.0.1", "localhost", "::1")) {
    throw "Classroom Tunnel 원본 서버는 로컬 HTTP 주소에만 바인딩해야 합니다."
}

$resolvedPackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$serverCandidates = @(
    (Join-Path $resolvedPackageRoot "Classroom.Server.exe"),
    (Join-Path $resolvedPackageRoot "server\Classroom.Server.exe")
)
$serverExecutable = $serverCandidates | Where-Object {
    Test-Path -LiteralPath $_ -PathType Leaf
} | Select-Object -First 1
if (-not $serverExecutable) {
    throw "패키지에서 Classroom.Server.exe를 찾지 못했습니다: $resolvedPackageRoot"
}

$serviceName = "ClassroomServer"
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    if ($existingService.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        $existingService.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
    }
}

$sourceDirectory = Split-Path -Parent $serverExecutable
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Copy-Item -Path (Join-Path $sourceDirectory '*') -Destination $InstallRoot -Recurse -Force
New-Item -ItemType Directory -Path (Split-Path -Parent $DatabasePath) -Force | Out-Null

$installedExecutable = Join-Path $InstallRoot "Classroom.Server.exe"
if ($existingService) {
    & sc.exe config $serviceName binPath= ('"{0}"' -f $installedExecutable) start= auto DisplayName= "Blossom Classroom Server" | Out-Null
}
else {
    & sc.exe create $serviceName binPath= ('"{0}"' -f $installedExecutable) start= auto DisplayName= "Blossom Classroom Server" | Out-Null
}
if ($LASTEXITCODE -ne 0) {
    throw "Classroom 서버 Windows 서비스를 만들거나 업데이트하지 못했습니다."
}
& sc.exe description $serviceName "Blossom Classroom 교사 콘솔, 학생 연결, 명령 및 감사 서버" | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$serviceEnvironment = @(
    "ASPNETCORE_ENVIRONMENT=Production",
    "ASPNETCORE_URLS=$($ListenUrl.AbsoluteUri.TrimEnd('/'))",
    "CLASSROOM_DATABASE_PATH=$DatabasePath",
    "CLASSROOM_BOOTSTRAP_TEACHER_LOGIN=$BootstrapTeacherLogin",
    "CLASSROOM_BOOTSTRAP_TEACHER_PASSWORD=$BootstrapTeacherPassword",
    "CLASSROOM_TLS_TERMINATED_BY_PROXY=true",
    "CLASSROOM_CONSOLE_ORIGINS=$ConsoleOrigins"
)
New-ItemProperty -Path $serviceRegistryPath -Name "Environment" -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null

Start-Service -Name $serviceName
$installedService = Get-Service -Name $serviceName
$installedService.WaitForStatus("Running", [TimeSpan]::FromSeconds(20))

$readyUrl = [UriBuilder]::new($ListenUrl)
$readyUrl.Path = "/health/ready"
try {
    $ready = Invoke-RestMethod `
        -Method Get `
        -Uri $readyUrl.Uri `
        -Headers @{
            "X-Forwarded-For" = "127.0.0.1"
            "X-Forwarded-Proto" = "https"
        } `
        -TimeoutSec 15
    if ($ready.status -ne "ready") {
        throw "준비 상태 응답이 올바르지 않습니다."
    }
}
catch {
    throw "서비스는 시작됐지만 준비 상태 확인에 실패했습니다: $($_.Exception.Message)"
}

Write-Host "Classroom 서버 설치가 완료되었습니다." -ForegroundColor Green
Write-Host "  서비스: $($installedService.Status)"
Write-Host "  로컬 원본: $($ListenUrl.AbsoluteUri.TrimEnd('/'))"
Write-Host "  데이터베이스: $DatabasePath"
Write-Host "  허용 콘솔: $ConsoleOrigins"
