# Classroom Student 설치

교사 콘솔에서 학생 이름을 입력해 등록 파일을 만든 뒤, 학생용 패키지 폴더에
등록 파일을 넣고 `Install-ClassroomStudent.cmd`를 두 번 클릭하면 됩니다. 이
래퍼가 필요한 관리자 권한 PowerShell을 자동으로 열고 설치 후 서비스를
시작합니다. 학생별 등록 파일은 10분 동안 한 번만 사용할 수 있습니다.

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

교사 Console에서 `학생 등록`을 눌러 만든 `classroom-enrollment-*.json`을
publish 결과 폴더에 넣은 다음, 관리자 권한이 필요한 PC에서 아래 파일을
실행한다. 등록 토큰 교환과 장치 토큰 발급은 스크립트가 서버와 자동으로
처리한다.

```text
학생용 패키지\
├─ Classroom.Student.Service.exe
├─ Classroom.Student.Desktop.exe
├─ Install-ClassroomStudent.cmd
├─ Install-ClassroomStudent.ps1
└─ classroom-enrollment-홍길동.json
```

`Install-ClassroomStudent.cmd`는 폴더 안의 등록 JSON을 자동으로 찾는다. 여러
파일이 있으면 사용할 JSON을 `.cmd` 파일 위로 끌어다 놓으면 된다.

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
