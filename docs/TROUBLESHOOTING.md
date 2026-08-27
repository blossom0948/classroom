# Troubleshooting

## .NET 8 SDK를 찾을 수 없음

`dotnet --list-sdks`에 8.x가 있어야 한다. 런타임만 설치되어 있으면 빌드할 수 없다.

## Android 빌드가 JDK를 찾지 못함

AGP 9.0.1은 JDK 17과 Gradle 9.1.0이 필요하다. `JAVA_HOME`을 JDK 17 폴더로 설정하고 새 터미널에서 다시 실행한다.

## 생체인증 사용 불가

기본 모드에서는 기기에 PIN/패턴 같은 보안 잠금과 강한 생체인증을 먼저 등록한다. 앱 설정에서 **휴대폰 PIN·강한 얼굴인식도 허용**을 켜면 Android 시스템 PIN/패턴/비밀번호도 사용할 수 있다. 기기가 얼굴인식을 `BIOMETRIC_WEAK`으로만 제공하면 기존 보안 키 모드에서는 얼굴이 거부될 수 있다. 집에서 호환성을 우선할 때만 **약한 얼굴인식 호환 모드 사용**을 켜고 해당 PC를 다시 연결한다. 이 모드는 서명 키와 생체인식의 암호학적 연결이 약해진다.

## Key permanently invalidated

생체정보를 추가/삭제하면 기존 키가 무효화될 수 있다. Android 앱에서 PC 연결을 해제한 뒤 Windows 설정 앱이 만든 새 페어링 정보로 다시 연결한다.

## PC 연결 실패

- PC는 이더넷, Android는 Wi-Fi여도 된다. 같은 LAN이 아니어도 양쪽을 Tailscale/WireGuard 같은 사설 VPN에 연결하면 된다.
- QR에는 PC의 LAN·VPN 주소 후보가 함께 들어가며 Android가 주소를 자동으로 바꿔 시도한다. `100.x.x.x` 후보가 없으면 VPN 연결 후 QR을 다시 만든다.
- 방화벽 규칙은 Windows의 Public/Private/Domain 프로필에서 TCP 48231을 로컬 서브넷과 사설 VPN 대역에만 허용한다.
- 연결 QR은 생성 후 2분 안에 사용해야 하며 한 번만 사용할 수 있다.
- 공유기 AP isolation/게스트 Wi-Fi가 켜져 있으면 기기 간 통신이 차단될 수 있다.
- LAN IP가 바뀐 경우 QR을 다시 만들고 페어링한다. Tailscale의 `100.x.x.x` 주소는 장소가 바뀌어도 보통 유지된다.

자세한 순서는 [원격 연결 안내](REMOTE_CONNECTION.md)를 참고한다. 공용 인터넷에 TCP 48231을 직접 공개하는 방법은 지원하지 않는다.

## 연결 QR 버튼을 눌러도 아무것도 나오지 않음

이전 설치는 파일만 복사되고 Windows 서비스 생성 전에 실패할 수 있었다. 최신 릴리스의 **PhoneUnlock-Setup.exe**를 실행하면 서비스와 방화벽을 함께 복구한다. 설정 앱이 `설치되지 않음`을 표시하면 **설치 프로그램 받기**를 누르면 최신 설치 EXE를 직접 내려받는다.

설치 후에도 같은 메시지가 나오면 `services.msc`에서 `PhoneUnlockService`가 `실행 중`인지 확인한다. 설치 EXE를 `/LOG=PhoneUnlock-install.log`와 함께 실행하면 실패한 Windows 명령과 오류 코드가 로그에 남는다.

## Android APK를 업데이트할 수 없음

`0.4.0-beta.1` 이전 GitHub Actions의 debug APK는 빌드할 때마다 서명이 달라질 수 있다. 이번 한 번만 기존 Phone Unlock 앱을 삭제하고 릴리스의 `PhoneUnlock-Android.apk`를 설치한다. 이후 버전은 고정 서명을 사용하므로 앱의 **새 버전 받기** 버튼으로 덮어쓰기 업데이트할 수 있다. Android 시스템의 설치 확인은 보안상 생략할 수 없다.

## 업데이트 서버 응답 오류

