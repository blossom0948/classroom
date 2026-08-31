# Classroom 서비스 실행 안내

이 문서는 `classroom` 저장소에서 구현한 Classroom P0 서비스를 빌드하고
로컬 파일럿으로 실행하는 방법을 기록한다. `windowslogin` 저장소와 Phone
Unlock의 Credential Provider/Windows 암호 저장 경로는 이 제품에서 사용하지
않는다.

## 현재 구현 범위

현재 `Classroom.sln`에는 다음 구성요소가 있다.

- `Classroom.Core`: 토큰 해시, 고정 시간 비교, 감사 모델, 공통 JSON
- `Classroom.Protocol`: versioned envelope, enrollment, heartbeat/status,
  command ACK/result, 입력·크기 검증
- `Classroom.Server`: SQLite 영속 DB, 교사 login/session, class/session,
  device enrollment, heartbeat 상태, bounded command queue, audit API,
  HTTP/WebSocket TLS 보호, 정적 Teacher Console
- `Classroom.Student.Service`: Windows Service 경계, device bearer 인증,
  WebSocket 재연결, Desktop IPC, hello/heartbeat, 명령 ACK/result
- `Classroom.Student.Desktop`: 학생에게 보이는 WinForms UI, foreground 앱·배터리·
  네트워크 상태 제공, 메시지/URL/집중 overlay/승인 앱 실행
- `tests/*`: Core, Protocol, Server SQLite, Student Service/Desktop IPC self-test
- `scripts/install`: 학생 코드 설치 앱과 Service/Desktop을 설치하는 PowerShell
  스크립트

학생에게 보이는 Desktop이 수집하는 상태는 현재 foreground process 이름,
표시용 앱 이름, 배터리, 네트워크, policy flag와 마지막 heartbeat다. 창 제목,
키 입력, 쿠키, 비밀번호, 전체 URL, 화면 녹화/상시 캡처는 수집하지 않는다.
브라우저 domain은 Chrome/Edge 관리형 extension을 추가하는 후속 범위다.

## 빌드와 테스트

저장소의 `.tools/dotnet/dotnet.exe`는 로컬 검증용 .NET 8 SDK이며
`.gitignore` 대상이다.

```powershell
$dotnet = Join-Path (Get-Location) ".tools\dotnet\dotnet.exe"
& $dotnet build Classroom.sln --configuration Release
& $dotnet run --project tests\Classroom.Core.Tests\Classroom.Core.Tests.csproj --configuration Release --no-build
& $dotnet run --project tests\Classroom.Protocol.Tests\Classroom.Protocol.Tests.csproj --configuration Release --no-build
& $dotnet run --project tests\Classroom.Server.Tests\Classroom.Server.Tests.csproj --configuration Release --no-build
& $dotnet run --project tests\Classroom.Student.Service.Tests\Classroom.Student.Service.Tests.csproj --configuration Release --no-build
```

기존 Phone Unlock 복제본의 회귀 확인은 별도로 실행한다.

```powershell
& $dotnet build PhoneUnlock.sln --configuration Release
& $dotnet run --project windows\PhoneUnlock.Core.Tests\PhoneUnlock.Core.Tests.csproj --configuration Release --no-build
& $dotnet run --project windows\PhoneUnlock.Service.Tests\PhoneUnlock.Service.Tests.csproj --configuration Release --no-build
```

## 개발 서버와 Teacher Console

