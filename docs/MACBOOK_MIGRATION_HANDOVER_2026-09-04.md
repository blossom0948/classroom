# Classroom 맥북 이전 인수인계

작성일: 2026-09-04 (Asia/Seoul)  
저장소: `https://github.com/blossom0948/classroom.git`  
기준 브랜치: `main`  
기준 커밋/태그: `ba91f2b` / `v0.5.35`

이 문서는 Windows 작업 환경에서 맥북으로 개발 환경을 옮길 때 바로 이어서
작업할 수 있도록 작성한 기술·운영 인수인계다. 기능 목록, 사용자 요청의 결정
과정, 현재 배포 상태, 릴리스 방법, 남은 한계와 다음 작업을 한 곳에 정리한다.

> 참고: Codex 대화의 원문 전체는 저장소 파일로 자동 내보내지지 않는다. 아래
> `작업 요청·대화 이력`은 실제 요청과 구현 판단을 최대한 빠짐없이 옮긴 상세
> 요약이며, 맥북에서 작업을 이어가기 위한 기준 문서다.

---

## 1. 가장 먼저 알아둘 현재 상태

| 항목 | 현재 값 / 상태 |
| --- | --- |
| 운영 교사 콘솔 | <https://classroom-2en.pages.dev/> |
| 운영 API | <https://classroom-api.blossom0948.cloud> |
| 학생 설치 짧은 주소 | <https://classroom-2en.pages.dev/student> |
| 최신 공개 버전 | `v0.5.35` |
| 최신 학생 설치 파일 | `Classroom.Student.Setup.exe` |
| 최신 학생 전용 업데이트 패키지 | `Classroom-Student-x64.zip` |
| 최신 전체 Windows 패키지 | `Classroom-Windows-x64.zip` |
| 운영 DB | Cloudflare Durable Object SQLite |
| 마지막 확인 결과 | API `ready`, DB `durable-object-sqlite`, 웹 콘솔 `0.5.35`, GitHub Release `v0.5.35` |

`v0.5.35`은 2026-09-02에 배포되었다. 특히 학생 화면 보기 실패의 근본 원인을
수정한 버전이므로, 화면 공유 관련 재현/수정은 반드시 이 태그를 기준으로 시작한다.

현재 작업 트리에는 사용자 첨부 파일 폴더인 `.codex-remote-attachments/`가
추적되지 않은 상태로 있을 수 있다. 이 폴더는 제품 소스나 배포 산출물이 아니므로
커밋하지 않는다.

---

## 2. 제품의 실제 범위와 경계

Classroom은 **학교 수업 운영용 교사 콘솔 + Windows 학생 에이전트**다.

### 현재 제공하는 것

- 학급별 학생 장치 등록, 수업 시작/종료, 온라인 상태 확인
- 현재 포그라운드 앱, 창 제목, 배터리, 네트워크 상태 표시
- 전체/선택 학생 메시지, HTTPS URL 열기, 집중 모드, 승인 앱 실행
- 교사가 수업 중 직접 켠 경우에 한한 학생 화면 보기
- 명령 ACK/결과 및 감사 기록
- 학생용 설치 도우미, Windows 서비스, 자동 시작, 자동 업데이트
- 관리자 권한 관리, 학생 코드 발급/재발급, 학생 앱 종료 비밀번호, 학교 읽기 전용 로그인

### 의도적으로 제공하지 않는 것

- 키 입력, 비밀번호, 쿠키, 오디오, 웹캠 수집
- 임의 PowerShell/명령 프롬프트/원격 셸 실행
- 학생 파일 열람 또는 임의 파일 전송
- 교사의 임의 마우스·키보드 조작, 원격 전원 끄기/켜기
- 숨은 상시 화면 녹화 또는 화면 프레임 영구 저장
- 일반 앱 코드만으로 Windows 작업 관리자, 삭제, 관리자 권한을 완벽히 차단하는 기능

후자의 Windows 강제 실행·삭제 방지는 학교 장비 관리 정책(Intune, GPO, Assigned
Access, AppLocker/App Control, 표준 사용자 계정) 범위다. 이 제품은 정상적인 창
닫기와 비정상 종료 감시를 제공하지만, OS 보안 경계를 우회하는 방식으로 프로세스를
삭제 불가능하게 만들지 않는다.

---

## 3. 현재 아키텍처

```text
교사 브라우저 / PWA
  │ HTTPS
  ▼
classroom-2en.pages.dev
  ├─ 정적 Teacher Console (index.html / app.js / styles.css)
  └─ Pages Function 프록시 경로(호환·선택 사용)
  │ HTTPS / WSS
  ▼
classroom-api.blossom0948.cloud
  └─ Cloudflare Worker (cloudflare/worker.js)
       └─ Durable Object "ClassroomState"
            └─ SQLite: 학교, 교사, 학급, 학생 코드, 장치,
               수업, 명령, 감사, 게스트/종료 비밀번호

학생 Windows PC
  Classroom.Student.Setup.exe
    └─ ClassroomStudentService (LocalSystem Windows Service)
         ├─ WSS 재연결 / 장치 heartbeat / 명령 ACK
         └─ 인증된 Named Pipe IPC
              └─ Classroom.Student.Desktop (로그인 사용자 세션)
                   ├─ 트레이 상태·학생 공지·집중 오버레이
                   ├─ 포그라운드 앱/창·배터리·네트워크 상태
                   └─ 교사가 요청한 동안만 JPEG 화면 프레임 캡처
```

### 핵심 데이터 흐름

1. 관리자 교사가 학생 이름/번호로 8자리 학생 코드를 발급한다.
2. 학생 PC의 설치 도우미가 코드를 서버에 제출해 장치 ID·장치 token·IPC token을 받는다.
3. LocalSystem 서비스는 장치 token으로 `/ws/student`에 WSS 연결한다.
4. 사용자 세션의 학생 Desktop은 IPC token으로 로컬 Named Pipe에 연결한다.
5. Desktop 상태가 Service → Worker로 heartbeat 전송된다.
6. 교사 콘솔은 수업·학급 권한 검증 뒤 상태/명령 API를 호출한다.
7. 명령은 서버 queue → WSS → Service → IPC → Desktop으로 전달되고, 결과가 역방향으로
   ACK/result와 감사 기록에 반영된다.

### 중요한 보안 분리

