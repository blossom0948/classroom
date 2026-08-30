# Classroom 운영 배포

## 현재 운영 구성

```text
Teacher Console / Student Desktop
        │ HTTPS / WSS
        ▼
classroom-2en.pages.dev
  ├─ 정적 Teacher Console · PWA
  └─ Pages Function proxy
        │ HTTPS / WSS
        ▼
classroom-api.blossom0948.cloud
  └─ Cloudflare Worker + Durable Object SQLite
```

공개 서비스의 API와 영속 데이터는 Cloudflare에서 실행된다. 따라서 선생님 PC,
로컬 Windows 서비스, Cloudflare Tunnel이 꺼져 있어도 `classroom-2en.pages.dev`의
로그인·수업·학생 코드·장치 상태 API는 중단되지 않는다. 콘솔의 운영 설정은
`config.js`에서 `classroom-api.blossom0948.cloud`로 직접 연결한다.

## 1. Cloudflare Worker 운영

운영 백엔드 소스는 `cloudflare/worker.js`, 설정은
`cloudflare/wrangler.jsonc`에 있다. Durable Object의 SQLite가 교사, 수업,
학생 코드, 장치와 감사 기록을 영속 보관한다.

```powershell
npm install
npm run check:worker
npm run deploy:worker
```

Worker에는 다음 두 주소가 연결되어 있다.

```text
https://classroom-api.blossom0948.cloud       # 운영용 고정 주소
https://classroom-api.blossom0948.workers.dev # Cloudflare 기본 주소
```

학교 선택은 교육부 NEIS 학교 기본정보 API에서 실제 학교를 검색한다. 배포 후
Worker secret에 발급받은 NEIS 인증키를 넣어야 초기 설정의 학교 검색이 활성화된다.
Resend 같은 메일 발송 서비스의 발신 도메인을 준비한 뒤에는 비밀번호 확인 코드용
`RESEND_API_KEY` secret과 `CLASSROOM_EMAIL_FROM` 변수를 함께 설정한다. 키를 소스나
브라우저 설정에 넣지 않는다.

```powershell
npx wrangler secret put NEIS_API_KEY --config cloudflare/wrangler.jsonc
npx wrangler secret put RESEND_API_KEY --config cloudflare/wrangler.jsonc
npx wrangler secret put CLASSROOM_EMAIL_FROM --config cloudflare/wrangler.jsonc
```

`CLASSROOM_EMAIL_FROM`은 메일 공급자에서 인증한 발신 주소(예:
`Classroom <no-reply@학교의-인증-도메인>`)를 사용한다. 이 설정이 없는 동안에도
Google·이메일 로그인과 Firebase 비밀번호 재설정은 사용할 수 있지만, 콘솔의
비밀번호 변경 확인 코드 발송은 안전하게 비활성화된다.

새 코드를 배포한 뒤에는 다음을 확인한다.

```powershell
Invoke-RestMethod https://classroom-api.blossom0948.cloud/health/ready
```

`status: ready`와 `database: durable-object-sqlite`가 반환되어야 한다.

## 2. Firebase Authentication 설정

Classroom 콘솔은 Firebase Authentication으로 이메일/비밀번호 가입과 Google
로그인을 처리한다. 현재 웹 앱 설정은
`src/Classroom.Server/wwwroot/config.js`에 있고, Google provider와
Email/Password provider를 모두 활성화해야 한다.

Firebase Authentication의 승인 도메인에는 최소 다음 주소를 등록한다.

```text
classroom-2en.pages.dev
localhost
```

Google 로그인은 학교 환경에서 popup 차단을 피하기 위해 redirect 방식으로
진행한다. 로그인 이후에는 초기 설정 창에서 교사 이름·과목·기본 반을 입력하며,
언제든 설정 화면에서 바꿀 수 있다.

## 3. Cloudflare Pages 설정

`classroom-2en` Pages project의 Git production branch는 `main`이다.

| 설정 | 값 |
| --- | --- |
| Root directory | 저장소 root |
| Build command | `npm run build` |
| Build output directory | `dist` |
| Production environment variable | `CLASSROOM_BACKEND_ORIGIN=https://classroom-api.blossom0948.cloud` |

이 변수는 Pages Function이 `/auth`, `/api`, `/health`, `/ws/student` 요청을
Cloudflare Worker로 보낼 때 사용하는 선택적 프록시 설정이다. 운영 콘솔은
고정 API 주소로 직접 연결하므로 Git 연동이나 직접 Pages 배포에서 이 변수 누락이
로그인 자체를 막지 않는다. Pages Function 프록시를 사용할 경우에는 값을 반드시
설정한다. `main`에 push하면 Pages가 정적 콘솔·PWA를 다시 빌드하여 자동 배포한다.

## 4. 배포 검증

아래 요청이 모두 성공해야 운영 가능 상태다.

```powershell
Invoke-RestMethod https://classroom-api.blossom0948.cloud/health/ready
Invoke-RestMethod https://classroom-2en.pages.dev/health/ready
Invoke-WebRequest https://classroom-2en.pages.dev/
```

기대 결과:

- `/` → 200, Teacher Console HTML 및 PWA manifest
- 두 `/health/ready` → `status: ready`, `database: durable-object-sqlite`
- 이메일 가입·Google 로그인 → Firebase session 교환 → Classroom bearer token 발급
- 학생 코드는 재발급하기 전까지 유지되며, 재발급하면 기존 학생 장치 token은 해제
- Student Setup 기본 서버 → `https://classroom-api.blossom0948.cloud`
- Student Service → `/ws/student?deviceId=...`에서 WebSocket 연결

## 5. 개인정보 및 운영 준비

콘솔에는 이용약관과 개인정보 처리 안내를 제공한다. 실제 학교 도입 전에는 학교가
개인정보 처리자 연락처, 보관 기간, 담당자, 교육청·학교 내부 규정에 맞는 문구를
확정해야 한다. 운영 계정은 관리자 화면에서 다른 교사를 관리자로 지정할 수 있다.

## 6. 로컬 .NET 서버는 선택적 개발/복구 수단

`Classroom.Server`와 Windows 서비스 설치 스크립트는 개발·사내망 전용 운영 또는
복구 실험을 위해 유지한다. 공개 Pages 서비스는 더 이상 그 PC나 기존 Tunnel에
의존하지 않는다. 로컬 서버를 별도로 운용할 경우에는 HTTPS reverse proxy와
영속 볼륨을 사용하고, 공개 Pages의 `CLASSROOM_BACKEND_ORIGIN`을 임의로
로컬 주소로 바꾸지 않는다.
