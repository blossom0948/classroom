# Troubleshooting

## .NET 8 SDK를 찾을 수 없음

`dotnet --list-sdks`에 8.x가 있어야 한다. 런타임만 설치되어 있으면 빌드할 수 없다.

## Android 빌드가 JDK를 찾지 못함

AGP 9.0.1은 JDK 17과 Gradle 9.1.0이 필요하다. `JAVA_HOME`을 JDK 17 폴더로 설정하고 새 터미널에서 다시 실행한다.

## 생체인증 사용 불가

기본 모드에서는 기기에 PIN/패턴 같은 보안 잠금과 강한 생체인증을 먼저 등록한다. 앱 설정에서 **휴대폰 PIN·강한 얼굴인식도 허용**을 켜면 Android 시스템 PIN/패턴/비밀번호도 사용할 수 있다. 기기가 얼굴인식을 `BIOMETRIC_WEAK`으로만 제공하면 서명 키 보호에 사용할 수 없다.

## Key permanently invalidated

생체정보를 추가/삭제하면 기존 키가 무효화될 수 있다. Android 앱에서 PC 연결을 해제한 뒤 Windows 설정 앱이 만든 새 페어링 정보로 다시 연결한다.

## PC 연결 실패

- PC는 이더넷, Android는 Wi-Fi여도 된다. 두 장치가 같은 공유기/LAN에 있고 휴대폰 주소가 PC의 `192.168.10.x`처럼 같은 서브넷인지 확인한다.
- 방화벽 규칙은 Windows의 Public/Private/Domain 프로필 모두에서 앱 실행 파일의 TCP 48231을 `LocalSubnet`에만 허용한다.
- 연결 QR은 생성 후 2분 안에 사용해야 하며 한 번만 사용할 수 있다.
- 공유기 AP isolation/게스트 Wi-Fi가 켜져 있으면 기기 간 통신이 차단될 수 있다.
- PC IP가 바뀐 경우 기존 연결을 해제하고 다시 페어링한다.

## 연결 QR 버튼을 눌러도 아무것도 나오지 않음

이전 설치는 파일만 복사되고 Windows 서비스 생성 전에 실패할 수 있었다. 최신 릴리스의 **PhoneUnlock-Setup.exe**를 실행하면 서비스와 방화벽을 함께 복구한다. 설정 앱이 `설치되지 않음`을 표시하면 **설치 프로그램 받기**를 누르면 최신 설치 EXE를 직접 내려받는다.

설치 후에도 같은 메시지가 나오면 `services.msc`에서 `PhoneUnlockService`가 `실행 중`인지 확인한다. 설치 EXE를 `/LOG=PhoneUnlock-install.log`와 함께 실행하면 실패한 Windows 명령과 오류 코드가 로그에 남는다.

## Android APK를 업데이트할 수 없음

`0.4.0-beta.1` 이전 GitHub Actions의 debug APK는 빌드할 때마다 서명이 달라질 수 있다. 이번 한 번만 기존 Phone Unlock 앱을 삭제하고 릴리스의 `PhoneUnlock-Android.apk`를 설치한다. 이후 버전은 고정 서명을 사용하므로 앱의 **새 버전 받기** 버튼으로 덮어쓰기 업데이트할 수 있다. Android 시스템의 설치 확인은 보안상 생략할 수 없다.

## Windows 암호를 확인하지 못함

계정은 현재 로그인 사용자로 자동 선택된다. Windows Hello PIN이 아니라 계정의 실제 암호를 입력해야 한다. Microsoft 계정이면 Microsoft 계정 암호, 로컬 계정이면 로컬 계정 암호를 사용한다.

## 로그인 요청 알림이 오지 않음

- Android에서 Phone Unlock 알림 권한과 배터리 백그라운드 실행을 허용한다.
- 앱의 상시 알림이 `PC 이름 · 연결됨`인지 확인한다.
- Android 재부팅 후 제조사 정책이 자동 시작을 막으면 앱을 한 번 직접 연다.
- 설정 앱의 휴대폰 상태가 `오프라인`이면 다시 연결하거나 재페어링한다.

## 휴대폰 연결·알림·배터리 상태 점검

Android 앱의 **연결·알림·배터리 진단** 버튼을 누르면 PC `/health` 응답, 알림 권한, 배터리 최적화, 인증 준비 상태를 한 번에 확인할 수 있다. 배터리 제한이 표시되면 진단 카드의 **배터리 제한 설정 열기**에서 Phone Unlock을 제한 없음으로 설정한다.

## 연결이 끊기면 PC 자동 잠금

Windows 설정 앱에서 **연결이 끊기면 자동 잠금**을 켜고 10~600초 유예 시간을 선택한다. 이 기능은 GPS로 거리를 재는 기능이 아니라 선택한 휴대폰의 인증된 LAN WebSocket heartbeat가 끊긴 뒤 잠그는 기능이다. Windows 로그인 세션이 없거나 휴대폰이 한 번도 연결되지 않은 상태에서는 오탐 잠금을 막기 위해 감시가 무장되지 않는다.

## 요청이 와도 지문창이 자동으로 열리지 않음

- Android 앱의 **로그인 요청 시 인증창 자동 열기**가 켜져 있는지 확인한다.
- Android 14 이상에서 **자동 팝업 허용** 버튼이 보이면 눌러 Phone Unlock의 전체 화면 알림을 허용한다.
- Phone Unlock의 `Windows 로그인 요청` 알림 채널을 무음 또는 낮은 중요도로 바꾸지 않는다.
- 휴대폰을 사용 중일 때는 Android가 전체 화면 대신 상단 알림만 표시할 수 있다. 이때 알림을 누르면 승인 버튼 없이 지문창이 바로 열린다.

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
