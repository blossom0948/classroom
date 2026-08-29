# Classroom migration inventory

작성일: 2026-08-28
대상 저장소: 'blossom0948/classroom'
기준 커밋: '4d764f7' ('v0.4.0-beta.23')
기준 태그: 'phone-unlock-import-baseline' (로컬 태그)

## 0. Phase A 기준선 범위

사용자 요청은 'classroom' 저장소를 학교용 제품으로 전환하는 것이며,
'windowslogin' 저장소는 수정하지 않는 것이다. 첨부된 마스터 플랜의 최초
실행 지시는 Phase A로 한정되어 있었다. 해당 기준선 조사를 먼저 완료한 뒤,
최신 사용자 지시에 따라 Classroom P0 서비스 구현까지 이 저장소에서 계속
진행했다.

- 현재 구조·의존성·서비스·프로토콜·설치·업데이트·감사·네트워크 코드 조사
- 기준선 빌드와 현재 self-test 실행
- 'KEEP / REFACTOR / REMOVE_LATER / UNKNOWN' 분류
- Classroom 재사용 경계와 Phase B의 구체적인 작업 순서 기록
- 빌드 환경, 기술 부채, 보안·학교 배포 위험 기록

Student Agent, Teacher Console, Classroom Server의 대규모 구현과 기존
Phone Unlock 코드 삭제는 당시 Phase A에서는 수행하지 않았다.

## 1. 기준선 요약

| 항목 | 결과 |
| --- | --- |
| 원격 저장소 | 'https://github.com/blossom0948/classroom.git' |
| 현재 브랜치 | 'main' (origin과 동기화된 상태로 clone) |
| 현재 제품 | Android Phone Unlock + Windows Phone Unlock 복제본 |
| Classroom 코드 | 없음 (Classroom.* , Server, Teacher Console, 학생 관리 모델 없음) |
| 추적 파일 | 217개 (android 71, windows 107, docs 28 등) |
| 기준 태그 | 'phone-unlock-import-baseline' |
| 원본 저장소 변경 | 없음. 이 작업은 'C:\Users\bloss\Downloads\classroom'에서만 수행 |

현재 솔루션의 주요 코드 규모는 다음과 같다.

| 영역 | 파일 수 | 대략적 라인 수 | 현재 역할 |
| --- | ---: | ---: | --- |
| 'windows/PhoneUnlock.Service' | 42 | 4,568 | LocalSystem Windows Service, HTTPS/WSS, IPC, 인증·원격 제어 |
| 'windows/PhoneUnlock.Setup' | 10 | 2,394 | WPF 설정·페어링·자격 증명·진단 UI |
| 'windows/PhoneUnlock.CredentialProvider' | 13 | 1,374 | 네이티브 Windows 로그인 타일 |
| 'android/PhoneUnlock/app' | 64 | 5,493 | Android 생체인증·페어링·PC 원격 제어 |
| 'windows/PhoneUnlock.Core' | 19 | 453 | 메시지 모델, canonical payload, challenge·서명 검증 |
| 'windows/PhoneUnlock.Agent' | 5 | 472 | 로그인 사용자 세션 트레이, 자동 잠금, Bluetooth/presence |
| 'windows/PhoneUnlock.*.Tests' | 4 | 352 | 실행형 Core/Service self-test |

## 2. 현재 의존성·구조

### 2.1 솔루션과 프로젝트

'PhoneUnlock.sln'에 포함된 프로젝트는 다음과 같다.

~~~text
PhoneUnlock.Core                 net8.0
PhoneUnlock.Desktop              net8.0-windows, WPF
PhoneUnlock.Service              net8.0-windows, ASP.NET Core Web SDK
PhoneUnlock.Setup                net8.0-windows, WPF
PhoneUnlock.Agent                net8.0-windows, Windows Forms
PhoneUnlock.Core.Tests           net8.0, executable self-test
PhoneUnlock.Service.Tests        net8.0-windows, executable self-test
~~~

