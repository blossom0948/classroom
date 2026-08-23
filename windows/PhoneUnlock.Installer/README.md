# Phone Unlock 설치·복구

GitHub Releases에서 다음 파일 하나를 받아 더블클릭합니다.

```text
PhoneUnlock-Setup.exe
```

일반 Windows 설치 마법사가 서비스, 자동 시작, 로컬 네트워크 방화벽 규칙, 시작 메뉴 바로가기를 설치합니다. 실패하면 설치 마법사가 오류 코드를 표시하고 `/LOG=파일경로` 옵션으로 전체 로그를 남길 수 있습니다.

설정 앱에서 QR 연결 → 현재 계정 암호 확인 → **지문 로그인 켜기**를 진행합니다. 마지막 버튼이 실제 휴대폰 인증 테스트와 로그인 활성화를 함께 처리합니다.

ZIP과 `Install-PhoneUnlock.ps1`은 개발·복구용 보조 경로일 뿐 일반 설치에는 필요하지 않습니다.

문제가 있으면 기존 PIN 또는 비밀번호로 로그인한 뒤 다음을 실행합니다.

```powershell
& "$env:ProgramFiles\PhoneUnlock\Disable-CredentialProvider.ps1"
```

로그인 화면에 진입할 수 없다면 Windows 복구 환경/안전 모드에서 [RECOVERY.md](RECOVERY.md)의 레지스트리 명령을 사용합니다. 이 프로젝트는 Microsoft 기본 Credential Provider를 필터링하지 않으므로 기존 로그인 옵션이 계속 남아 있어야 합니다.
