# Android Credential Manager 확장 조사

Android Credential Manager는 passkey, 비밀번호, federated sign-in을 하나의 시스템 UI로 다루는 API다. Phone Unlock의 Windows 로그인 승인 프로토콜을 대신하는 기능이 아니라, 향후 웹·앱 계정 보안 허브로 확장할 때 별도로 검토한다.

## 현재 앱과의 구분

- 현재 Phone Unlock은 Android Keystore의 PC별 ECDSA 키와 `BiometricPrompt.CryptoObject`를 사용한다.
- Credential Manager passkey는 relying party 서버가 생성 옵션과 challenge를 제공하고, public key credential 등록·검증을 관리하는 WebAuthn 흐름이다.
- 따라서 현재의 로컬 PC WSS 연결과 Windows Credential Provider를 Credential Manager provider로 억지로 포장하지 않는다.

## 향후 후보

1. Phone Unlock과 별도의 웹/앱 로그인 passkey 기능을 만든다.
2. Android Credential Manager와 passkey provider의 Android 14+ 권한·metadata·암호화 저장 요구사항을 검증한다.
3. PC와 phone 간 cross-device 인증을 추가할 때 origin, user verification, restore credentials, 계정 복구 정책을 별도로 설계한다.
4. Wear OS는 직접 인증기로 간주하지 않고, 사용자 확인 후 Phone Unlock에 안전한 승인 요청을 전달하는 별도 제품 흐름으로 검증한다.

현재 릴리스에는 Credential Manager provider, passkey 저장소, Wear OS 직접 인증을 넣지 않는다.

참고:

- [Android Developers: About passkeys](https://developer.android.com/identity/passkeys)
- [Android Developers: Create a passkey](https://developer.android.com/identity/passkeys/create-passkeys)
- [Android Developers: Credential Manager provider integration](https://developer.android.com/identity/sign-in/credential-provider)