- 장치 token 원문은 학생 서비스 쪽에만 있으며 서버에는 hash 형태로 보관한다.
- Desktop에는 서버 장치 token이 아니라 IPC token만 전달된다.
- 화면 프레임은 Cloudflare Worker 메모리의 `screenFrames` Map에만 일시 보관하고 약
  15초 후 만료한다. Durable Object SQLite나 감사 로그에 화면 이미지 자체를 저장하지 않는다.
- 교사 권한은 서버에서 학급 범위로 확인한다. 브라우저가 보내는 class ID만 신뢰하지 않는다.

---

## 4. 코드 지도: 맥북에서 우선 열 파일

| 경로 | 역할 |
| --- | --- |
| `cloudflare/worker.js` | 실제 운영 API, Durable Object SQLite, 인증, 학생 WebSocket, 명령, 화면 프레임, 다운로드 프록시 |
| `cloudflare/wrangler.jsonc` | Worker 이름, 도메인, Durable Object binding, migration, 공개 환경 변수 |
| `src/Classroom.Server/wwwroot/index.html` | Teacher Console 구조, 대화상자, 학생 화면/관리자/설정 UI |
| `src/Classroom.Server/wwwroot/app.js` | 로그인, 학급 polling, 명령 전송, 화면 벽, 모달, 업데이트 UI 로직 |
| `src/Classroom.Server/wwwroot/styles.css` | 반응형·모션·Liquid Glass·고대비·화면 벽 스타일 |
| `src/Classroom.Server/wwwroot/config.js` | 운영 API origin, Firebase 웹 설정, 학생 설치 URL |
| `src/Classroom.Server/wwwroot/classroom-update.json` | 학생 앱 자동 업데이트 manifest |
| `src/Classroom.Server/wwwroot/sw.js` | PWA cache 버전과 shell cache 정책 |
| `src/Classroom.Student.Desktop/Ui/StudentDesktopForm.cs` | 학생 트레이 UI, 메시지, 종료 PIN, 화면 공유 상태, X 버튼 처리 |
| `src/Classroom.Student.Desktop/Status/WindowsStudentStatusProvider.cs` | 앱/창/배터리/네트워크/화면 JPEG 캡처 |
| `src/Classroom.Student.Desktop/StudentDesktopWatchdog.cs` | Desktop 자식 프로세스 감시·재실행 |
| `src/Classroom.Student.Service/Desktop/DesktopStatusBridge.cs` | Service ↔ Desktop Named Pipe bridge |
| `src/Classroom.Student.Service/Networking/ClassroomServerClient.cs` | Service ↔ Worker WSS transport |
| `src/Classroom.Student.Service/StudentUpdateWorker.cs` | 15분 자동 업데이트, 수동 업데이트 요청 |
| `src/Classroom.Student.Service/StudentUpdateHelper.cs` | 재부팅 없는 서비스/학생 앱 교체·재시작 |
| `src/Classroom.Student.Setup/` | 학생 코드 입력, 패키지 다운로드, UAC 설치 flow |
| `scripts/install/` | 수동/관리형 설치·제거 호환 경로 |
| `scripts/enterprise/Test-ClassroomDeviceReadiness.ps1` | 학교 IT용 읽기 전용 설치/자동시작 점검 |
| `.github/workflows/classroom-ci.yml` | Windows 빌드·테스트·릴리스 자산 생성 |
| `tests/` | Core, Protocol, Server, Service self-test 및 웹 품질 검사 |

---

## 5. 기능 현황

### 5.1 교사 콘솔

- 왼쪽 메뉴: `수업`, `학생 코드`, `관리자`(관리자만), `설정`
- 홈은 학생 카드/학생 화면 벽 중심이며, 수업 제어와 통계를 얇은 상단 strip으로 배치한다.
- 상단에는 학교명, 인사, 학급 선택, 날짜/날씨, 다크 모드가 있다.
- 반 선택기는 브라우저 기본 select만 쓰지 않고 고유 listbox와 높은 z-index를 사용한다.
- 큰 화면의 불필요한 빈 공간, 좁은 화면의 수직 글자, 카드 겹침 문제를 반복적으로 정리했다.
- CSS는 Liquid Glass 표현을 탐색/제어 표면에 제한하고, 학생 화면/정보 표면은 대비가 높은
  단색으로 유지한다. `prefers-reduced-motion`, `prefers-contrast: more` 대체 스타일도 있다.
- 모든 텍스트 흐름에 `writing-mode: horizontal-tb`, `word-break: keep-all` 계열 보호를 둬
  가로 폭이 줄어도 글자가 세로 한 글자씩 배치되는 현상을 막도록 작업했다.

### 5.2 로그인과 권한

#### 기본 학교 로그인

- 로그인 첫 화면에는 `학교 로그인`을 기본으로 표시한다.
- 학교 검색 → 관리자가 설정한 학교 공용 비밀번호 입력 → 읽기 전용 세션을 만드는 흐름이다.
- 이름은 "학교 로그인"이지만 기술적으로는 `guest`/읽기 전용 세션이다.
- 읽기 전용 세션은 수업 현황과 학생 활동을 볼 수 있지만 명령·화면 공유·관리 기능은 쓸 수 없다.

#### 관리자 로그인

- `관리자 로그인`을 눌렀을 때만 이메일/아이디·비밀번호, Google 로그인, 회원가입을 노출한다.
- Firebase 이메일/비밀번호 및 Google redirect 로그인 후 Classroom server session으로 교환한다.
- 공개 운영 콘솔은 현재 Google redirect 안정성 때문에 **Secure Cookie 모드가 아니라
  bearer session 호환 경로**를 사용한다. `config.js`의 `cookieSession: false`가 현재 진실이다.
- 계정 로그인 세션은 유지되고, 학교 읽기 전용 세션은 브라우저 세션 성격으로 제한한다.

#### 관리자 기능

- 학급 생성/명단 import, 학생 코드 발급·재발급
- 교사 계정에 관리자 권한 부여/해제
- 학생 이름 검색 후 `학생 관리자` 표식 부여/해제
- 학생 앱 종료 비밀번호 설정
- 학교 읽기 전용 로그인 비밀번호 설정

`학생 관리자`는 현재 서버에 저장되는 별도 표식이다. **교사 관리자 세션으로 승격되거나
교사 콘솔의 모든 권한을 얻는 기능은 아직 구현되지 않았다.** UI에도 이 제한을 명시한다.