beta.22부터 업데이트 확인은 GitHub REST API가 아니라 저장소의 `update.json`을 먼저 읽는다. 따라서 `api.github.com`의 익명 호출 제한으로 인해 업데이트가 "서버가 응답하지 않음"으로 표시되는 문제를 피한다. 그래도 실패하면 Android의 기본 브라우저에서 GitHub 저장소가 열리는지 확인하고, VPN·광고 차단 앱이 `raw.githubusercontent.com`을 차단하지 않는지 확인한다.

## PC 설정창이 바로 닫히는 경우

설치된 파일의 버전이 beta.22보다 낮으면 설정창 시작 전에 종료될 수 있다. 기존 PIN/비밀번호로 Windows에 로그인한 후 GitHub Releases의 `PhoneUnlock-Setup.exe`를 실행해 덮어 설치한다. beta.22부터 시작 예외는 `%LOCALAPPDATA%\PhoneUnlock\setup-startup.log`에도 남는다.

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

Windows 설정 앱에서 **연결이 끊기면 자동 잠금**을 켜고 10~600초 유예 시간을 선택한다. 이 기능은 GPS로 거리를 재는 기능이 아니라 선택한 휴대폰의 인증된 LAN/VPN WebSocket heartbeat가 끊긴 뒤 잠그는 기능이다. Windows 로그인 세션이 없거나 휴대폰이 한 번도 연결되지 않은 상태에서는 오탐 잠금을 막기 위해 감시가 무장되지 않는다.

최신 설치본에서 설정 앱에 `자동잠금 에이전트 연결됨`이 표시되어야 한다. 표시되지 않으면 **자동잠금 감시 시작**을 누르거나 Windows에 다시 로그인한다. 앱에서 PC 페어링을 삭제하면 WebSocket이 끊기고 설정한 유예 시간이 지난 뒤 잠긴다.

## 휴대폰이 가까워지면 자동 잠금 해제

**휴대폰이 가까워지면 PC 자동 잠금 해제**는 기본값이 꺼져 있다. 켜면 인증된 heartbeat가 다시 들어오는 순간 휴대폰 생체/PIN 확인 없이 저장된 Windows 자격 증명으로 잠금이 풀린다. 근접 신호만으로 인증을 완료하므로 보안 수준이 크게 낮아지고 집에서만 사용해야 한다. 이 기능은 Phone Unlock 로그인 옵션과 저장된 Windows 자격 증명이 등록된 잠금화면에서 동작한다.

## PC 원격 켜기가 되지 않음

Android의 **PC 켜기**는 Wake-on-LAN 매직 패킷을 지정 브로드캐스트와 전체 브로드캐스트, UDP 7·9번 포트로 짧게 반복 전송한다. 그래도 켜지지 않으면 PC BIOS/UEFI에서 Wake on LAN을 켜고, Windows 네트워크 카드의 `Wake on Magic Packet`과 전원 관리 옵션을 확인한다. 완전히 꺼진 PC는 VPN 주소로 연결할 수 없으므로, 다른 장소에서 켜려면 PC가 있던 LAN의 공유기 WOL 기능이나 상시 켜진 릴레이가 필요하다. PC 설정 앱에서 QR을 다시 만들지 않아 Android에 WOL 정보가 없으면 앱 진단에 `WOL 정보 없음`이 표시된다.

## 재실 센서 상태를 확인할 수 없음

Windows 설정 앱의 재실 센서 카드에서 **사람이 없으면 잠금**만 켠 뒤 **현재 상태 테스트**를 누른다. 기본값인 **이 PC 재실 센서 · 자동**은 별도 토큰·허브 입력 없이 동작하며, 설치 직후와 Windows 로그인 때 자동잠금 감시가 자동 시작된다. `사람 감지 중` 또는 `사람 없음`이 표시되면 바로 쓸 수 있다. 응답 없음이면 PC가 Windows 11 24H2 이상이 아니거나 호환 재실 하드웨어가 없는 것이다. Zigbee/Matter 센서는 센서 자체가 연결되어 있다는 것만으로는 충분하지 않고 Home Assistant 또는 SmartThings API에서 상태를 읽을 수 있어야 한다. SmartThings Station은 현재 계정 API 인증과 센서 선택이 필요하며, Phone Unlock이 SmartThings 계정 로그인이나 토큰 발급을 대신할 수는 없다.

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
