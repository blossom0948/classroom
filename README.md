# Blossom Classroom

학교 관리 Windows PC에서 학생의 수업 상태를 투명하게 확인하고, 담당 교사가
검증된 수업 명령을 보내는 Classroom 서비스입니다. 개인용 Phone Unlock과는
별도 제품이며 `windowslogin` 저장소나 Credential Provider, Windows 암호 저장
기능에 의존하지 않습니다.

## 지금 구현된 서비스

- 교사 로그인, 세션 만료·로그아웃·비밀번호 변경, 학급별 서버 권한 검증
- SQLite 영속 DB와 서버 재시작 후 장치·수업·명령·감사 기록 복구
- 일회용 학생 등록 파일 발급과 학생 PC 원클릭 등록·설치 흐름
- Windows Student Service의 인증된 WSS 재연결, heartbeat, 동적 수업 참여
- 학생에게 보이는 Student Desktop과 알림 영역 상태, 앱·배터리·네트워크 제공
- 학생 선택/전체 대상 메시지, HTTPS URL, 집중 모드, 승인 앱 실행
- 명령 queue → 전달 → ACK → 적용 결과의 장치별 추적과 감사 기록
- 수업 종료 시 집중 모드 자동 정리 및 연결 단절 시 학생 PC fail-safe
- 장치 연결 해제(revoke), 운영 TLS/reverse proxy 보호, CORS allowlist
- Cloudflare Pages 정적 교사 콘솔과 Pages Function 기반 HTTPS/WSS 원본 proxy
- Firebase 이메일 회원가입·로그인, Google 로그인과 Classroom bearer session 교환
- 교사용 공개 랜딩페이지와 신규 계정 온보딩 흐름
- Windows 서비스 설치 스크립트, Docker 서버 이미지, GitHub Actions 패키지

화면 몰래 수집, 키 입력·비밀번호·쿠키 수집, webcam/microphone, 임의 원격 셸은
구현하지 않습니다. 브라우저 도메인은 관리형 extension이 연결되기 전에는
`확인 필요`로 표시합니다.

## 저장소 구조

```text
Classroom.sln
src/
  Classroom.Core/              보안 토큰, 감사, 공통 직렬화
  Classroom.Protocol/          등록, heartbeat, 명령, ACK/result 프로토콜
  Classroom.Server/            ASP.NET Core API/WSS, SQLite, Teacher Console
  Classroom.Student.Service/   Windows Service, 서버 재연결, Desktop IPC
  Classroom.Student.Desktop/   보이는 학생 UI, 상태 provider, 명령 적용
tests/                          의존성 없는 Core/Protocol/Server/IPC self-test
scripts/install/                서버·학생 Windows 서비스 설치/복구
scripts/deploy/                 Cloudflare Pages 정적 산출물 빌드
functions/                      Pages에서 Classroom 서버로 보내는 API/WSS proxy
```

## 로컬 실행

필수 환경은 .NET 8 SDK와 Windows 10/11입니다.

```powershell
& .\.tools\dotnet\dotnet.exe build .\Classroom.sln -c Release
& .\.tools\dotnet\dotnet.exe run --project .\src\Classroom.Server\Classroom.Server.csproj -c Release --urls http://127.0.0.1:48240
```

[http://127.0.0.1:48240/](http://127.0.0.1:48240/)에서 개발 콘솔을 열 수
있습니다. 개발 DB의 최초 계정은 `teacher` / `ChangeMe!Classroom123`이며,
외부 운영 환경에서는 이 기본값이 허용되지 않습니다.

모든 Classroom self-test 실행:

```powershell
$tests = @(
  '.\tests\Classroom.Core.Tests\Classroom.Core.Tests.csproj',
  '.\tests\Classroom.Protocol.Tests\Classroom.Protocol.Tests.csproj',
  '.\tests\Classroom.Server.Tests\Classroom.Server.Tests.csproj',
  '.\tests\Classroom.Student.Service.Tests\Classroom.Student.Service.Tests.csproj'
)
foreach ($test in $tests) {
  & .\.tools\dotnet\dotnet.exe run --project $test -c Release --no-build
}
```

## 학생 PC 등록

1. 교사 콘솔에서 `장치 등록`을 눌러 학생 이름을 입력합니다.
2. 생성된 `classroom-enrollment-학생이름.json`을 다운로드합니다.
3. GitHub Actions의 `Classroom-Windows` 패키지와 등록 파일을 학생 PC에 옮깁니다.
4. 관리자 PowerShell에서 다음 한 줄을 실행합니다.

```powershell
.\Install-ClassroomStudent.ps1 -PackageRoot . -EnrollmentFile .\classroom-enrollment-학생이름.json
```

스크립트가 일회용 token으로 서버에 PC를 등록하고, 장치 token과 IPC token을
분리해 저장한 뒤 Service와 보이는 Desktop을 시작합니다. 수업 ID는 서버가
매 heartbeat마다 결정하므로 수업이 바뀌어도 재설치하지 않습니다.

## 운영 배포

Cloudflare Pages는 정적 교사 콘솔과 proxy를 담당하고, ASP.NET Core/SQLite/WSS
서버는 영속 디스크가 있는 Windows 서비스나 Docker host에서 실행합니다.
Pages만 배포하면 로그인 API와 학생 WebSocket이 존재하지 않으므로 완전한
서비스가 아닙니다.

정확한 환경 변수, Cloudflare build 설정, Tunnel/Docker 구성과 검증 절차는
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)를 따릅니다. 현재 공개 콘솔 주소는
[https://classroom-2en.pages.dev/](https://classroom-2en.pages.dev/)입니다.

상세 로컬 파일럿 설명은 [docs/CLASSROOM_MVP.md](docs/CLASSROOM_MVP.md), 보안과
개인정보 경계는 [docs/SECURITY.md](docs/SECURITY.md)와
[docs/PRIVACY.md](docs/PRIVACY.md)를 참고하세요.