### 5.3 학생 장치 상태와 명령

현재 교사가 수업 중 보낼 수 있는 명령은 다음으로 제한된다.

| 명령 | 구현 상태 |
| --- | --- |
| 학생/전체 메시지 | 구현됨. 학생 팝업은 궁서체 우선(`Gungsuh`, 대체 `Batang`)의 큰 흰 글자·빨간 배경 |
| HTTPS URL 열기 | 구현됨. HTTPS만 허용 |
| 집중 모드 | 구현됨. 학생 Desktop에 visible overlay 표시, 연결 60초 이상 단절 시 fail-safe 해제 |
| 승인 앱 실행 | 구현됨. allowlist 방식이며 현재 UI 선택지는 메모장/계산기 중심 |
| 학생 개인 메시지 | 구현됨. 일반 카드·화면 보기 카드·상세 패널에서 가능 |
| 화면 보기 | 구현됨. 아래 `화면 보기` 절 참고 |
| 임의 마우스/키보드/전원 원격 조작 | 구현하지 않음 |

### 5.4 "확인 필요"와 활동 판단

사용자는 YouTube의 교육 영상은 제외하고 게임/먹방 등을 경고하는 "AI" 판단을 요청했다.
현재 구현은 AI 모델이나 화면 분석이 아니라 **설명 가능한 메타데이터 규칙**이다.

- 앱/프로세스/도메인/창 제목의 게임 관련 키워드로 `확인 필요`를 표시한다.
- YouTube는 일반적으로 제외한다.
- YouTube 창 제목에 교육/수업/EBS 등 교육 키워드가 있으면 제외한다.
- YouTube 창 제목에 게임/먹방 키워드가 있으면 경고 후보로 표시한다.
- 화면 이미지, 오디오, 키 입력을 AI에 보내지 않는다.

주의할 점:

- Windows native status provider는 현재 `BrowserDomain`을 직접 읽지 못해 대부분 `null`이다.
  즉, 실제 판정에는 앱명·프로세스명·창 제목 영향이 더 크다.
- 이 기능은 교사의 확인 신호일 뿐 자동 차단/징계/원격 제어 기능이 아니다.
- 실제 AI 분류를 넣으려면 개인정보 영향 평가, 데이터 전송 동의, 오탐 검증, 비용/지연,
  수업 콘텐츠 예외 정책을 먼저 설계해야 한다.

### 5.5 요청되었지만 현재 구현되지 않았거나 범위가 달라진 항목

| 요청/아이디어 | 현재 상태 |
| --- | --- |
| 특정 학생의 원래 활동을 교사/다른 학생에게도 숨기는 예외 모드 | 구현되지 않음. 현재 `statusSharingMode`는 `visible-status` |
| 학생 관리자에게 교사 관리자와 동등한 모든 콘솔 권한 | 구현되지 않음. 별도 학생 관리자 표식만 존재 |
| 학생 동의 없이 교사가 마우스/전원/작업을 임의 제어 | 구현하지 않음 |
| Task Manager에서 모든 방법으로 종료/삭제 불가 | 앱만으로 구현하지 않음. Intune/GPO/표준 사용자 정책 영역 |
| 웹 페이지/동영상 내용을 실제 AI 비전 모델로 분석 | 구현하지 않음. 현재는 제목/앱 메타데이터 규칙 |
| 실시간 원격 데스크톱 영상 | 구현하지 않음. 교사 요청 동안의 JPEG 프레임 polling 방식 |

---

## 6. 화면 보기 기능: 동작, 성능, 최신 수정

### 동작 방식

1. 비게스트 교사가 활성 수업 중 `화면 보기`를 누른다.
2. 콘솔이 선택된 온라인 학생에게 `screenShare` 명령을 보낸다.
3. 학생 Desktop에 `화면 공유 중` 상태가 눈에 보이게 표시된다.
4. 학생 Desktop이 기본 화면을 JPEG로 축소/압축해 Service에 전달한다.
5. Service가 학생 WebSocket heartbeat로 Worker에 전달한다.
6. Worker는 화면 프레임을 메모리에 최대 약 15초 보관한다.
7. 콘솔은 같은 홈 학생 grid를 화면 벽으로 전환하고 polling으로 새 프레임을 표시한다.
8. 학생을 누르면 별도 새 창이 아니라 현재 콘솔의 detail pane을 전체 화면 성격으로 전환해
   큰 화면과 우측 상태 정보를 함께 표시한다.

### 화질·갱신 정책

- 최대 프레임: `1280 × 720` (화면 비율은 원본에 맞춤)
- 최대 JPEG 바이트: `72 KiB`
- WebSocket/IPC 최대 메시지: `128 KiB`
- 12명 이하: 약 `750 ms` 간격
- 13명 이상: 안정성을 위해 약 `1,000 ms` 간격
- 한 번에 화면 공유 대상: 최대 30명
- 화면 벽 페이지: 화면 폭에 따라 4/8/12명 기준

20명 전원을 0.75초보다 더 빠르게 갱신하면 각 학생의 업로드, 학교 무선망,
Cloudflare Worker CPU/메모리, 교사 브라우저의 base64 이미지 디코딩이 동시에
늘어난다. 현재 20명은 1초 정책이 합리적인 기본값이다. 더 빠르게 바꾸려면 실제
학교망에서 packet loss/latency/CPU를 계측한 뒤 0.75초 또는 WebRTC 기반 구조를
별도 설계해야 한다.

### v0.5.35에서 고친 근본 원인

학생 앱·공통 Protocol은 이미 최대 720p/72KiB 프레임을 만들 수 있었지만,
Cloudflare Worker의 수신 검증은 예전 값인 `640×480`, `36KiB`, `64KiB`로 남아
있었다. 따라서 정상적으로 보낸 720p 프레임이 Worker에서 조용히 `null` 처리되어
교사 콘솔에는 "첫 화면을 기다리는 중"만 보일 수 있었다.

`v0.5.35` 변경:

- Worker `MAX_MESSAGE_BYTES`: `64 KiB` → `128 KiB`
- Worker 화면 최대 폭/높이: `640×480` → `1280×720`
- Worker JPEG 크기: `36 KiB` → `72 KiB`
- base64 길이 한도: `49,152` → `98,304`
- `tests/web-quality.test.mjs`에 위 경계가 다시 어긋나지 않도록 guard 추가