의존성 방향은 대체로 'Service/Setup/Desktop -> Core',
'Service.Tests -> Service', 'Core.Tests -> Core'이다. 네이티브
'windows/PhoneUnlock.CredentialProvider/*.vcxproj'와 Android Gradle
프로젝트는 솔루션에 포함되지 않고 CI에서 별도 빌드된다. Installer도
프로젝트가 아니라 PowerShell/Inno Setup 스크립트 묶음이다.

### 2.2 현재 서버·통신 모델

- 중앙 Classroom Server는 없다.
- 'PhoneUnlock.Service/Program.cs'가 단일 PC에서 Kestrel을
  'ListenAnyIP'로 열고 자체 서명 인증서를 사용한다.
- Android가 '/pair'에서 1회용 pairing token으로 등록된 뒤,
  'wss://<host>:48231/ws?phoneId=...'에 Bearer device token으로 연결한다.
- 'PhoneConnection'과 'PhoneConnectionRegistry'가 Android 연결, heartbeat,
  인증 응답, 원격 잠금/전원/deck 메시지를 직접 처리한다.
- 'SecureNamedPipe', 'AgentPipeService', 'AuthPipeService',
  'SetupPipeService'가 Service와 로그인 사용자/설정 앱/Credential Provider를
  연결한다.

따라서 현재 구조에서 재사용 가능한 것은 인증된 연결·재연결·장치 상태·IPC의
기술 패턴이고, Classroom의 권한·학교·학급·수업 세션을 처리하는 서버
경계는 새로 설계해야 한다.

### 2.3 현재 상태·감사·저장

- 'ServiceConfiguration'은 한 PC의 'ComputerId', 등록 휴대폰, 선호 휴대폰,
  자동 잠금/presence/원격 제어 설정만 저장한다.
- 'ConfigurationStore'는 로컬 JSON 파일을 원자적 교체 방식으로 저장한다.
- 'AuditLogStore'는 로컬 JSONL을 최대 2,000건 유지하며 pairing,
  authentication, connection 및 Credential Provider 관련 이벤트를 기록한다.
- 현재 'AuditEntry'에는 학교/학급/수업 세션/교사/학생/교사 장치/학생 장치
  식별자가 없다. Classroom에서는 계획의 감사 이벤트 스키마로 확장해야 한다.
- 'PhoneUnlock.Service/Storage/WindowsCredentialStore.cs'는 Windows
  계정의 사용자명·비밀번호를 Windows Credential Manager에 저장하고,
  'AuthPipeService'가 승인 뒤 Credential Provider에 반환한다. 이 경로는
  Classroom에 재사용하지 않는다.

### 2.4 Android·설치·업데이트

- Android 앱은 'com.example.phoneunlock', 'PhoneUnlock' 이름과 Phone Unlock
  생체인증/위젯/원격 PC 제어 기능을 가진 독립 Gradle 앱이다.
- 'windows/PhoneUnlock.Installer'는 PhoneUnlockService 등록, 방화벽 규칙,
  Credential Provider 레지스트리, 복구·제거를 관리한다.
- '.github/workflows/ci.yml'는 Windows .NET 8 build, x64 Credential Provider,
  Inno Setup, Android release APK를 별도 빌드한다.
- 'update.json', 'ReleaseUpdateService.cs', Android
  'ReleaseUpdateChecker.kt'가 'blossom0948/windowslogin'의 manifest/release를
  가리킨다. Classroom 업데이트 채널과 섞이면 안 된다.

## 3. KEEP / REFACTOR / REMOVE_LATER / UNKNOWN

분류는 "현재 파일을 당장 삭제할지"가 아니라 Classroom 전환 때의 처리
방향이다. Phase A에서는 기존 코드를 보존한다.