개발 서버는 로컬 HTTP/WS로만 열 수 있다. 기본 SQLite 파일은
`src\Classroom.Server\data\classroom.db`다.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:CLASSROOM_DATABASE_PATH = "data\classroom.db"
$dotnet = Join-Path (Get-Location) ".tools\dotnet\dotnet.exe"
& $dotnet run --project src\Classroom.Server\Classroom.Server.csproj --configuration Release --urls http://127.0.0.1:48240
```

브라우저에서 [http://127.0.0.1:48240/](http://127.0.0.1:48240/)을 열면
Teacher Console이 표시된다. 개발 DB 최초 계정은 다음과 같다.

```text
계정: teacher
비밀번호: ChangeMe!Classroom123
```

첫 실행 후 Teacher Console의 설정에서 비밀번호를 변경한다. 일반 계정의 로그인
token은 설치 앱을 다시 열어도 로그인 상태를 복원할 수 있도록 브라우저의
`localStorage`에 저장되고, 서버에는 해시만 보관된다. 게스트 token은 현재 탭의
`sessionStorage`에만 저장된다. 개발 기본 계정은 파일럿 전용이며 외부에 노출하지
않는다.

Teacher Console에서 할 수 있는 일:

1. 학급 선택
2. 수업 시작/종료
3. 학생 장치 온라인/오프라인, 현재 앱, 배터리, 네트워크, Agent 버전 확인
4. 선택 학생 또는 전체 학급에 메시지, HTTPS URL, 집중 모드, 승인 앱 전송
5. 각 명령의 장치별 ACK/result와 감사 기록 확인
6. 학생 이름으로 8자리 일회용 학생 코드 생성
7. 등록 장치 연결 해제(revoke)

학생 Desktop이 연결된 경우 명령 결과가 `APPLIED`가 되고, 연결되지 않은
경우 성공으로 위장하지 않고 `STUDENT_DESKTOP_OFFLINE`으로 기록된다.

## 실제 파일럿 순서

### 1. 서버 실행

위 개발 서버 또는 운영 TLS 서버를 먼저 실행한다. 수업 세션은 서버 재시작
후에도 SQLite에서 복원된다.

### 2. 장치 등록

Teacher Console에서 `학생 등록`을 누르고 학생 이름을 입력해 8자리 학생 코드를
발급한다. 학생 ID를 비워두면 서버가 새 ID를 생성한다. API를 직접 사용할
때의 순서는 다음과 같다.

```text
POST /api/classes/{classId}/enrollment-tickets
POST /api/devices/enroll-code
POST /api/classes/{classId}/sessions
```

학생 설치 앱이 교사가 전달한 코드를 `/api/devices/enroll-code`에 제출하고
device token을 서비스 설정에만 저장한다. 서버는 학생 코드와 device token
원문이 아니라 SHA-256 해시만 저장한다.

### 3. 학생 프로그램 설치

GitHub Actions의 `Classroom-Windows` artifact를 내려받아 압축을 풀거나,
관리자 PowerShell에서 두 프로그램을 publish한다.

```powershell
& $dotnet publish src\Classroom.Student.Service\Classroom.Student.Service.csproj -c Release -r win-x64 --self-contained false -o artifacts\student-service
& $dotnet publish src\Classroom.Student.Desktop\Classroom.Student.Desktop.csproj -c Release -r win-x64 --self-contained false -o artifacts\student-desktop
```

학생 PC에서는 [단일 설치 앱](https://github.com/blossom0948/classroom/releases/latest/download/Classroom.Student.Setup.exe)을
바로 실행해도 된다. 앱이 필요한 패키지를 자동으로 내려받고, 교사 화면의 학생
코드를 확인한 뒤 관리자 권한 PowerShell을 열어 서비스 설치와 시작까지 진행한다.

```text
학생용 패키지\Classroom.Student.Setup.exe
```

표준 사용자 계정만 허용되는 학교 노트북은 학생이 권한을 우회할 수 없다.
학교 IT 관리자가 Intune, 그룹 정책 또는 학교 소프트웨어 배포 도구로 같은
패키지를 관리자 권한으로 배포해야 한다. 기존 JSON 방식과
`Install-ClassroomStudent.ps1 -PackageRoot ... -EnrollmentFile ...` 인자는
관리형 배포·복구를 위해 계속 지원한다.

설치 스크립트는 `ClassroomStudentService`를 Automatic 서비스로 등록하고,
현재 사용자의 로그인 시 `Classroom.Student.Desktop.exe`를 실행한다. 서버
device token은 Service 환경에만 저장하고 Desktop은 IPC token만 사용한다.
학생이 로그인한 뒤 Desktop 창에서 `관리 활성화`, 연결 상태, 현재 앱/배터리/
네트워크를 확인할 수 있다.

서비스를 제거할 때는 다음을 실행한다.

```powershell
.\scripts\install\Uninstall-ClassroomStudent.ps1
```

### 4. 연결 확인

Teacher Console의 학생 카드가 `온라인`으로 바뀌고 현재 앱/배터리/네트워크가
갱신되면 첫 파일럿이 연결된 것이다. Desktop에서 메모장 같은 프로그램을
앞에 띄운 뒤 카드의 앱 이름이 바뀌는지 확인한다. 그 다음 테스트 학생에게
메시지 또는 HTTPS URL을 보내고 Desktop에 결과가 나타나는지 확인한다.

파일럿은 먼저 교사 1명·학생 3대로 시작하고, 재접속/서버 재시작/수업 종료를
확인한 뒤 10대, 30대 순서로 늘린다. 실제 학생 PC에 배포하기 전에는 학교의
관리 기기 정책과 학생/보호자 고지 절차를 적용한다.

## 운영 TLS와 인증

Development가 아닌 환경에서는 개발 token fallback과 평문 HTTP를 사용할 수
없다. 서버 시작 시 다음 둘 중 하나를 반드시 설정한다.

### 서버가 TLS 종료를 직접 담당하는 경우

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:CLASSROOM_DATABASE_PATH = "D:\Classroom\data\classroom.db"
$env:CLASSROOM_BOOTSTRAP_TEACHER_LOGIN = "blossom0948"
$env:CLASSROOM_BOOTSTRAP_TEACHER_PASSWORD = "use-a-long-random-initial-password"
$env:CLASSROOM_TLS_CERT_PATH = "D:\Classroom\secrets\classroom.pfx"
$env:CLASSROOM_TLS_CERT_PASSWORD = "certificate-password"
$env:CLASSROOM_TLS_PORT = "443"
```

서버는 인증서를 Kestrel HTTPS로 열고 HSTS/HTTPS redirection을 적용한다.