관련 파일:

- `cloudflare/worker.js`
- `src/Classroom.Protocol/ProtocolConstants.cs`
- `src/Classroom.Student.Desktop/Status/WindowsStudentStatusProvider.cs`
- `src/Classroom.Server/wwwroot/app.js`

---

## 7. 학생 설치·자동 시작·종료·업데이트

### 설치 흐름

- 사용자가 여는 짧은 주소: <https://classroom-2en.pages.dev/student>
- Pages redirect 대상: `https://classroom-api.blossom0948.cloud/downloads/student-setup`
- Worker는 GitHub Release의 고정 asset만 프록시하고, Range/HEAD 요청을 넘겨 중단된
  다운로드 재개를 지원한다.
- `Classroom.Student.Setup.exe`는 학생 코드로 등록하고, 학생 전용 zip을 우선
  다운로드해 UAC 승인 뒤 서비스/데스크톱을 설치한다.
- GitHub release-assets 도메인이 학교망에서 막혀도 Classroom API 도메인을 우선
  사용하도록 `v0.5.34`에서 보완했다.

### 설치 후 저장 위치와 자동 시작

| 대상 | 위치/이름 |
| --- | --- |
| 설치 루트 | `C:\Program Files\Blossom Classroom Student` |
| Windows 서비스 | `ClassroomStudentService` |
| 서비스 시작 | Automatic + service recovery restart 정책 |
| Desktop 등록 설정 | `%ProgramData%\Blossom Classroom Student\desktop-config.json` |
| 현재 사용자 자동 시작 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\BlossomClassroomStudent` |
| 모든 사용자 자동 시작 | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\BlossomClassroomStudent` |
| 시작 인자 | `Classroom.Student.Desktop.exe --classroom-watchdog` |

학생 설치 앱을 다시 실행해도 기존 등록 정보를 찾아 재사용한다. 따라서 원래 목표였던
"재부팅/재실행 후 학생 코드를 다시 입력하거나 학생이 중복 생성되는 문제"를 막도록
구성되어 있다.

### 창 닫기와 실제 종료: v0.5.35 기준

- 기본 시작: 학생 창을 띄우지 않고 트레이/백그라운드에서 시작한다.
- 트레이 `상태 열기`: 필요한 경우에만 상태 창을 표시한다.
- 상태 창 X 또는 Alt+F4: **프로세스를 종료하지 않고 트레이로 숨긴다.**
- 숨긴 뒤에도 Named Pipe, Service 연결, 화면 공유, 상태 heartbeat는 유지된다.
- 트레이 `프로그램 종료` 또는 상태 창의 종료 버튼: 관리자 종료 비밀번호 확인을 요청한다.
- 서버가 종료 비밀번호를 승인한 경우에만 watchdog이 의도적 종료로 인식하고 Desktop을
  종료한다.
- 일반적인 비정상 Desktop 자식 프로세스 종료는 watchdog이 약 2초 주기로 다시 실행한다.
- Windows 재부팅/로그인 후에는 Run 항목이 watchdog을 다시 시작한다.

관련 핵심 파일:

- `src/Classroom.Student.Desktop/Ui/StudentDesktopForm.cs` → `HideToTray()`
- `src/Classroom.Student.Desktop/StudentDesktopWatchdog.cs`
- `src/Classroom.Core/Desktop/StudentDesktopExitAuthorization.cs`
- `src/Classroom.Student.Service/Desktop/DesktopStatusBridge.cs`

### 자동 업데이트

- manifest: `src/Classroom.Server/wwwroot/classroom-update.json`
- 주기: 학생 서비스 시작 약 15초 후 1회, 이후 15분마다
- 수동: 학생 상태 창의 `업데이트 확인`
- 패키지: `Classroom-Student-x64.zip` 우선, 전체 zip 보조 호환 경로
- 적용: 별도 update helper가 서비스와 Desktop을 중단/교체/재시작하므로 Windows 재부팅을
  기다리지 않는 것이 목표다.

업데이트 관련 핵심 파일:

- `src/Classroom.Student.Service/StudentUpdateWorker.cs`
- `src/Classroom.Student.Service/StudentUpdateHelper.cs`
- `src/Classroom.Server/wwwroot/classroom-update.json`

### 학교 IT 점검 명령