| 경로/구성요소 | 분류 | 판단 및 다음 경계 |
| --- | --- | --- |
| 'windows/PhoneUnlock.Core/Security/ChallengeGenerator.cs' | KEEP | 난수 challenge·만료시간 생성 패턴을 Classroom.Core 후보로 보존 |
| 'windows/PhoneUnlock.Core/Security/SignatureVerifier.cs' | REFACTOR | 장치/명령 서명 검증 패턴은 재사용하되 Classroom 프로토콜로 분리 |
| 'windows/PhoneUnlock.Core/Security/ChallengeStore.cs' | REFACTOR | replay/expiry 원자 소비 모델을 공통 코어로 추출하고 도메인 이름 제거 |
| 'windows/PhoneUnlock.Core/Security/CanonicalPayload.cs' | REFACTOR | canonical serialization 원칙은 유지하되 Phone Unlock prefix/필드는 재설계 |
| 'windows/PhoneUnlock.Core/Protocol/ProtocolJson.cs' | KEEP | UTF-8 compact JSON 옵션·검증 패턴 후보 |
| 'windows/PhoneUnlock.Core/Models/ProtocolEnvelope.cs' | REFACTOR | 공통 envelope 후보. Classroom message type/version 규격으로 분리 |
| 'windows/PhoneUnlock.Core/Models/Auth*', 'Remote*' | REMOVE_LATER | Phone 로그인/잠금/전원 전용 모델. Classroom 명령 모델로 직접 확장하지 않음 |
| 'windows/PhoneUnlock.Service/Networking/PhoneConnection*' | REFACTOR | authenticated connection, heartbeat, reconnect, ACK 대기 패턴을 Student/Server transport로 재설계 |
| 'windows/PhoneUnlock.Service/Security/CertificateManager.cs' | REFACTOR | TLS certificate/fingerprint와 주소 선택 아이디어는 후보; WOL·단일 PC 가정은 분리 |
| 'windows/PhoneUnlock.Service/Storage/AuditLogStore.cs' | REFACTOR | append-only/동시성/파일 권한 패턴은 후보; 중앙 감사·보존 정책·계획 스키마 필요 |
| 'windows/PhoneUnlock.Service/Storage/ConfigurationStore.cs' | REFACTOR | 원자 저장 패턴은 후보; 학교/사용자/학급/세션/장치 registry로 대체 |
| 'windows/PhoneUnlock.Service/Pipes/SecureNamedPipe.cs' | REFACTOR | 제한된 로컬 IPC ACL 패턴은 Student Desktop IPC 후보; 역할 권한과 protocol version 추가 |
| 'windows/PhoneUnlock.Service/Pipes/AgentPipeService.cs' | REFACTOR | 서비스-트레이 분리·상태 전달·재연결 구조는 Student Agent 후보 |
| 'windows/PhoneUnlock.Service/Pipes/Agent*' | REFACTOR | authenticated local state/notification channel 후보; RSSI/deck/presence는 선별 제거 |
| 'windows/PhoneUnlock.Service/Security/PairingCoordinator.cs' | REFACTOR | 1회용 등록 token·공개키 검증·device token hash 아이디어 후보; 학교 device enrollment로 재설계 |
| 'windows/PhoneUnlock.Service/Storage/WindowsCredentialStore.cs' | REMOVE_LATER | 학생 Windows 암호 저장 금지. Classroom 코드가 의존하지 않도록 분리 |
| 'windows/PhoneUnlock.Service/Pipes/AuthPipeService.cs' | REMOVE_LATER | Credential Provider와 비밀번호 반환 전용 |
| 'windows/PhoneUnlock.Service/Security/Remote*', 'Interop/Remote*' | REMOVE_LATER | Phone Unlock 원격 잠금/해제/전원 전용; 교사 임의 shell로 변형하지 않음 |
| 'windows/PhoneUnlock.Service/Security/Proximity*', 'Pipes/Proximity*' | REMOVE_LATER | 개인용 proximity unlock/센서 자동화 전용 |
| 'windows/PhoneUnlock.Service/Storage/WindowsSecretStore.cs' | UNKNOWN | 일반 비밀 저장 패턴은 검토하되 학생 credential 저장소로 사용 금지; Server secret 정책 결정 후 분리 |
| 'windows/PhoneUnlock.Agent' | REFACTOR | 학생에게 보이는 트레이/알림/IPC 수명주기 후보; 자동 잠금·Bluetooth·deck 기능은 재평가 |
| 'windows/PhoneUnlock.Setup' | REFACTOR | 관리자 설치·진단·업데이트 UI 경험 후보; Teacher Console로 직접 확장하지 않음 |
| 'windows/PhoneUnlock.Desktop' | REMOVE_LATER | 네트워크 없는 수동 canonical/signature 개발 harness; Classroom Teacher Console이 대체 |
| 'windows/PhoneUnlock.CredentialProvider' | REMOVE_LATER | Windows 로그인 전용 네이티브 DLL. Phase A에서 삭제하지 않으며 Classroom Agent와 결합 금지 |
| 'android/PhoneUnlock' | REMOVE_LATER | 학교 MVP의 필수 구성 아님. 별도 교사용 모바일 앱으로 억지 변형하지 않음 |
| 'windows/PhoneUnlock.Core.Tests' | REFACTOR | 공통 crypto/protocol test로 이동·확장 |
| 'windows/PhoneUnlock.Service.Tests' | REFACTOR | enrollment, connection, ACK, authorization, audit 통합 테스트로 확장 |
| 'windows/PhoneUnlock.Installer' | REFACTOR | 설치/복구/업데이트 경험은 후보; Classroom service identity·권한·서명·채널로 재작성 |
| 'docs', 'protocol', 'scripts', '.github/workflows/ci.yml' | REFACTOR | 운영 지식과 CI 골격은 보존하되 Phone Unlock 명칭·URL·배포 대상 분리 |
| 'README.md', 'update.json', release notes | REFACTOR | 제품 문서/업데이트 주소를 Classroom 전용으로 교체 |
| 'THIRD_PARTY_NOTICES.md' | KEEP | 라이선스 고지는 유지하고 신규 Classroom 의존성 추가 시 갱신 |

