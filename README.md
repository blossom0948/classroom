# Phone Unlock

Android의 생체인증과 Android Keystore의 ECDSA P-256 키를 이용해 Windows PC가 만든 일회용 challenge를 승인하는 실험 프로젝트입니다.

> 현재 저장소는 안전한 **Phase 1 암호 인증 테스트**까지 구현합니다. 실제 Windows 잠금 해제, Credential Provider 등록, Windows 비밀번호 저장은 아직 구현하거나 설치하지 않았습니다. Windows의 기존 PIN/비밀번호 로그인은 변경되지 않습니다.

## 현재 가능한 것

- Windows 테스트 앱에서 32바이트 암호학적 challenge 생성
- 30초 만료, request/computer 바인딩, canonical UTF-8 payload 생성
- Android Keystore에 export 불가능한 EC P-256 키 생성
- Android `BiometricPrompt.CryptoObject` 인증 후 `SHA256withECDSA` 서명
- Android 응답을 Windows 테스트 앱에 붙여 넣어 공개키 서명 검증
- 만료·재전송·다른 challenge·잘못된 키 거부
- 로컬 암호 흐름 데모와 자동화된 Core self-test

Phase 1에서는 네트워크를 의도적으로 연결하지 않습니다. PC의 요청 JSON을 Android 앱에 붙여 넣고, Android의 공개키/응답 JSON을 PC 앱에 붙여 넣는 방식으로 암호 상호운용성을 먼저 확인합니다. 다음 Phase에서 페어링과 `wss://` WebSocket 전송을 추가합니다.

## 저장소 구조

```text
docs/                                설계, 보안, 테스트, 문제 해결
protocol/protocol.md                  v1 메시지와 canonical payload 규격
android/PhoneUnlock/                  Kotlin Android 앱
windows/PhoneUnlock.Core/             challenge와 검증 코어
windows/PhoneUnlock.Core.Tests/       의존성 없는 실행형 self-test
windows/PhoneUnlock.Desktop/          .NET 8 WPF 테스트 앱
windows/PhoneUnlock.Service/          다음 Phase 자리표시자
windows/PhoneUnlock.CredentialProvider/ 최종 Phase 자리표시자
windows/PhoneUnlock.Installer/        최종 Phase 자리표시자
scripts/dev/                           개발·검증 스크립트
scripts/uninstall/                     향후 복구 스크립트 위치
```

## Windows 테스트 앱 실행

필수: Windows 11, .NET 8 SDK

```powershell
dotnet run --project .\windows\PhoneUnlock.Desktop\PhoneUnlock.Desktop.csproj
```

앱에서 `테스트 challenge 생성`을 누른 후 요청 JSON을 Android 앱에 전달합니다. 휴대폰에서 생체인증 서명을 마치면 Android 앱에 표시되는 공개키와 응답 JSON을 Windows 앱에 붙여 넣고 `Android 서명 검증`을 누릅니다.

휴대폰 없이도 `로컬 암호 데모`로 동일한 canonical payload와 DER ECDSA 검증 경로를 확인할 수 있습니다. 이 버튼은 Android 생체인증을 흉내 내는 개발용 테스트일 뿐, 실제 휴대폰 인증 성공을 뜻하지 않습니다.

## Android 앱 빌드

필수: JDK 17, Android SDK 36, Android Build Tools 36.0.0

```powershell
cd .\android\PhoneUnlock
.\gradlew.bat :app:assembleDebug
```

생성 APK: `android/PhoneUnlock/app/build/outputs/apk/debug/app-debug.apk`

실기기에서 Android 11(API 30) 이상과 강한 생체인증 등록이 필요합니다. 앱의 `요청 JSON` 칸에 Windows 요청을 붙여 넣고 `생체인증 후 서명`을 누릅니다.

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
- Windows 비밀번호, PIN, 개인키, pairing secret을 Git에 저장하지 않습니다.
- 로그인 문제가 생기면 언제나 Windows 기본 PIN/비밀번호를 사용해야 합니다.
- 향후 Phone Unlock은 기본 로그인 수단을 대체하거나 제거하지 않는 추가 Credential Provider로만 제공됩니다.

자세한 내용은 [아키텍처](docs/ARCHITECTURE.md), [보안](docs/SECURITY.md), [테스트](docs/TESTING.md), [문제 해결](docs/TROUBLESHOOTING.md), [프로토콜](protocol/protocol.md)을 참고하세요.