학생 Windows PC에서 관리자 또는 IT 담당자가 읽기 전용으로 확인할 수 있다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\enterprise\Test-ClassroomDeviceReadiness.ps1
```

이 스크립트는 서비스 존재/상태/자동 시작, 설치 파일, Run 항목만 읽고 변경하지 않는다.

---

## 8. 디자인·반응형 작업 이력

사용자는 애플 Liquid Glass와 Samsung One UI 계열의 "고급스럽지만 60대 교사도
읽기 쉬운" 디자인을 지속적으로 요청했다. 현재 디자인에서 적용한 원칙은 다음과 같다.

- 유리 소재는 sidebar, navigation, 대화상자, 일부 제어 표면에만 사용한다.
- 학생 화면, 학생 카드, 긴 정보, 명령 제어는 높은 대비의 단색 surface로 유지한다.
- 애니메이션은 section entrance, panel, card hover에 짧게 적용하고 `prefers-reduced-motion`
  사용자는 애니메이션을 줄인다.
- 고대비 모드에서는 blur/transparency를 더 불투명한 대비 표면으로 대체한다.
- 모바일은 floating rounded navigation을 사용하고, PC sidebar도 모서리/그림자를
  조정해 딱딱한 세로 막대 느낌을 줄였다.
- 드롭다운/학급 선택은 큰 카드 아래로 숨지 않도록 `z-index`를 명시했다.
- 카드·패널은 `min-width: 0`, `overflow`, text ellipsis/clamp, grid breakpoint를
  반복 적용해 글자 잘림/세로 배치를 막았다.
- 설정에서 정보성 카드가 과도하게 늘어나던 것을 정리하고, 약관/개인정보는 하단 footer
  링크로 이동했다.

주의: CSS는 여러 차례의 빠른 개선이 누적되어 후반부에 override 규칙이 많다. 큰
디자인 변경 전에는 `styles.css` 하단의 버전별 주석/override 순서를 먼저 살피고,
브라우저 desktop·tablet·mobile 폭에서 함께 확인해야 한다.

---

## 9. 작업 요청·대화 이력 (상세 요약)

아래는 이 프로젝트에서 실제로 요청되거나 결정된 흐름을 시간 순서로 압축한 것이다.

1. **관리자 화면 초기 정리**
   - 관리자 지정 입력과 버튼 위치 교체, 날짜 폰트 확대·날씨 아이콘/문구 배치,
     활동 탭 제거, 관리자 대상의 교사/학생 탭 분리를 요청했다.
   - 현재 관리자 화면에는 교사 관리자/학생 관리자 탭, 학생 검색/선택, 종료 PIN,
     학교 로그인 비밀번호 설정이 있다.

2. **학생 설치 실패 반복 조사**
   - 학생 코드 입력 후 설치가 `코드 1`, 서비스 시작 실패, process/file lock,
     `EntryPoint not found` 등으로 실패했다.
   - 설치 도우미, elevation, Windows 서비스 호출, 기존 서비스 중지/파일 교체 순서,
     학교망 다운로드 fallback을 계속 고쳤다.
   - 현재 설치는 단독 setup EXE, 학생 전용 package, 최대 3회 재시도, 20분 timeout,
     학교 API download proxy를 사용한다.

3. **학생 활동의 표시 범위 요청**
   - 특정 학생의 원래 활동을 전자칠판/교사에게도 안 보이게 하고 싶다는 요청이 있었다.
   - 현재 구현은 학생 상태를 기본적으로 보여 주는 `visible-status` 모드다. 개인별
     활동 숨김 예외 정책은 구현되지 않았으므로, 다음 개발자가 이 기능을 새로 설계해야 한다.

4. **학생 관리자 권한 요청**
   - 관리자 탭에서 학생을 검색해 관리자 권한을 주고 싶다는 요청이 있었다.
   - 현재는 `StudentAdminGrants`로 학생 관리자 표식을 저장한다.
   - 교사 관리자 권한을 학생 장치/학생 계정에 실제로 부여하는 것은 구현하지 않았다.

5. **학생 화면/서비스 안정화 요청**
   - 학생 카드에 현재 앱/창이 홈에서 보이게, 배터리/네트워크도 표시되게 요청했다.
   - Desktop status provider와 카드/상세 pane을 연결했고, detail에서는 긴 창 제목을
     잘리지 않도록 정리했다.

6. **학생 앱 종료/자동 시작 요청**
   - 학생이 X를 누르거나 프로그램을 닫았을 때 등록/서비스가 사라지지 않도록 요청했다.
   - v0.5.30에서 background watchdog/Run key/ProgramData 설정 저장/중복 등록 방지를
     추가했고, v0.5.35에서 X가 PIN 종료 대화상자를 띄우던 잘못된 흐름을 tray hide로 고쳤다.

7. **종료 비밀번호 요청**
   - 관리자만 종료 비밀번호를 바꾸고, 학생 종료 시 입력해야 한다는 요청이 있었다.
   - `StudentExitPins` 서버 hash + Desktop → Service → Server verification 흐름이 있다.
   - 정상 X는 이제 종료 요청으로 간주하지 않고 tray hide로 처리한다.

8. **자동 업데이트 요청**
   - 교사/학생 프로그램 모두 수동 재설치 없이 업데이트, 학생은 재부팅 없이 바로 적용을
     요청했다.
   - PWA update UI, 학생 update manifest, 15분 자동 check, isolated update helper를
     도입했다. Windows 재시작을 요구하지 않는 것이 현재 설계다.

9. **화면 보기 요청**
   - 별도 팝업이 아닌 홈 학생 카드 위치에서 화면 벽과 상세 pane을 보고 싶다는 요청이 있었다.
   - 현재 홈 grid가 monitor wall로 전환되며, 전체 화면, 페이지, Flip 스타일 이름/번호
     label, detail screen fullscreen을 제공한다.
   - 720p 정도 화질과 1초 안팎 갱신을 요청했고, v0.5.29~35에 걸쳐 반영했다.

10. **개인 메시지 요청**
    - 화면 보기 중이 아니어도 특정 학생에게 개인 메시지를 보낼 수 있게 해 달라는 요청이 있었다.
    - 일반 카드, 화면 카드, 상세 pane에서 개인 메시지 버튼이 제공된다.

11. **로그인/게스트 흐름 변경 요청**
    - 기본은 학교 로그인 하나로 보이게 하고, 아이디/Google/회원가입은 관리자 로그인에서만
      보이게 여러 번 다듬었다.
    - 현재 "학교 로그인"은 학교 선택 + 관리자가 정한 비밀번호의 read-only guest flow다.
    - Google 로그인 redirect 뒤 landing으로 돌아가던 회귀는 v0.5.25/v0.5.28에서 수정했다.

12. **UI/시인성/애니메이션 요청**
    - 창/글자 겹침, 세로 텍스트, 기본 Windows select, 긴 카드 여백, 모바일 스크롤,
      PC sidebar 경직감, 날짜·날씨/인사 배치 등을 반복 수정했다.
    - Liquid Glass 참고 이미지와 floating mobile navigation 참고 이미지를 바탕으로 현재
      CSS가 구성됐다.

13. **학생 설치 다운로드 장애**
    - 학교 노트북에서 GitHub release 다운로드가 갑자기 안 되던 사례가 있었다.
    - v0.5.32 짧은 주소, v0.5.33 학생 전용 zip/재시도, v0.5.34 Classroom API download
      proxy + Range forwarding으로 보완했다.

14. **화면 보기 최종 장애**
    - 화면 보기가 제대로 작동하지 않고 학생 화면이 안 보인다는 최신 보고가 있었다.
    - v0.5.35에서 Worker stale validation limit 불일치를 수정했다. 이 문서 시점의
      최신 코드/운영 API 기준으로는 720p 프레임이 전달될 수 있어야 한다.

---

## 10. 릴리스 이력: v0.5 계열

| 버전 | 핵심 변경 |
| --- | --- |
| v0.5.7 ~ 0.5.10 | Windows service 상태/설치 교체/학생 Desktop 연결·감시의 초기 안정화 |
| v0.5.11 | 홈 학생 활동 표시와 공지 UI 보완 |
| v0.5.12 | 학생 등록 지속성, 중복 장치 방지 |
| v0.5.13 | 라이브 화면 보기 및 자동 업데이트 초기 도입 |
| v0.5.14 | 반응형 교사 콘솔 재설계 |
| v0.5.15 | 종료 PIN과 상세 학생 화면 흐름 |
| v0.5.16 | 학생 업데이트 확인/적용 제어 |
| v0.5.17 | 학교 게스트 로그인 도입 |
| v0.5.18 | 게스트의 학생 코드 조회/자동 업데이트 보완 |
| v0.5.19 | 활동 "확인 필요" 분류 정책 |
| v0.5.20 | 홈 내부 학생 화면 벽 |
| v0.5.21 ~ 0.5.23 | 교실 운영 흐름, 세션 지속성, 반응형/시인성 polish |
| v0.5.24 | 학생 업데이트 helper로 재부팅 없는 적용, 간결한 학생 화면 |
| v0.5.25 | Google/계정 로그인 landing 회귀 수정 |
| v0.5.26 | 좁은 폭 텍스트/헤더/모션 안정화 |
| v0.5.27 | monitor-first 홈, 화면 detail/Flip label, Intune 안내 |
| v0.5.28 | Google redirect session 복구 재보완 |
| v0.5.29 | Liquid Glass 정리, 화면 720p/갱신 정책, 개인 메시지 |
| v0.5.30 | 기본 학교 로그인, background agent, 재부팅 자동 시작, 중복 등록 방지 |
| v0.5.31 | 학교 로그인/학교 게스트 UI를 하나의 기본 flow로 정리 |
| v0.5.32 | `/student` 짧은 설치 링크 |
| v0.5.33 | 학생 전용 package, 재시도/timeout 강화 |
| v0.5.34 | 학교망을 위한 Classroom API download proxy, Range 지원 |
| v0.5.35 | 720p frame 수신 한도 불일치 수정, X → tray hide 종료 흐름 수정 |

각 상세 내역은 `docs/RELEASE_NOTES-0.5.24.md`부터
`docs/RELEASE_NOTES-0.5.35.md`에 있다.

---

## 11. 맥북 개발 환경 준비

### 필수 도구

- Git
- Node.js LTS (권장: 20 이상)
- npm
- .NET 8 SDK
- Cloudflare 계정 권한 + Wrangler 로그인 권한
- GitHub 저장소 push/tag 권한
- Firebase project 관리 권한(인증 설정을 바꿀 경우)
- 실제 Windows PC 또는 Windows VM/원격 PC(학생 Desktop/Service/UI 수동 검증용)

예시:

```bash
git clone https://github.com/blossom0948/classroom.git
cd classroom
npm ci
dotnet --info
npx wrangler login
```

### macOS에서 가능한 것과 불가능한 것

| 작업 | macOS에서 가능 여부 |
| --- | --- |
| Worker/Pages JavaScript 검사, 테스트, 정적 build | 가능 |
| Cloudflare Worker 배포 | 계정 권한이 있으면 가능 |
| Git commit/push/tag, GitHub Actions 릴리스 트리거 | 가능 |
| `net8.0` Core/Protocol/Server 코드 작성·빌드 | 가능 |
| `net8.0-windows` 프로젝트 cross-build | 프로젝트에 `EnableWindowsTargeting=true`가 있어 대체로 가능하지만 Windows CI가 최종 기준 |
| WinForms 학생 앱 실행·트레이·화면 캡처·Windows Service 설치 테스트 | macOS에서 불가능 |
| UAC, Registry Run key, Windows Service recovery, 작업 표시줄 트레이 검증 | macOS에서 불가능 |

따라서 맥북에서는 코드/웹/Worker 작업을 하고, 실제 학생 Windows 동작은 GitHub Actions
Windows runner + 학교 테스트 노트북 또는 Windows VM에서 확인한다.

### 로컬 검증 명령

```bash
npm run check:pages
npm run check:worker
npm run test:pages
npm run test:worker-download
npm run test:quality
npm run build

