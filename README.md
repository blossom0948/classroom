# Phone Unlock

Android의 강한 생체인증과 Android Keystore의 ECDSA P-256 키로 Windows 11 로그인을 승인하는 로컬 네트워크 프로젝트입니다.

> 실제 로그인 경로의 소스와 설치 패키지를 구현했습니다. 다만 Credential Provider는 로그인 시스템에 로드되는 네이티브 DLL이므로, 먼저 Windows VM과 실제 Android 기기에서 복구 절차까지 검증한 뒤 사용하세요. 설치 스크립트는 기본 PIN·비밀번호·Windows Hello를 비활성화하지 않습니다.

## 구현된 기능

- Windows QR 표시와 Android 카메라 스캔을 이용한 2분·1회용 LAN 페어링
- Android Keystore의 내보낼 수 없는 P-256 키와 `BiometricPrompt.CryptoObject`
- 인증된 `wss://` 연결, 로그인 요청 시 자동 생체인식 화면, 자동 재연결
- Windows LocalSystem 서비스의 challenge/만료/replay/서명 검증
- 관리자 전용 Named Pipe로 설정 앱과 Credential Provider 분리
- 현재 로그인 계정 자동 선택과 Windows Credential Manager의 검증된 자격 증명 보호 저장
- V2 Credential Provider를 기본 로그인으로 선택하고, 잠금화면이 열리면 자동으로 휴대폰 요청
- 설정 앱 안의 지문 테스트·로그인 활성화와 별도 비활성화·복구 스크립트
- ZIP이나 PowerShell 없이 설치되는 단일 `PhoneUnlock-Setup.exe`와 앱 내 업데이트 확인
- 한 번 만든 고정 키로 서명되는 Android release APK와 앱 내 최신 APK 바로 받기
- 수동 JSON 암호 상호운용 테스트 화면과 자동화된 Core/Service self-test 유지

## 저장소 구조

```text
docs/                                설계, 보안, 테스트, 문제 해결
protocol/protocol.md                  v1 메시지와 canonical payload 규격
android/PhoneUnlock/                  Kotlin Android 앱
windows/PhoneUnlock.Core/             challenge와 검증 코어
windows/PhoneUnlock.Core.Tests/       의존성 없는 실행형 self-test
windows/PhoneUnlock.Desktop/          .NET 8 WPF 테스트 앱
windows/PhoneUnlock.Service/          HTTPS/WSS + IPC Windows Service
windows/PhoneUnlock.Setup/            관리자용 WPF 설정 앱
windows/PhoneUnlock.CredentialProvider/ V2 네이티브 Credential Provider
windows/PhoneUnlock.Installer/        단계별 설치·활성화·복구 스크립트
scripts/dev/                           개발·검증 스크립트
scripts/uninstall/                     향후 복구 스크립트 위치
```

## 설치 순서

GitHub Releases에서 Windows용 `PhoneUnlock-Setup.exe`와 휴대폰용 `PhoneUnlock-Android.apk`만 받으면 됩니다.

1. Windows에서 **PhoneUnlock-Setup.exe**를 더블클릭하고 설치합니다. ZIP 압축 해제나 PowerShell 작업은 필요하지 않습니다.
2. Android 11 이상 실기기에 **PhoneUnlock-Android.apk**를 설치하고 알림을 허용합니다. Android 14 이상에서는 앱에 표시되는 **자동 팝업 허용**도 한 번 누릅니다.
3. Windows 설정 앱에서 **연결 QR 코드 만들기**를 누르고 Android 앱으로 스캔합니다.
4. 자동 선택된 현재 계정의 Windows 암호를 한 번 입력합니다. PIN 번호는 사용할 수 없습니다.
5. **지문 로그인 켜기**를 누르고 휴대폰에서 지문을 인증합니다.
6. 이후 PC가 잠기면 Phone Unlock이 기본으로 열리고 휴대폰에 요청이 자동 전송됩니다. 휴대폰 잠금 화면 위에 생체인식 창이 바로 열리며 별도의 승인 버튼은 필요하지 않습니다.

