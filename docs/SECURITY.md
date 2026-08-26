# Security

## 보안 속성

- challenge는 `RandomNumberGenerator`가 생성한 32바이트 난수다.
- 요청은 `requestId`, `computerId`, challenge, 절대 만료시간에 함께 서명된다.
- canonical payload는 UTF-8, LF, 고정 필드 순서, 끝 줄바꿈 없음으로 정의된다.
- 기본 Android 개인키는 사용자 생체인증이 매번 필요한 Keystore 키다. 호환 모드의 별도 키는 예외이며, 약한 얼굴인식 성공 뒤 앱이 서명하도록 사용한다.
- PC는 X.509 SubjectPublicKeyInfo 공개키만 받는다.
- 검증 성공 후 request ID를 원자적으로 소비해 재전송을 거부한다.
- 잘못된 서명은 요청을 소비하지 않아 단순한 네트워크 방해가 정상 승인을 차단하지 못한다.
- pairing token과 device token은 각각 256비트 난수이며 서버에는 device token hash만 저장한다.
- Android는 최초 페어링 JSON의 TLS 인증서 SHA-256 fingerprint를 고정한다.
- Windows 설정/인증 Named Pipe ACL은 운영 서비스에서 LocalSystem과 Administrators로 제한한다.
- 자동 잠금 Named Pipe는 저장된 Windows 계정 SID를 추가 ACL로 허용하고, 대화형 `PhoneUnlock.Agent`가 `LockWorkStation`을 호출한다.
- Android 인증은 `BIOMETRIC_STRONG`만 기본 허용하며, 사용자가 켠 경우에만 Android `DEVICE_CREDENTIAL`을 함께 허용한다. 사용자가 별도로 켠 호환 모드에서만 `BIOMETRIC_WEAK`을 허용하고, 이 모드에서는 사용자 인증과 서명 키의 암호학적 연결이 약해진다.
- 근접 자동 잠금 해제는 기본값이 꺼져 있으며, 사용자가 설정에서 켠 경우에만 보안 연결의 presence 전환으로 Credential Provider가 인증 없이 저장 자격 증명을 요청한다. 이는 휴대폰 heartbeat만으로 잠금을 풀 수 있는 편의 기능이므로 집에서만 사용해야 한다.
- 서비스 구성과 PFX 파일 ACL은 LocalSystem과 Administrators로 제한한다.
- Credential Provider는 요청한 사용자 SID와 저장된 계정 SID가 일치할 때만 자격 증명을 받는다.
- Credential Provider는 원문 비밀번호를 받은 즉시 `CredProtectW`로 보호하고 원문 버퍼를 지운다.

## 신뢰 경계와 제한

- 이 방식은 완전한 passwordless 로그인이 아니다. LocalSystem 서비스가 기존 Windows 비밀번호를 Windows Credential Manager에 보관하고, 유효한 휴대폰 승인이 있을 때만 Credential Provider에 전달한다.
- 관리자 또는 LocalSystem 권한이 탈취되면 저장 자격 증명과 서비스 신뢰 경계를 우회할 수 있다.
- 잠금 해제 가능 여부는 같은 LAN 또는 연결된 사설 VPN, Android 백그라운드 실행/알림, 휴대폰 배터리 정책에 영향을 받는다.
- 자체 서명 TLS 인증서는 공개 PKI가 아니라 최초 페어링 정보의 fingerprint 정확성에 의존한다.
- 현재 릴리스는 코드 서명이 없다. Windows VM에서 DLL/설치/복구를 먼저 검증해야 한다.
- 근접 자동 잠금 해제는 기본값으로 허용하지 않는다. 사용자가 명시적으로 켠 경우에만 근접 신호가 Credential Provider의 인증 요청을 시작하며, 이는 집에서만 사용해야 하는 편의 기능이다.

## 기록 금지

- Windows 계정 비밀번호와 PIN
- Android private key
- raw biometric 정보
- 전체 pairing secret

## 설치 안전 장치

- 기본 Microsoft Credential Provider 필터를 설치하지 않는다.
- 서비스, 자격 증명, 페어링, 최근 인증 테스트가 모두 준비된 후에만 타일 등록을 허용한다.
- 비활성화 스크립트는 Phone Unlock의 두 레지스트리 키만 제거한다.
- 방화벽은 Ethernet이 Public으로 분류되는 PC도 지원하도록 모든 Windows 네트워크 프로필에서 열리지만, 설치된 서비스 실행 파일의 TCP 48231과 `LocalSubnet` 원격 주소로만 제한한다.
- 원격 장소 연결은 Tailscale/WireGuard 같은 암호화 사설 VPN 주소 대역으로 제한한다. 공용 인터넷에 서비스 포트를 직접 공개하지 않는다.
- 최초 운영 사용 전 별도 관리자 계정 또는 확인된 PIN/비밀번호 복구 수단을 유지한다.