dotnet build Classroom.sln -c Release --nologo
dotnet run --project tests/Classroom.Core.Tests/Classroom.Core.Tests.csproj -c Release --no-build --nologo
dotnet run --project tests/Classroom.Protocol.Tests/Classroom.Protocol.Tests.csproj -c Release --no-build --nologo
dotnet run --project tests/Classroom.Server.Tests/Classroom.Server.Tests.csproj -c Release --no-build --nologo
dotnet run --project tests/Classroom.Student.Service.Tests/Classroom.Student.Service.Tests.csproj -c Release --no-build --nologo
```

macOS에서 Windows-targeting restore 문제가 나면, 먼저 GitHub Actions의
`windows-latest` 결과를 확인한다. 개발 편의를 위해 강제로 플랫폼 한계를 우회하는
것보다 CI와 Windows 실기기 테스트를 release gate로 유지하는 편이 안전하다.

---

## 12. 배포 절차

### 12.1 Worker 코드가 바뀐 경우

`cloudflare/worker.js` 또는 `cloudflare/wrangler.jsonc`를 바꾸면 GitHub main push만으로
운영 Worker가 자동 배포되는 구조가 아니다. 맥북에서 명시적으로 실행한다.

```bash
npm run check:worker
npm run deploy:worker
curl https://classroom-api.blossom0948.cloud/health/ready
```

배포 전에는 `wrangler login` 또는 CI secret 기반 Cloudflare 인증이 필요하다.
Durable Object migration을 새로 추가할 때는 기존 `v1` migration을 수정하지 말고 새
migration 항목을 추가한다.

### 12.2 Teacher Console / Pages

Cloudflare Pages project `classroom-2en`의 production branch는 `main`이다.

| 설정 | 값 |
| --- | --- |
| Root | repository root |
| Build | `npm run build` |
| Output | `dist` |
| Optional proxy origin | `CLASSROOM_BACKEND_ORIGIN=https://classroom-api.blossom0948.cloud` |

`main` push 뒤 Pages 배포가 끝났는지 다음을 확인한다.

```bash
curl https://classroom-2en.pages.dev/version.json
curl -I https://classroom-2en.pages.dev/
```

### 12.3 Windows 설치 파일 release

`.github/workflows/classroom-ci.yml`은 Windows runner에서 다음을 수행한다.

1. `Classroom.sln` Release build
2. Core/Protocol/Server/Student Service self-test
3. Pages/Worker JavaScript 검사와 build
4. Windows self-contained publish
5. `Classroom-Windows-x64.zip`, `Classroom-Student-x64.zip`,
   `Classroom.Student.Setup.exe` 생성
6. tag push일 때 GitHub Release asset 업로드