## 4. 재사용하지 않을 Phone Unlock 전용 경계

다음은 Classroom의 새 코드가 참조하지 않아야 한다.

- Credential Provider COM/LogonUI 구현과 'AuthPipeService'
- 'WindowsCredentialStore'의 Windows 비밀번호 저장·반환 흐름
- Android 생체인증 앱, widget, Quick Settings tile, PC 원격 제어
- proximity unlock, Bluetooth RSSI 기반 개인용 자동 잠금, SmartThings/Home Assistant 연동
- Phone Unlock의 remote lock/unlock/power/deck 명령
- 'PhoneUnlock' 서비스 이름, Credential Provider GUID, registry path,
  update URL, 'PhoneUnlock/WindowsLogon' credential target

반대로 다음 패턴만 의존성·보안 검토 후 'Blossom.Classroom.*'로 단계적으로
옮긴다.

- challenge/expiry/replay와 고정 시간 비교
- P-256/X.509 서명 검증 및 제한된 device token 저장
- WSS/TLS certificate pinning과 연결 재시도
- bounded channel, ACK 대기, connection registry
- JSONL 감사 append/lock/파일 ACL 패턴
- setup/diagnostics/recovery 문서와 self-contained Windows publish 경험

## 5. Phase A 기준선 검증

### 5.1 Windows build

명령:

~~~powershell
dotnet build PhoneUnlock.sln --configuration Release
~~~

결과: **실패 (exit 1)**

- 호스트에 설치된 SDK는 '6.0.410' 하나뿐이다.
- 저장소 프로젝트는 'net8.0', 'net8.0-windows'를 대상으로 한다.
- 솔루션의 7개 .NET 프로젝트에서 'NETSDK1045'가 발생했다.
- 호스트에는 별도의 'msbuild' 명령도 없었다. 네이티브 프로젝트는 Visual
  Studio MSBuild가 필요하다.

이는 코드 기준선의 실패로 기록하며 Phase A에서 target framework를 낮추거나
코드를 수정해 숨기지 않는다. CI는 'actions/setup-dotnet@v4'로 .NET 8을
설치하고 'microsoft/setup-msbuild@v2'를 사용한다.

### 5.2 현재 self-test

