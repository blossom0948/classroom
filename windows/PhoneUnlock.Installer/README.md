# Phone Unlock 설치·복구

릴리스 ZIP을 관리자 PowerShell에서 압축 해제한 뒤 아래 순서로 실행합니다.

```powershell
.\Install-PhoneUnlock.ps1
```

설정 앱에서 Windows 계정 저장 → Android 페어링 → 휴대폰 인증 테스트를 완료합니다. 테스트 성공 후에만 로그인 타일을 활성화합니다.

```powershell
.\Enable-CredentialProvider.ps1
```

문제가 있으면 기존 PIN 또는 비밀번호로 로그인한 뒤 다음을 실행합니다.

```powershell
& "$env:ProgramFiles\PhoneUnlock\Disable-CredentialProvider.ps1"
```

로그인 화면에 진입할 수 없다면 Windows 복구 환경/안전 모드에서 [RECOVERY.md](RECOVERY.md)의 레지스트리 명령을 사용합니다. 이 프로젝트는 Microsoft 기본 Credential Provider를 필터링하지 않으므로 기존 로그인 옵션이 계속 남아 있어야 합니다.
