# Troubleshooting

## .NET 8 SDK를 찾을 수 없음

`dotnet --list-sdks`에 8.x가 있어야 한다. 런타임만 설치되어 있으면 빌드할 수 없다.

## Android 빌드가 JDK를 찾지 못함

AGP 9.0.1은 JDK 17과 Gradle 9.1.0이 필요하다. `JAVA_HOME`을 JDK 17 폴더로 설정하고 새 터미널에서 다시 실행한다.

## 생체인증 사용 불가

기기에 PIN/패턴 같은 보안 잠금과 강한 생체인증을 먼저 등록한다. 기기가 `BIOMETRIC_STRONG`을 제공하지 않으면 앱은 로그인을 승인하지 않는다.

## Key permanently invalidated

생체정보를 추가/삭제하면 기존 키가 무효화될 수 있다. Android 앱에서 PC 연결을 해제한 뒤 Windows 설정 앱이 만든 새 페어링 정보로 다시 연결한다.

## PC 연결 실패

- PC와 Android가 같은 로컬 네트워크인지 확인한다.
- Windows 네트워크 프로필이 Public이면 LocalSubnet 방화벽 규칙이 적용되지 않으므로 Private 네트워크에서 테스트한다.
- 연결 QR은 생성 후 2분 안에 사용해야 하며 한 번만 사용할 수 있다.
- 공유기 AP isolation/게스트 Wi-Fi가 켜져 있으면 기기 간 통신이 차단될 수 있다.
- PC IP가 바뀐 경우 기존 연결을 해제하고 다시 페어링한다.

## 연결 QR 버튼을 눌러도 아무것도 나오지 않음

설정 앱 실행 파일만 직접 열고 서비스 설치를 건너뛰면 QR을 만들 수 없다. 최신 릴리스 ZIP 전체를 푼 뒤 **Phone Unlock 설치.cmd**를 실행한다. 최신 설정 앱은 이 상태를 감지해 상단에 **이 PC에 설치** 버튼을 표시한다.

## Windows 암호를 확인하지 못함

계정은 현재 로그인 사용자로 자동 선택된다. Windows Hello PIN이 아니라 계정의 실제 암호를 입력해야 한다. Microsoft 계정이면 Microsoft 계정 암호, 로컬 계정이면 로컬 계정 암호를 사용한다.

## 로그인 요청 알림이 오지 않음

- Android에서 Phone Unlock 알림 권한과 배터리 백그라운드 실행을 허용한다.
- 앱의 상시 알림이 `PC 이름 · 연결됨`인지 확인한다.
- Android 재부팅 후 제조사 정책이 자동 시작을 막으면 앱을 한 번 직접 연다.
- 설정 앱의 휴대폰 상태가 `오프라인`이면 다시 연결하거나 재페어링한다.

## 서명 검증 실패

- Windows의 최신 요청과 Android에 붙여 넣은 요청이 같은지 확인한다.
- 30초 이내에 승인한다.
- 공개키와 응답 JSON을 줄임 없이 복사한다.
- Base64 문자열에 따옴표나 공백을 추가하지 않는다.

## Windows 로그인 문제

로그인 화면에서 **로그인 옵션**을 눌러 기본 PIN 또는 비밀번호를 사용한다. 로그인 후 관리자 PowerShell에서 다음을 실행한다.

```powershell
& "$env:ProgramFiles\PhoneUnlock\Disable-CredentialProvider.ps1"
```

로그인할 수 없으면 `windows/PhoneUnlock.Installer/RECOVERY.md`의 안전 모드 명령을 사용한다. 설치 스크립트는 Microsoft 기본 공급자를 필터링하지 않는다.