일반 흐름:

```bash
git add <필요한 파일>
git commit -m "..."
git push origin main

git tag -a v0.5.36 -m "Classroom v0.5.36"
git push origin v0.5.36
```

tag가 CI를 통과하고 release asset이 올라간 뒤에만 학생 설치/자동업데이트 경로가 새
Windows binary를 받을 수 있다.

---

## 13. 새 버전 만들 때 반드시 같이 바꿀 곳

현재 버전 값은 중앙화되어 있지 않다. 예를 들어 `0.5.35 → 0.5.36` 배포 시 아래를
같이 확인한다.

| 파일 | 수정 내용 |
| --- | --- |
| `src/Classroom.Student.Desktop/Classroom.Student.Desktop.csproj` | Version/AssemblyVersion/FileVersion |
| `src/Classroom.Student.Service/Classroom.Student.Service.csproj` | Version/AssemblyVersion/FileVersion |
| `src/Classroom.Student.Setup/Classroom.Student.Setup.csproj` | Version/AssemblyVersion/FileVersion |
| `src/Classroom.Student.Setup/StudentSetupForm.cs` | `AgentVersion` |
| `scripts/install/Install-ClassroomStudent.ps1` | 기본 `AgentVersion` |
| `src/Classroom.Server/wwwroot/version.json` | console version |
| `src/Classroom.Server/wwwroot/classroom-update.json` | 학생 업데이트 version/package URL |
| `src/Classroom.Server/wwwroot/app.js` | `APP_VERSION` |
| `src/Classroom.Server/wwwroot/index.html` | asset query string과 settings 표시 버전 |
| `src/Classroom.Server/wwwroot/sw.js` | cache name + asset query string |
| `docs/RELEASE_NOTES-x.y.z.md` | 새 release note |
| `.github/workflows/classroom-ci.yml` | package에 복사할 release note 파일명 |

`Directory.Build.props`에는 과거 `0.4.0-beta.23` 기본 Version 값이 남아 있다.
현재 Classroom 프로젝트는 각각 override하므로 실행 버전에는 영향이 없지만,
버전 중앙화 작업을 한다면 이 파일과 개별 csproj 전략을 함께 정리해야 한다.

---

## 14. 인증·비밀·운영 접근 권한 인수인계

맥북으로 옮길 때 소스만 clone해서는 운영 배포 권한이 복원되지 않는다. 다음 접근 권한을
별도로 준비한다.

### 필요한 계정/권한

- GitHub `blossom0948/classroom` push/tag/release 권한
- Cloudflare account의 Worker, Durable Object, Pages 배포 권한
- Firebase project `classroom-production-52ae6`의 Authentication 설정 권한
- 필요 시 NEIS API key 관리 권한
- 필요 시 Resend/API 메일 발신 도메인/secret 관리 권한

### 소스에 포함되는 공개 값과 포함하면 안 되는 비밀

- Firebase web config의 API key는 브라우저용 공개 식별자라 `config.js`에 있다.
- Cloudflare `wrangler.jsonc`의 `FIREBASE_WEB_API_KEY`도 공개 웹 인증 구성 값이다.
- NEIS key, Resend key, 메일 발신 비밀, Cloudflare API token, GitHub PAT, 관리자
  실제 비밀번호는 저장소/문서/스크린샷에 넣지 않는다.
- Worker secret은 다음처럼 새 맥북에서 다시 설정/확인한다.

```bash
npx wrangler secret put NEIS_API_KEY --config cloudflare/wrangler.jsonc
npx wrangler secret put RESEND_API_KEY --config cloudflare/wrangler.jsonc
npx wrangler secret put CLASSROOM_EMAIL_FROM --config cloudflare/wrangler.jsonc
```

### 로그인 관련 주의

- Firebase Auth 승인 도메인에는 최소 `classroom-2en.pages.dev`, `localhost`가 있어야 한다.
- 공개 운영은 bearer session 경로라 로그아웃/세션 저장 코드 변경 시 Google redirect
  회귀 테스트가 필요하다.
- secure-cookie 모드는 코드에 남아 있지만 기본값이 아니다. 이를 다시 기본으로 바꾸려면
  same-site cookie, Pages proxy, Firebase redirect, Safari/학교 브라우저를 포함해 충분히
  테스트해야 한다.

---

## 15. 알려진 한계·기술 부채·추천 다음 작업

### 우선순위 높음

1. **Windows 실기기 수용 테스트 자동화/기록**
   - 현재 CI는 빌드와 self-test는 통과하지만, 실제 WinForms 트레이, Run key, UAC,
     service recovery, screen capture는 학교 PC 또는 Windows VM에서 검증해야 한다.
   - 최소 100%/125%/150%/200% DPI, 1366×768/1920×1080, 1/10/20/30명 화면 벽을
     정식 체크리스트로 만들어야 한다.

2. **화면 보기 실사용 부하 측정**
   - v0.5.35 이후 실제 학생 1대 → 3대 → 10대 → 20대에서 프레임 도착률, 평균 지연,
     CPU, 업로드 대역폭을 계측한다.
   - 20명에서 너무 느리면 무조건 polling 간격만 줄이지 말고 adaptive quality/선택 공유/
     WebRTC 여부를 설계한다.

3. **학생 설치/업데이트 신뢰성**
   - GitHub release asset, Worker proxy, 학교 proxy/SSL inspection 환경을 각각 시험한다.
   - Windows code signing, installer signing, package integrity manifest/hash 검증을 도입하는
     것이 실제 학교 배포 전 중요한 과제다.

4. **인증 세션 정리**
   - bearer token localStorage 경로는 Google 로그인 안정성을 위해 유지 중이다.
   - same-site custom domain과 HttpOnly Secure Cookie 구조가 안정되면 쿠키 모드로
     전환하는 것이 좋다.

### 기능 설계가 필요한 항목

- 개인별 활동 비공개/예외 정책: 누가 무엇을 보지 못하는지, 교사/학생/관리자 기준,
  감사 가능성, 전자칠판 모드를 어떻게 나눌지 먼저 명세가 필요하다.
- 학생 관리자 역할: 현재 표식만 있다. 진짜 권한으로 확장할 경우 교사 계정과 학생
  장치/학생 identity를 섞지 않는 별도 role model이 필요하다.