저장소의 테스트가 test runner 프로젝트가 아닌 실행형 console self-test이므로
다음 경로를 실행했다.

~~~powershell
dotnet run --project windows/PhoneUnlock.Core.Tests/PhoneUnlock.Core.Tests.csproj --configuration Release
dotnet run --project windows/PhoneUnlock.Service.Tests/PhoneUnlock.Service.Tests.csproj --configuration Release
~~~

결과: **둘 다 실행 전 실패 (exit 1)**

- Core self-test는 참조하는 'net8.0' 프로젝트에서 'NETSDK1045'로 중단됐다.
- Service self-test는 참조하는 'net8.0-windows'/Core 프로젝트에서
  'NETSDK1045'로 중단됐다.
- 따라서 이번 호스트에서는 PASS 개수를 주장하지 않는다.

테스트 코드 자체는 Core 9개(난수 challenge, canonical bytes, DER ECDSA,
tamper/replay/expiry, pairing token, remote power 제한), Service 5개(token,
pairing, invalid key, audit, rate limit)이며, Classroom 기능 테스트는 아직
없다.

### 5.3 Android/native/package build

- 'android/PhoneUnlock/gradlew.bat :app:assembleDebug --no-daemon'은
  'JAVA_HOME is not set and no java command could be found'를 출력했다.
- 호스트에서 'java', 'gradle', 'adb', 'msbuild', 'vswhere'를 찾지 못했다.
- Android build wrapper가 이 환경 오류 뒤 exit code 0을 반환했으므로, exit
  code만으로 성공으로 판정하지 않는다. APK 산출물은 생성되지 않았다.
- 네이티브 Credential Provider와 Inno Setup은 각각 Visual Studio C++/Windows
  SDK와 Inno Setup compiler가 필요하며 이 호스트 기준선에서는 실행하지 못했다.

## 6. 기술 부채·보안·학교 배포 위험

### 기술 부채

1. 제품 식별자·namespace·서비스 이름·포트·설정 경로가 Phone Unlock에
   강하게 결합되어 있다. 전체 문자열 치환으로 전환하면 installer/registry/
   service identity가 깨질 수 있다.
2. 현재는 중앙 서버, 영속 DB, 역할/권한, 학급/수업 세션, device revoke,
   command ACK 모델이 없다.
3. WSS 연결은 Android 단말과 단일 PC를 위한 모델이며 30대 Student Agent와
   한 Teacher Console을 위한 fan-out/backpressure 구조가 아니다.
4. 화면 캡처, thumbnail, live view, foreground app/domain activity 모델과
   privacy/retention 정책이 없다.
5. 테스트가 console self-test 중심이고 Server/Agent/installer/UI/실제
   학교 네트워크의 통합 테스트가 없다.

### 보안·프라이버시

1. 현재 'ListenAnyIP'/자체 서명 certificate/fingerprint pinning은 개인용
   LAN/VPN 모델이다. Classroom에서는 중앙 인증, 역할별 authorization,
   device enrollment/revoke와 서버 측 권한 검사가 필요하다.
2. 현재 감사 이벤트는 Phone Unlock 필드에 맞춰져 있어 계획의
   'schoolId/classId/sessionId/teacherId/studentId/requestId/result'를
   보장하지 않는다.
3. Classroom은 Windows password, PIN, cookie, key input, raw biometric,
   숨은 화면 수집을 저장하거나 수집하지 않아야 한다.
4. 화면 보기/활동 수집은 수업 세션·최소 데이터·학생 표시 indicator·보존
   기간·감사 로그를 함께 설계해야 한다.

### 학교 PC 배포

1. Student Service와 학생에게 보이는 Desktop UI를 LocalSystem/interactive
   session으로 분리해야 한다. 기존 Credential Provider 경로를 재사용하면
   제품 경계와 복구 위험이 커진다.
2. 현재 설치 스크립트는 Phone Unlock registry/GUID/firewall/service를
   변경한다. Classroom installer는 별도 service name, install root,
   firewall scope, signed binary, rollback을 가져야 한다.
