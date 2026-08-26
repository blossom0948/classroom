# Passwordless/FIDO2 확장 조사

이 문서는 현재 Phone Unlock 로그인 경로를 교체하지 않고, 향후 Windows 비밀번호 저장 의존성을 줄일 때 검토할 경로를 정리한다.

## 현재 구조

Phone Unlock은 Windows Credential Provider 타일에서 PC 서비스에 인증을 요청하고, Android Keystore P-256 서명을 확인한 뒤 Windows Credential Manager에 저장된 현재 계정 자격 증명을 반환한다. 이는 현재 Windows 로컬 계정과 일반 Microsoft 계정에서 동작하는 추가 로그인 수단이며, 기본 PIN·비밀번호·Windows Hello 공급자를 유지한다.

## 확인한 플랫폼 경계

- Windows의 Credential Provider는 Logon UI가 수집한 자격 증명을 LSA 인증에 전달하는 확장 지점이다. Microsoft는 사용자마다 최소 하나의 시스템 Credential Provider와 복구 로그인 경로를 유지하도록 권장한다.
- Windows WebAuthn API는 FIDO2/WebAuthn 인증기를 앱에 제공하며, 최신 Windows에서는 플러그인 passkey manager를 등록할 수 있는 방향으로 확장되고 있다.
- 그렇다고 Android의 현재 Phone Unlock 서명을 Windows 잠금화면 passkey로 즉시 바꿀 수 있는 것은 아니다. WebAuthn은 relying party/origin, authenticator 데이터, 계정 등록 및 검증 서버가 필요한 별도 프로토콜이다.
- Windows Web Sign-in은 특정 Entra joined 환경의 제한된 Credential Provider 시나리오이므로 개인 로컬 PC의 일반적인 대체 경로로 가정하지 않는다.

## 향후 설계 순서

1. 별도 테스트 앱에서 WebAuthn/FIDO2 등록·인증과 Windows 계정 매핑을 검증한다.
2. 로컬 계정, Microsoft 계정, Entra 계정, Windows edition 및 조직 정책별 동작을 구분한다.
3. 복구 PIN·비밀번호가 남아 있는 VM에서 custom Credential Provider와 충돌하지 않는지 확인한다.
4. 검증된 경우에만 Phone Unlock의 기존 `AUTH_REQUEST` 경로와 독립된 feature flag로 추가한다.

현재 릴리스에서는 WebAuthn, FIDO2, passkey manager 또는 Web Sign-in을 Windows 잠금해제에 사용하지 않는다.

참고:

- [Microsoft: Credential Providers in Windows](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-providers-in-windows)
- [Microsoft: WebAuthn APIs](https://learn.microsoft.com/en-us/windows/security/identity-protection/hello-for-business/webauthn-apis)
- [Microsoft: Web Sign-in](https://learn.microsoft.com/en-us/windows/security/identity-protection/web-sign-in/)
