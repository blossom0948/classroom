# Classroom Student 설치

일반적인 설치는 교사 콘솔 관리자 메뉴에서 학생 이름을 입력해 8자리 코드를 만든 뒤,
관리자 메뉴의 학생용 설치 앱 버튼으로 받은 패키지에서 `Classroom.Student.Setup.exe`를 실행하는 방식입니다. 학생은
코드만 입력하고, 앱이 서버 등록·관리자 권한 확인·서비스 설치·학생 화면 실행을
자동으로 진행합니다. 코드는 관리자가 새로 발급하기 전까지 계속 사용할 수 있습니다.

학교가 표준 사용자 계정만 허용하는 경우에는 학생이 권한을 우회할 수 없습니다.
학교 IT 관리자가 동일한 패키지와 등록 파일을 Intune, 그룹 정책 또는 학교의
소프트웨어 배포 도구로 관리자 권한으로 배포해야 합니다. 설치 스크립트는
Phone Unlock, Credential Provider, Windows 로그인 설정을 변경하지 않습니다.

패키지를 직접 만들 때는 다음처럼 publish한다.

```powershell
$dotnet = Join-Path (Get-Location) ".tools\dotnet\dotnet.exe"
& $dotnet publish src\Classroom.Student.Service\Classroom.Student.Service.csproj -c Release -r win-x64 --self-contained false -o artifacts\student
& $dotnet publish src\Classroom.Student.Desktop\Classroom.Student.Desktop.csproj -c Release -r win-x64 --self-contained false -o artifacts\student
```

GitHub Actions의 `Classroom-Windows` 패키지에는 아래 설치 앱이 포함됩니다.
학생 PC에는 패키지 전체 압축을 한 번만 풀고 설치 앱만 실행합니다.

```text
학생용 패키지\
├─ Classroom.Student.Setup.exe
├─ student-service\Classroom.Student.Service.exe
├─ student-desktop\Classroom.Student.Desktop.exe
├─ Install-ClassroomStudent.cmd
├─ Install-ClassroomStudent.ps1
└─ ...
```

`Classroom.Student.Setup.exe`가 기본 설치 경로와 운영 서버를 자동으로 사용합니다.
코드 등록이 성공하면 임시 장치 설정을 만들고, 같은 설치 앱을 관리자 권한으로
다시 실행해 파일 복사·Windows 서비스 등록·학생 화면 실행을 처리합니다. 일반적인
학생 설치에는 PowerShell이 사용되지 않으므로 학교 PC의 PowerShell 실행 정책 때문에
설치가 중단되지 않습니다. 관리자 권한을 취소하거나 설치에 실패하면 같은 코드로
다시 설치를 시도할 수 있습니다. 코드가 외부에 노출된 경우에만 교사 콘솔에서 새
코드를 발급해 이전 코드를 폐기합니다.

`Install-ClassroomStudent.cmd`와 JSON 파일 방식은 기존 파일럿과 관리형 배포를
위한 호환 경로입니다. 새 설치에서는 학생에게 이 수동 경로를 안내하지 않습니다.

고급 자동화가 필요한 경우에는 기존 PowerShell 인자를 직접 사용할 수 있다.

```powershell
.\scripts\install\Install-ClassroomStudent.ps1 `
  -PackageRoot (Resolve-Path .\artifacts\student) `
  -EnrollmentFile .\classroom-enrollment-학생.json
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