3. 현재 update manifest가 'windowslogin' release를 가리키므로 Classroom
   패키지에서 반드시 분리한다.
4. 운영 배포 전에 3대 → 10대 → 30대 순서로 연결·재접속·서버 재시작·명령
   ACK·정책 종료·복구를 검증해야 한다.

## 7. Phase B 구체 계획

Phase A 결과를 반영한 다음 순서로 작은 변경 단위로 진행한다.

1. 'Classroom.sln'을 새로 만들고 기존 'PhoneUnlock.sln'은 기준선으로
   보존한다. 'src/', 'tests/', 'docs/', 'browser/', 'scripts/' 디렉터리와
   'Blossom.Classroom.*' namespace 규칙을 먼저 확정한다.
2. 'Classroom.Core'를 추가한다. JSON 옵션, ID/시간·validation,
   challenge/replay 및 감사의 공통 primitive만 이동하고
   'Auth*'/Windows credential 타입은 이동하지 않는다.
3. 'Classroom.Protocol'을 추가한다. versioned envelope, device enrollment,
   heartbeat/status snapshot, command request/ACK/result, error와 request ID를
   정의한다. Phone Unlock 'AUTH_APPROVED'와 remote power payload는 그대로
   재사용하지 않는다.
4. 'Classroom.Core.Tests'와 'Classroom.Protocol.Tests'를 test runner로
   구성하고 canonical serialization, expiry/replay, malformed input,
   message size, ACK correlation을 먼저 고정한다.
5. 기존 Service에서 연결·TLS·재시도·device token hash·bounded channel의
   구현을 별도 커밋으로 검토해 새 transport 계층에 복사/추출한다. 이 단계에서
   Credential Provider, Android, Windows password storage에 대한 project
   reference가 생기지 않도록 한다.
6. 'Classroom.Student.Service'와 제한된 'Classroom.Student.Desktop'의
   빈 골격만 추가한다. 첫 동작은 등록된 device ID, heartbeat, connection
   status이며 화면 캡처·원격 shell·password 흐름은 포함하지 않는다.
7. 'Classroom.Server'의 개발용 ASP.NET Core 경계를 추가한다. 처음에는
   dev DB/in-memory adapter를 사용하되 auth/role/school/class/session/device
   모델과 server-side authorization을 먼저 둔다.
8. 각 단계마다 .NET 8 SDK/Visual Studio가 있는 CI에서 build와 self-test를
   실행하고, 로컬에서는 동일한 명령을 문서화한다. 이후에야 Teacher Console,
   command ACK, browser extension, thumbnail, live view 순서로 진행한다.

## 8. Phase A 완료 상태

- [x] 저장소 clone 및 전체 주요 구조 조사
- [x] 'phone-unlock-import-baseline' 로컬 태그 생성
- [x] Windows solution build 시도 및 'NETSDK1045' 기록
- [x] Core/Service self-test 시도 및 환경 실패 기록
- [x] Android build prerequisite 실패 기록
- [x] 주요 프로젝트·폴더 KEEP/REFACTOR/REMOVE_LATER/UNKNOWN 분류
- [x] Classroom 재사용 경계와 Phase B 계획 기록
- [x] 'windowslogin' 저장소 변경 없음
- [x] 'MIGRATION_INVENTORY.md' 생성
- [x] .NET 8/Visual Studio/JDK 17 환경에서 성공 기준선 재실행
- [x] Phase B 착수 (초기 vertical slice 진행 중)

Phase A 당시에는 소스 코드·솔루션·설치 스크립트의 기존 동작을 변경하지
않았고, 기준 태그는 로컬에만 생성되어 원격으로 push하지 않았다. 이후 사용자
지시에 따라 이 저장소에만 Classroom 구현을 추가했다. 기존 Phone Unlock
솔루션은 보존했으며 `windowslogin` 저장소는 수정하지 않았다.

## 9. Phase B 및 초기 서비스 구현 진행

현재 `Classroom.sln`에 다음 프로젝트가 추가되어 있다.

