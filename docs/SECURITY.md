# Security

## Phase 1 보안 속성

- challenge는 `RandomNumberGenerator`가 생성한 32바이트 난수다.
- 요청은 `requestId`, `computerId`, challenge, 절대 만료시간에 함께 서명된다.
- canonical payload는 UTF-8, LF, 고정 필드 순서, 끝 줄바꿈 없음으로 정의된다.
- Android 개인키는 사용자 생체인증이 매번 필요한 Keystore 키다.
- PC는 X.509 SubjectPublicKeyInfo 공개키만 받는다.
- 검증 성공 후 request ID를 원자적으로 소비해 재전송을 거부한다.
- 잘못된 서명은 요청을 소비하지 않아 단순한 네트워크 방해가 정상 승인을 차단하지 못한다.

## 현재 신뢰 경계

Phase 1의 JSON 전달은 수동이며 페어링 신원을 제공하지 않는다. Windows 앱에 붙여 넣는 공개키가 실제 등록 휴대폰의 키인지 사용자가 확인해야 한다. 이 제한 때문에 현재 코드는 인증 연구용이며 Windows 로그인에 연결하면 안 된다.

## 기록 금지

- Windows 계정 비밀번호와 PIN
- Android private key
- raw biometric 정보
- 전체 pairing secret

## 다음 Phase 필수 조건

- 256비트 pairing token과 짧은 만료
- 공개키·phone ID의 명시적 등록/해제
- TLS 및 인증서 fingerprint pinning
- 외부 인터페이스에 무인증 HTTP 노출 금지
- rate limit, 구조화 보안 로그, 키 삭제 이후 즉시 거부
