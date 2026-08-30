# Classroom 운영 배포

## 구성

```text
Teacher / Student
        │ HTTPS / WSS
        ▼
classroom-2en.pages.dev
  ├─ 정적 Teacher Console (dist)
  └─ Pages Function proxy
        │ HTTPS / WSS
        ▼
Classroom.Server
  ├─ ASP.NET Core
  └─ 영속 SQLite volume
```

Cloudflare Pages는 .NET 프로세스나 SQLite 파일을 실행하지 않는다. 이 저장소의
`functions/_middleware.js`가 `/auth`, `/api`, `/health`, `/ws/student`만 별도
원본 서버로 전달한다. 정적 콘솔과 원본 서버가 모두 준비되어야 실제 서비스다.

## 1. Classroom.Server 운영 환경

필수 환경 변수:

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:8080
CLASSROOM_TLS_TERMINATED_BY_PROXY=true
CLASSROOM_DATABASE_PATH=/data/classroom.db
CLASSROOM_BOOTSTRAP_TEACHER_LOGIN=blossom0948
CLASSROOM_BOOTSTRAP_TEACHER_PASSWORD=<12자 이상의 무작위 초기 비밀번호>
CLASSROOM_CONSOLE_ORIGINS=https://classroom-2en.pages.dev
CLASSROOM_FIREBASE_PROJECT_ID=<Firebase project id>
CLASSROOM_FIREBASE_WEB_API_KEY=<Firebase Web API key>
```

서버는 인터넷에 평문 HTTP로 직접 공개하지 않는다. HTTPS reverse proxy 또는
Cloudflare Tunnel만 원본 HTTP 포트에 연결한다. SQLite 경로에는 재시작 후에도
남는 volume을 연결하고 DB, `-wal`, `-shm` 파일을 함께 백업한다.

### Docker

```bash
docker build -t blossom-classroom:0.2.0 .
docker run -d --name classroom --restart unless-stopped \
  -p 127.0.0.1:48240:8080 \
  -v classroom-data:/data \
  -e CLASSROOM_BOOTSTRAP_TEACHER_PASSWORD='<initial-password>' \
  -e CLASSROOM_CONSOLE_ORIGINS='https://classroom-2en.pages.dev' \
  blossom-classroom:0.2.0
```

### Windows 서비스

GitHub Actions의 `Classroom-Windows` 압축을 푼 뒤 관리자 PowerShell에서:

```powershell
.\Install-ClassroomServer.ps1 `
  -PackageRoot . `
  -BootstrapTeacherPassword '<initial-password>' `
  -ConsoleOrigins 'https://classroom-2en.pages.dev'
```

서버는 `127.0.0.1:48240`에만 바인딩된다. Cloudflare Tunnel published
application의 service를 `http://127.0.0.1:48240`으로 설정한다. Windows가
재시작되어도 `ClassroomServer` 서비스가 자동 시작되어야 한다.

## 2. Firebase Authentication 설정

Classroom의 공개 콘솔은 Firebase Authentication으로 신규 교사 계정을 만들고
Google 계정 로그인을 처리한다. Firebase Console에서 별도 Classroom 프로젝트를
사용하고 Authentication의 Email/Password와 Google provider를 활성화한다.
웹 앱의 Firebase config는 `src/Classroom.Server/wwwroot/config.js`에 넣고,
서버에는 같은 프로젝트의 `projectId`와 Web API key를 각각
`CLASSROOM_FIREBASE_PROJECT_ID`, `CLASSROOM_FIREBASE_WEB_API_KEY`로 설정한다.
Web API key는 웹 앱 식별자이며 비밀번호나 서비스 계정 키를 저장하지 않는다.
새 계정은 Firebase ID token을 `/auth/firebase-login`으로 보내고, 서버가 Firebase
Identity Toolkit에서 검증한 뒤 SQLite teacher/session으로 연결한다.

Firebase Authentication의 승인 도메인에는 다음 주소를 등록한다.

```text
classroom-2en.pages.dev
localhost
```

## 3. Cloudflare Pages 설정

`classroom-2en` Pages project의 Git production branch를 `main`으로 설정한다.

| 설정 | 값 |
| --- | --- |
| Root directory | 저장소 root |
| Build command | `npm run build` |
| Build output directory | `dist` |
| Production environment variable | `CLASSROOM_BACKEND_ORIGIN=https://<원본-host>` |

`CLASSROOM_BACKEND_ORIGIN`에는 Pages 주소가 아니라 HTTPS 원본/Tunnel 주소를
넣는다. 끝에 경로를 붙이지 않는다. 이 값이 없으면 proxy는 의도적으로 503
`BACKEND_NOT_CONFIGURED`를 반환한다.

Git integration을 유지하면 `main` push마다 Pages가 `dist`를 다시 만들고
자동 배포한다. 빌드 설정과 산출물 디렉터리에 관한 공식 안내는
[Cloudflare Pages build configuration](https://developers.cloudflare.com/pages/configuration/build-configuration/)과
[Git integration](https://developers.cloudflare.com/pages/get-started/git-integration/)을 참고한다.

## 4. 배포 검증

다음 세 요청이 모두 성공해야 한다.

```powershell
Invoke-RestMethod https://classroom-2en.pages.dev/health
Invoke-RestMethod https://classroom-2en.pages.dev/health/ready
Invoke-WebRequest https://classroom-2en.pages.dev/
```

기대 결과:

- `/` → 200, Teacher Console HTML
- `/health` → `status: running`
- `/health/ready` → `status: ready`, `database: available`
- 로그인 → bearer token 발급
- 신규 이메일 회원가입 또는 Google 로그인 → Firebase session 교환 → bearer token 발급
- 학생 등록 파일의 `serverUrl` → `wss://classroom-2en.pages.dev`
- Student Service → `/ws/student?deviceId=...`에서 101 WebSocket upgrade

브라우저 개발자 도구에서 정적 파일 404, CORS 오류, CSP 오류가 없어야 한다.
교사 로그인 후 수업 시작, 학생 heartbeat, 메시지 ACK/result, 수업 종료 집중
모드 해제를 차례로 확인한다.

## 5. 기존 404의 원인

이 저장소의 Teacher Console은 `src/Classroom.Server/wwwroot`에 있었지만 Pages
project가 그 폴더를 build output으로 사용하지 않았다. 따라서 Git push와
GitHub Actions가 성공해도 Pages에는 root `index.html`이 없어 404가 발생했다.
`npm run build`가 이제 필요한 정적 파일과 `_headers`, `_routes.json`을 `dist`에
만든다.

또한 정적 파일만 보이게 하는 것으로는 서비스가 완성되지 않는다. 로그인,
SQLite, WSS와 명령 routing은 별도 `Classroom.Server` 원본이 계속 실행되어야
한다.

## 6. 복구

- Pages 오류: 마지막 정상 Git commit으로 production deployment를 rollback한다.
- 서버 오류: `/health/ready`와 Windows service/container log를 확인한다.
- DB 복구: 서버를 멈추고 DB, `-wal`, `-shm`을 같은 시점의 백업으로 복원한다.
- 학생 집중 모드: 연결이 60초 이상 끊기면 Desktop fail-safe가 overlay를 해제한다.
- 장치 token 노출: 교사 콘솔에서 장치를 revoke하고 새 등록 파일로 재등록한다.