PC와 Android 앱 모두 실행 화면에서 새 버전을 확인할 수 있습니다. Android에서 이전 `debug` APK를 사용했다면 고정 서명판으로 바뀌는 이번 한 번만 기존 앱을 삭제하고 다시 설치해야 합니다. 이후 release APK는 연결 정보를 유지한 채 업데이트됩니다.

Windows 설정 창은 닫아도 됩니다. 로그인 요청을 처리하는 `PhoneUnlockService`가 Windows 서비스로 백그라운드에서 자동 실행됩니다. 설정을 다시 열 때는 바탕 화면의 **Phone Unlock 설정** 또는 시작 메뉴에서 `Phone Unlock 설정`을 검색합니다.

비활성화와 복구 방법은 [설치·복구 문서](windows/PhoneUnlock.Installer/README.md)를 참고하세요.

## 개발용 Windows 테스트 앱

필수: Windows 11, .NET 8 SDK

```powershell
dotnet run --project .\windows\PhoneUnlock.Desktop\PhoneUnlock.Desktop.csproj
```

이 앱은 네트워크/로그인과 분리된 수동 암호 상호운용 테스트를 유지합니다. `테스트 challenge 생성` 후 요청 JSON을 Android 앱에 전달하고 공개키와 응답 JSON을 되돌려 `Android 서명 검증`을 누릅니다.

휴대폰 없이도 `로컬 암호 데모`로 동일한 canonical payload와 DER ECDSA 검증 경로를 확인할 수 있습니다. 이 버튼은 Android 생체인증을 흉내 내는 개발용 테스트일 뿐, 실제 휴대폰 인증 성공을 뜻하지 않습니다.

## Android 앱 빌드

필수: JDK 17, Android SDK 36, Android Build Tools 36.0.0

개발용 debug APK는 `:app:assembleDebug`로 빌드할 수 있습니다. 배포용 `:app:assembleRelease`는 저장소 소유자의 고정 서명 환경 변수가 필요하며 GitHub Actions에서 생성합니다.

실기기에서 Android 11(API 30) 이상과 강한 생체인증 등록이 필요합니다. 실제 로그인은 앱의 **QR 코드 스캔**으로 연결하면 이후 요청부터 생체인식 창이 자동으로 열립니다. 휴대폰 사용 중에는 Android 정책에 따라 전체 화면 대신 상단 알림으로 표시될 수 있으며, 이 경우 알림을 누르면 생체인식 창이 즉시 열립니다.

## 전체 검증

```powershell
.\scripts\dev\verify.ps1
```

Android 빌드까지 검사하려면 JDK 17을 준비한 뒤 다음을 실행합니다.

```powershell
.\scripts\dev\verify.ps1 -IncludeAndroid
```

## 중요한 안전 안내

- 이 앱은 지문 원본 데이터에 접근하지 않습니다.
- Android 개인키는 Keystore 밖으로 내보내지 않습니다.
- Windows 비밀번호, PIN, 개인키, pairing secret을 Git에 저장하지 않습니다. Windows 비밀번호는 서비스 계정의 Credential Manager에 저장되며 Android로 전송되지 않습니다.
- 로그인 문제가 생기면 언제나 Windows 기본 PIN/비밀번호를 사용해야 합니다.
- Phone Unlock은 기본 로그인 수단을 대체하거나 제거하지 않는 추가 Credential Provider로만 등록됩니다.
- Android는 보안 정책상 APK를 완전 무인 설치할 수 없으므로 앱이 최신 APK 다운로드를 열어 준 뒤 시스템 설치 확인은 한 번 눌러야 합니다.

자세한 내용은 [아키텍처](docs/ARCHITECTURE.md), [보안](docs/SECURITY.md), [테스트](docs/TESTING.md), [문제 해결](docs/TROUBLESHOOTING.md), [프로토콜](protocol/protocol.md)을 참고하세요.
