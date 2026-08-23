# 로그인 복구

Phone Unlock은 Microsoft 기본 로그인 옵션을 필터링하지 않습니다. 먼저 로그인 화면의 **로그인 옵션**에서 PIN 또는 비밀번호를 선택하세요.

## Windows 안전 모드 또는 복구 명령 프롬프트

관리자 명령 프롬프트에서 Phone Unlock 타일 등록만 제거합니다.

```cmd
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{8C12D44B-04D3-41D4-980B-80DF3D8DD324}" /f
reg delete "HKLM\SOFTWARE\Classes\CLSID\{8C12D44B-04D3-41D4-980B-80DF3D8DD324}" /f
reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\System" /v DefaultCredentialProvider /f
```

서비스도 중지해야 한다면 다음을 실행합니다.

```cmd
sc stop PhoneUnlockService
sc config PhoneUnlockService start= disabled
```

재부팅 후 기존 PIN/비밀번호로 로그인하고 Phone Unlock을 다시 설정하거나 제거하세요.