- `src/Classroom.Core`: 공통 보안·직렬화·감사 primitive
- `src/Classroom.Protocol`: versioned device/command protocol과 validation
- `src/Classroom.Server`: SQLite 영속 ASP.NET Core 서버, teacher scope
  authorization, bounded command routing, audit API, 정적 Teacher Console
- `src/Classroom.Student.Service`: Windows Service 호스팅 경계, bearer device
  authentication, WebSocket reconnect, Desktop IPC, hello/heartbeat, command
  ACK/result
- `src/Classroom.Student.Desktop`: 학생에게 보이는 WinForms UI 및 Windows 상태
  provider
- `tests/Classroom.Core.Tests`, `tests/Classroom.Protocol.Tests`,
  `tests/Classroom.Server.Tests`, `tests/Classroom.Student.Service.Tests`: 단위/
  통합 self-test
- `scripts/install`: Classroom Student Service/Desktop 설치·제거

초기 end-to-end 검증은 다음 흐름으로 통과했다.

`enrollment ticket → one-use device enrollment → class session → student
WebSocket hello/heartbeat → teacher status query → message command queue →
student ACK/result → audit`

Desktop이 연결된 경우 command sink가 실제 apply 결과를 ACK/result로 전달하고,
연결되지 않은 경우 `STUDENT_DESKTOP_OFFLINE`으로 실패를 기록한다. 화면 캡처·
숨은 입력·원격 shell은 도입하지 않았다.

## 10. 현재 구현 상태 (2026-08-29)

최신 사용자 지시에 따라 다음 P0 범위를 구현했다.

- `src/Classroom.Student.Desktop`: 학생에게 보이는 WinForms Desktop과
  authenticated named-pipe IPC
- `src/Classroom.Student.Desktop/Status/WindowsStudentStatusProvider.cs`:
  foreground process, 표시용 앱 이름, 배터리, 네트워크 상태 제공
- `src/Classroom.Student.Service/Desktop/DesktopStatusBridge.cs`:
  Service-Desktop hello/status/command-result 연결, token 검증, 재접속 경계
- `src/Classroom.Server/Storage/ClassroomDatabase.cs`: SQLite WAL 영속
  스키마 및 users/classes/students/tickets/devices/sessions/commands/audit/
  teacher sessions 저장·복원
- `src/Classroom.Server/Security`: PBKDF2 교사 비밀번호, bearer teacher
  session, login rate limit, class 범위 authorization
- `src/Classroom.Server/Program.cs`: 직접 Kestrel TLS 또는 TLS terminated
  proxy 강제, HTTP API 및 WSS 학생 연결
- `src/Classroom.Server/wwwroot`: login, class/session, student grid,
  command, audit, settings를 포함한 Teacher Console
- `scripts/install`: `ClassroomStudentService`와 사용자 Desktop을 Phone
  Unlock 경로와 분리해 설치/제거

현재 검증된 주요 흐름은 다음과 같다.

`teacher login → class/session → enrollment ticket → one-use device
enrollment → Student Service WSS → Desktop IPC status → Teacher student
grid → Message/URL/Focus command → Desktop apply → ACK/result → audit`

SQLite 재시작 후 session/enrollment/device 상태 복원과 교사 session revoke,
Student Service/Desktop named-pipe status/command correlation 테스트도
통과했다.

아직 남은 후속 범위는 계획서 Phase G 이후의 browser extension 기반 domain,
thumbnail/live view, 정책 catalog와 revoke/rotate 관리자 UI, signed installer/
updater, 다중 학교/교사 관리, 실제 학교 파일럿 운영 문서와 부하 검증이다.
따라서 현재 산출물은 실제 연결을 시험할 수 있는 승인된 파일럿 버전이며,
전체 학교 배포 전에는 운영 인증서·비밀 저장소·백업·관리 기기 정책을 별도로
확정해야 한다.

기존 `PhoneUnlock.sln`과 `windowslogin` 저장소는 보존되었고, Classroom 새
프로젝트에는 Credential Provider, Android Phone Unlock, Windows password
storage project reference가 없다.