### 역방향 프록시가 TLS를 종료하는 경우

프록시에서 인증서와 443을 관리하고 내부 연결을 서버로 전달한다.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:CLASSROOM_TLS_TERMINATED_BY_PROXY = "true"
$env:CLASSROOM_DATABASE_PATH = "D:\Classroom\data\classroom.db"
$env:CLASSROOM_BOOTSTRAP_TEACHER_LOGIN = "blossom0948"
$env:CLASSROOM_BOOTSTRAP_TEACHER_PASSWORD = "use-a-long-random-initial-password"
```

프록시는 외부에서 HTTPS/WSS만 허용하고 `X-Forwarded-Proto`를 올바르게
전달해야 한다. 학생 Agent의 `CLASSROOM_SERVER_URL`은 운영에서 반드시
`wss://...`를 사용한다.

교사 login은 PBKDF2-SHA256 해시(600,000 iterations)로 저장되고, login
rate limit은 IP/계정 조합별로 적용된다. 운영에서는 초기 bootstrap 비밀번호를
첫 로그인 직후 변경하고, DB·PFX·서비스 환경을 관리자 ACL로 보호한다.

## HTTP API 요약

| API | 용도 |
| --- | --- |
| `GET /health` | 서버 실행 상태 |
| `GET /health/ready` | SQLite 포함 readiness |
| `POST /auth/login` | 교사 login 및 bearer session 발급 |
| `GET /auth/me` | 현재 교사와 허용 학급 확인 |
| `POST /auth/logout` | 현재 teacher session revoke |
| `POST /auth/change-password` | 교사 비밀번호 변경 |
| `GET /api/classes` | 교사에게 배정된 학급 |
| `GET /api/classes/{classId}/session` | 활성 수업 세션 |
| `POST /api/classes/{classId}/enrollment-tickets` | 장치 등록 ticket |
| `POST /api/devices/enroll-code` | 학생 코드 소비 및 device token 발급 |
| `POST /api/devices/enroll` | 기존 JSON ticket 호환용 장치 등록 |
| `POST /api/classes/{classId}/sessions` | 수업 시작 |
| `DELETE /api/classes/{classId}/sessions/{sessionId}` | 수업 종료 |
| `GET /api/classes/{classId}/students` | 제한된 학생 상태 snapshot |
| `DELETE /api/classes/{classId}/devices/{deviceId}` | 등록 장치 revoke |
| `POST /api/classes/{classId}/commands` | 검증된 명령 queue 등록 |
| `GET /api/classes/{classId}/commands/{requestId}` | 장치별 ACK/result 상태 |
| `GET /api/classes/{classId}/audit?limit=100` | 교사 범위 감사 이벤트 |
| `GET /ws/student?deviceId=...` | 학생 Agent WSS 연결 |

교사 API는 login 응답의 token을 다음 header로 보낸다.

```text
Authorization: Bearer <teacher-session-token>
```

학생 연결은 device token을 사용하며, 모든 class/session/teacher 범위는
서버에서 다시 검증한다. 교사 브라우저가 보내는 class ID만 믿지 않는다.

## 데이터와 복구

SQLite DB에는 users, classes, students, enrollment tickets, devices,
sessions, commands, audit events, teacher sessions가 저장된다. SQLite는
WAL과 busy timeout을 사용하며 DB 디렉터리가 없으면 자동 생성한다.

운영에서는 DB 파일과 `-wal`/`-shm` 파일을 함께 백업하고, 백업 복원 후
서버를 다시 시작해 session/device/audit 조회를 확인한다. 원격 명령은
완료/실패 상태를 기록하고, 서버 재시작 시 미완료 queue를 복원한다.

## 보안 및 범위 경계

- 교사는 배정된 학급만 조회/제어한다.
- 명령은 Message, HTTPS URL, FocusMode, allowlisted app launch만 허용한다.
- 임의 PowerShell, arbitrary shell, keylogger, webcam/microphone, 비밀번호/
  쿠키 수집, 숨은 화면 수집은 제공하지 않는다.
- FocusMode는 수업 전용 visible overlay이며 Windows 잠금 화면을 대체하지
  않는다. 수업 종료 시 서버가 해제 명령을 queue에 남기고, 서버 연결이
  60초 이상 끊기면 Student Desktop fail-safe가 overlay를 해제한다.
- 현재 P0에는 browser extension, thumbnail, individual live view, 정책
  catalog, signed MSI/installer와 updater가 아직 없다.
  이를 구현하기 전까지는 실제 학교 전체 배포가 아니라 승인된 파일럿으로
  운영한다.

## 기존 Phone Unlock과의 분리

Classroom은 별도 `Classroom.sln`과 `Blossom.Classroom.*` namespace를
사용한다. 기존 `PhoneUnlock.sln`, Credential Provider, Android 앱,
Windows Credential Manager password flow 및 `windowslogin` 저장소에는
의존하지 않는다. 설치 스크립트도 `ClassroomStudentService`와 별도 사용자
환경만 변경한다.
