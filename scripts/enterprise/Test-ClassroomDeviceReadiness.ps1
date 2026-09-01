[CmdletBinding()]
param(
    [switch]$AsJson
)

# Read-only school IT readiness check. It does not start, stop, change, or
# repair anything; it only reports the install and automatic-start signals
# that Classroom's student setup normally creates.
$classroomServiceName = 'ClassroomStudentService'
$classroomInstallRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'Blossom Classroom Student'
$classroomServicePath = Join-Path $classroomInstallRoot 'service\Classroom.Student.Service.exe'
$classroomDesktopPath = Join-Path $classroomInstallRoot 'desktop\Classroom.Student.Desktop.exe'
$classroomRunPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$classroomRunValueName = 'BlossomClassroomStudent'

$classroomService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$classroomServiceName'" -ErrorAction SilentlyContinue
$classroomRunValue = $null
try {
    $classroomRunValue = (Get-ItemProperty -Path $classroomRunPath -Name $classroomRunValueName -ErrorAction Stop).$classroomRunValueName
} catch {
    $classroomRunValue = $null
}

$classroomChecks = @(
    [pscustomobject]@{
        Check = '학생 Windows 서비스 설치'
        Result = if ($null -ne $classroomService) { '통과' } else { '확인 필요' }
        Detail = if ($null -ne $classroomService) { "상태: $($classroomService.State) · 시작 유형: $($classroomService.StartMode)" } else { 'ClassroomStudentService를 찾지 못했습니다.' }
    }
    [pscustomobject]@{
        Check = '학생 서비스 자동 시작'
        Result = if ($classroomService -and $classroomService.StartMode -eq 'Auto') { '통과' } else { '확인 필요' }
        Detail = if ($classroomService) { "시작 유형: $($classroomService.StartMode)" } else { '서비스가 설치되지 않았습니다.' }
    }
    [pscustomobject]@{
        Check = '학생 앱 파일'
        Result = if (Test-Path -LiteralPath $classroomDesktopPath) { '통과' } else { '확인 필요' }
        Detail = $classroomDesktopPath
    }
    [pscustomobject]@{
        Check = '서비스 파일'
        Result = if (Test-Path -LiteralPath $classroomServicePath) { '통과' } else { '확인 필요' }
        Detail = $classroomServicePath
    }
    [pscustomobject]@{
        Check = '현재 사용자 UI 자동 시작'
        Result = if ([string]::IsNullOrWhiteSpace($classroomRunValue)) { '확인 필요' } else { '통과' }
        Detail = if ([string]::IsNullOrWhiteSpace($classroomRunValue)) { '현재 사용자 Run 항목이 없습니다.' } else { $classroomRunValue }
    }
)

$classroomResult = [pscustomobject]@{
    CheckedAt = (Get-Date).ToString('o')
    ComputerName = $env:COMPUTERNAME
    InstallRoot = $classroomInstallRoot
    Checks = $classroomChecks
}

if ($AsJson) {
    $classroomResult | ConvertTo-Json -Depth 4
} else {
    Write-Output 'Classroom 학생 장치 준비 상태 (읽기 전용)'
    Write-Output "장치: $($classroomResult.ComputerName)"
    $classroomChecks | Format-Table -AutoSize
    Write-Output '확인 필요 항목은 설치 앱을 다시 실행하거나 학교 IT 담당자에게 전달해 주세요.'
}