- AI 위험 판정: 화면/동영상 분석을 도입하기 전에 데이터 최소화, opt-in/고지, 학교
  정책, 오탐 처리, 모델 provider, 비용 상한, 평가 데이터셋을 먼저 정한다.
- 브라우저 domain: 현재 native app은 정확한 도메인을 못 읽는다. managed browser extension
  또는 학교 MDM integration을 설계해야 한다.
- 원격 제어: 현재 제품의 명확한 범위를 넘어선다. 필요하더라도 별도 권한, 학생 표시,
  audit, consent/학교 정책, 안전한 protocol을 처음부터 설계해야 한다.

### 문서 정리 부채

- `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/TESTING.md`,
  `docs/TROUBLESHOOTING.md` 상당 부분은 과거 **Phone Unlock** 제품 설명이다.
- Classroom과 무관한 Credential Provider, Android Phone Unlock, Windows 비밀번호,
  WOL 내용이 섞여 있으므로 이 파일들을 Classroom 공식 운영 문서로 그대로 믿으면 안 된다.
- 현재 Classroom 기준 문서: `README.md`, `docs/CLASSROOM_MVP.md`,
  `docs/DEPLOYMENT.md`, `docs/DEVICE_MANAGEMENT.md`, `scripts/install/README.md`,
  그리고 이 인수인계 문서다.
- 다음 큰 문서 작업 때는 Phone Unlock 문서를 `docs/legacy-phone-unlock/`로 옮기거나,
  제목/링크를 분리해 혼동을 없애는 것을 권장한다.

---

## 16. 장애 재현·점검 순서

### 학생이 온라인인데 화면이 안 보일 때

1. 교사 계정이 read-only 학교 로그인(guest)이 아닌지 확인한다.
2. 활성 수업을 시작했는지 확인한다.
3. 학생 카드가 온라인이고 학생 Desktop/Service 연결 상태가 정상인지 확인한다.
4. 학생 앱 버전이 `0.5.35` 이상인지 상세 pane에서 확인한다.
5. 학생 앱 트레이 `상태 열기`에서 `화면 공유 중`이 표시되는지 확인한다.
6. 15초 이상 기다려도 첫 frame이 없으면 Worker/학생 앱 version mismatch를 의심한다.
7. 운영 API health 확인:

```bash
curl https://classroom-api.blossom0948.cloud/health/ready
```

8. Worker를 수정한 경우 `npm run deploy:worker`를 실제로 실행했는지 확인한다.

### 학생 앱이 재부팅 후 안 열릴 때

1. 학생 PC에서 `Test-ClassroomDeviceReadiness.ps1`을 실행한다.
2. `ClassroomStudentService`가 설치됨/Auto/Running인지 확인한다.
3. `C:\Program Files\Blossom Classroom Student\desktop\Classroom.Student.Desktop.exe` 존재를 확인한다.
4. HKCU/HKLM Run의 `BlossomClassroomStudent` 값을 확인한다.
5. `%ProgramData%\Blossom Classroom Student\desktop-config.json`이 남아 있는지 확인한다.
6. 학생 setup EXE를 다시 실행해 기존 등록을 재사용하도록 복구한다. 무작정 새 학생
   코드를 발급할 필요는 없다.

### 학생 설치 다운로드가 학교망에서 실패할 때

1. 브라우저에서 <https://classroom-2en.pages.dev/student>을 직접 연다.
2. `classroom-api.blossom0948.cloud/downloads/student-setup` 도메인이 학교 필터에서
   허용되는지 확인한다.
3. Worker proxy가 release asset을 Range 방식으로 연결하므로 GitHub release-assets만
   차단된 경우에도 되도록 Classroom 도메인을 우선 사용한다.
4. 그래도 안 되면 학교 proxy/SSL inspection/파일 확장자 정책/Windows SmartScreen을
   IT 담당자에게 확인한다.

### Google 로그인 뒤 랜딩으로 돌아갈 때

1. Firebase 승인 도메인을 확인한다.
2. `config.js`의 public API origin이 운영 Worker domain인지 확인한다.
3. Google redirect completion을 session restore보다 먼저 처리하는 `app.js` 흐름을
   되돌리지 않았는지 확인한다.
4. `v0.5.28` 이후의 auth logic을 기준으로 regression test를 수행한다.

---

## 17. 맥북 첫날 체크리스트

- [ ] GitHub에서 저장소 clone 및 `main`, `v0.5.35` 확인
- [ ] `npm ci`, `.NET 8 SDK`, `npm run test:quality` 실행
- [ ] `npm run build`로 `dist/` 생성 확인
- [ ] Cloudflare `wrangler login` 후 계정/프로젝트 접근 확인
- [ ] `curl https://classroom-api.blossom0948.cloud/health/ready` 확인
- [ ] `curl https://classroom-2en.pages.dev/version.json`이 `0.5.35`인지 확인
- [ ] GitHub Release `v0.5.35`에 3개 asset이 있는지 확인
- [ ] Firebase Auth 도메인/Google provider/Email-Password provider 접근 권한 확인
- [ ] Windows VM 또는 학교 테스트 노트북 한 대를 준비
- [ ] 학생 코드 발급 → setup 설치 → 재부팅 → tray → 메시지 → 화면 보기 → update
  순서의 smoke test를 실행
- [ ] Worker 변경 시 `npm run deploy:worker`가 필요한 점을 팀/본인 작업 흐름에 반영

---

## 18. 최종 권장 작업 순서

맥북으로 옮긴 직후에는 기능을 더 추가하기보다 다음 순서가 안전하다.

1. `v0.5.35` 운영 상태와 학교 테스트 PC 1대에서 화면 보기/X→트레이 동작을 확인한다.
2. 3대, 10대, 20대 화면 보기 부하 테스트 결과를 문서화한다.
3. Phone Unlock 유산 문서를 정리하고 Classroom 운영 문서만 분리한다.
4. 버전 문자열을 한 곳에서 관리하는 release script를 만든다.
5. Windows 코드 서명·MDM 배포·Intune 정책을 학교 IT와 확정한다.
6. 그 다음에야 개인별 활동 비공개, 학생 관리자 실제 role, AI 분류 같은 큰 기능을
   개인정보/권한 명세와 함께 설계한다.

이 순서를 따르면, 이미 배포된 학생 설치/업데이트/화면 보기 경로를 흔들지 않으면서
맥북 환경에서도 안정적으로 다음 개발을 이어갈 수 있다.
