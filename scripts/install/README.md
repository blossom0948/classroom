# Classroom Student 설치

일반적인 설치는 교사 콘솔의 `학생용 앱 다운로드` 링크에서
`Classroom.Student.Setup.exe`를 내려받아 실행하는 방식입니다. 교사는 먼저 학생 이름과
8자리 코드를 발급하고, 학생은 그 코드만 입력합니다. 설치 도우미가 필요한 학생
서비스·백그라운드 화면 구성요소를 자동으로 내려받아 서버 등록·관리자 권한 확인·설치를
진행합니다. 이미 설치된 PC에서 설치 도우미를 다시 실행하면 코드를 다시 묻지 않고
기존 등록을 사용해 백그라운드 앱을 다시 시작합니다.

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
단독으로 받은 설치 도우미는 공식 Windows 패키지를 자동으로 내려받은 뒤, 코드 등록이
성공하면 임시 장치 설정을 만들고 같은 설치 앱을 관리자 권한으로 다시 실행해 파일
복사·Windows 서비스 등록·학생 화면 실행을 처리합니다. 일반적인
학생 설치에는 PowerShell이 사용되지 않으므로 학교 PC의 PowerShell 실행 정책 때문에
설치가 중단되지 않습니다. 관리자 권한을 취소하거나 설치에 실패하면 같은 코드로
다시 설치를 시도할 수 있습니다. 코드가 외부에 노출된 경우에만 교사 콘솔에서 새
코드를 발급해 이전 코드를 폐기합니다.

설치가 끝나면 학생 화면은 기본적으로 창을 띄우지 않고 작업 표시줄 알림 영역의
`Classroom Student`로만 동작합니다. 알림 영역의 `상태 열기`를 눌렀을 때만 상태 창을
표시하고, 창을 닫아도 설치가 삭제되거나 등록이 초기화되지 않습니다. Windows 서비스는
자동 시작으로 등록되고, 모든 Windows 사용자 로그인 시 감시 프로세스가 자동으로
학생 화면을 실행합니다. 학생 화면 프로세스가 종료되어도 로그인 세션의 Classroom
감시 프로세스가 최대 약 2초 안에 다시 실행합니다. 등록 정보는
`%ProgramData%\Blossom Classroom Student\desktop-config.json`에 저장하므로 재부팅 후
환경변수가 전달되지 않아도 코드를 다시 입력할 필요가 없습니다. 같은 학생 코드를
재입력해도 서버는 새 학생 카드를 만들지 않고 기존 학생 장치 기록을 갱신합니다.

`Install-ClassroomStudent.cmd`와 JSON 파일 방식은 기존 파일럿과 관리형 배포를
위한 호환 경로입니다. 새 설치에서는 학생에게 이 수동 경로를 안내하지 않습니다.

고급 자동화가 필요한 경우에는 기존 PowerShell 인자를 직접 사용할 수 있다.

```powershell
.\scripts\install\Install-ClassroomStudent.ps1 `
  -PackageRoot (Resolve-Path .\artifacts\student) `
  -EnrollmentFile .\classroom-enrollment-학생.json
```

이 스크립트는 `ClassroomStudentService`를 Automatic 서비스로 등록하고,
컴퓨터의 Windows 로그인 시 `Classroom.Student.Desktop.exe --classroom-watchdog`를
시작한다. 감시 프로세스는 학생 화면을 백그라운드에서 실행하고 화면 프로세스가
종료되면 다시 실행한다. 서비스 토큰은 서비스 환경에만 넣고 Desktop에는 IPC 토큰만
넣는다. Desktop은 사용자 환경변수와 함께 ProgramData의 저장된 설정도 읽으므로
설치 직후나 재부팅 후 별도 재등록이 필요하지 않다.

제거:

```powershell
.\scripts\install\Uninstall-ClassroomStudent.ps1
```

설치·제거 스크립트는 `ClassroomStudentService`, Classroom의 사용자·컴퓨터 자동 시작
항목과 설치 폴더만 대상으로 한다. `-KeepConfiguration`을 사용하지 않은 제거에서는
ProgramData의 Desktop 등록 설정도 삭제한다. Phone Unlock 서비스, Credential Provider,
Windows 로그인 구성에는 접근하지 않는다.
