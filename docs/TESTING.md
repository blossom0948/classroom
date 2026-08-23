# Testing

## 자동화된 Core 검사

`PhoneUnlock.Core.Tests`는 외부 테스트 패키지 없이 실행되는 self-test 프로그램이다. 다음을 검사한다.

- 32바이트 랜덤 challenge와 고유성
- canonical payload의 바이트 단위 고정값
- EC P-256 DER 서명 성공
- 다른 키와 변경된 challenge 거부
- 만료 거부
- 같은 응답 재전송 거부
- pairing token 값과 만료 검증

```powershell
dotnet run --project .\windows\PhoneUnlock.Core.Tests\PhoneUnlock.Core.Tests.csproj -c Release
```

## 자동화된 Service 검사

`PhoneUnlock.Service.Tests`는 장치 토큰 hash 검증, P-256 공개키 등록, 페어링 token 1회 소비, 잘못된 공개키 거부를 검사한다.

```powershell
dotnet run --project .\windows\PhoneUnlock.Service.Tests\PhoneUnlock.Service.Tests.csproj -c Release
```

## Windows UI 수동 검사

1. `테스트 challenge 생성`을 누른다.
2. 요청 JSON의 challenge가 매번 바뀌는지 확인한다.
3. `로컬 암호 데모`를 눌러 성공을 확인한다.
4. 같은 응답으로 다시 검증해 replay 거부를 확인한다.
5. 30초 후 응답이 만료되는지 확인한다.

## Android 실기기 네트워크 검사

1. 강한 생체인증이 등록된 Android 11+ 기기에 고정 서명 release APK를 설치한다.
2. Windows 설정 앱에서 연결 QR을 만든 뒤 Android의 **QR 코드 스캔**으로 읽는다.
3. 설정 앱의 PC와 Android의 PC 이름/주소가 일치하는지 확인한다.
4. 설정 앱에서 **휴대폰에 인증 요청**을 누른다.
5. Android 화면을 끈 상태에서 로그인 요청이 오면 인증 화면과 지문창이 자동으로 열리는지 확인한다.
6. Android 14 이상에서는 앱의 **자동 팝업 허용**으로 이동해 권한을 허용한 뒤 같은 검사를 반복한다.
7. Windows 설정 앱에 인증 성공이 표시되는지 확인한다.
8. 거부, 30초 만료, Wi-Fi 끊김, Android 앱 강제 종료 후 재연결을 각각 확인한다.

## Credential Provider VM 검사

1. 스냅샷을 만든 Windows 11 VM과 별도 Android 실기기를 사용한다.
2. 기본 PIN/비밀번호로 로그인 가능한지 먼저 확인한다.
3. 서비스 설치와 페어링을 마친 뒤 설정 앱에서 **지문 로그인 켜기**를 실행한다.
4. 화면 잠금 후 Phone Unlock이 기본 선택되고 요청이 자동 전송되는지 확인한다.
5. 지문 승인 성공, 사용자 거부, 휴대폰 오프라인, timeout을 확인한다.
6. 잘못 저장한 비밀번호에서 Windows 오류가 표시되고 기본 로그인으로 복구되는지 확인한다.
7. `Disable-CredentialProvider.ps1`과 `RECOVERY.md`의 레지스트리 복구 명령을 검증한다.

실제 생체인증 테스트는 에뮬레이터가 아닌 실기기를 권장한다.
