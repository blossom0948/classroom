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

## Windows UI 수동 검사

1. `테스트 challenge 생성`을 누른다.
2. 요청 JSON의 challenge가 매번 바뀌는지 확인한다.
3. `로컬 암호 데모`를 눌러 성공을 확인한다.
4. 같은 응답으로 다시 검증해 replay 거부를 확인한다.
5. 30초 후 응답이 만료되는지 확인한다.

## Android 실기기 상호운용 검사

1. 강한 생체인증이 등록된 Android 11+ 기기에 debug APK를 설치한다.
2. Windows 요청 JSON을 Android 앱에 붙여 넣는다.
3. `생체인증 후 서명`을 누르고 지문/생체인증을 완료한다.
4. 공개키와 응답 JSON을 Windows 앱으로 복사한다.
5. `Android 서명 검증` 결과가 성공인지 확인한다.
6. 같은 응답을 다시 제출해 replay 거부를 확인한다.
7. 요청 생성 후 30초가 지나면 만료 거부를 확인한다.

실제 생체인증 테스트는 에뮬레이터가 아닌 실기기를 권장한다.
