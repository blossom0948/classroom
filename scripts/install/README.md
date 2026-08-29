# Classroom Student 설치

관리자 PowerShell에서 먼저 publish한다.

```powershell
$dotnet = Join-Path (Get-Location) ".tools\dotnet\dotnet.exe"
& $dotnet publish src\Classroom.Student.Service\Classroom.Student.Service.csproj -c Release -r win-x64 --self-contained false -o artifacts\student
& $dotnet publish src\Classroom.Student.Desktop\Classroom.Student.Desktop.csproj -c Release -r win-x64 --self-contained false -o artifacts\student
```

교사 Console에서 발급받은 `DeviceId`/`EnrollmentToken`으로 장치를 먼저
`POST /api/devices/enroll`에 등록하고, 응답의 `DeviceToken`과 수업의
`SessionId`를 준비한다. `IpcToken`은 학생 Service와 Desktop만 공유하는
16자 이상의 별도 난수다.

```powershell
$ipcToken = "replace-with-a-random-32-byte-token"
.\scripts\install\Install-ClassroomStudent.ps1 `
  -PackageRoot (Resolve-Path .\artifacts\student) `
  -ServerUrl wss://classroom.example.edu `
  -DeviceId "<device-id>" `
  -SessionId "<active-session-id>" `
  -DeviceToken "<device-token>" `
  -IpcToken $ipcToken
```

이 스크립트는 `ClassroomStudentService`를 Automatic 서비스로 등록하고,
현재 사용자 로그인 시 `Classroom.Student.Desktop.exe`를 시작한다. 서비스
토큰은 서비스 환경에만 넣고 Desktop에는 IPC 토큰만 넣는다. 처음 설치한 뒤
사용자 로그오프/로그온을 한 번 수행해야 Desktop이 사용자 환경 변수를 읽는다.

제거:

```powershell
.\scripts\install\Uninstall-ClassroomStudent.ps1
```

설치·제거 스크립트는 `ClassroomStudentService`와 해당 사용자 Run 항목만
대상으로 한다. Phone Unlock 서비스, Credential Provider, Windows 로그인
구성에는 접근하지 않는다.
