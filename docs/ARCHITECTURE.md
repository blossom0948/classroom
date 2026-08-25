# Architecture

## 실제 로그인 경로

```text
Windows LogonUI
  Phone Unlock Credential Provider 타일
      ↓ 관리자/SYSTEM 전용 Named Pipe (SID)
PhoneUnlockService (LocalSystem)
  30초 challenge 생성 → 인증된 WSS 연결
      ↓ TLS 인증서 pin + Bearer 장치 토큰
Android Phone Unlock
  잠금 화면 알림 → BiometricPrompt → Keystore ECDSA 서명
      ↓ AUTH_APPROVED
PhoneUnlockService
  phone/computer/request/challenge/만료/replay/서명 검증 + 감사 기록
      ↓ 승인된 경우에만 저장 자격 증명 반환
Credential Provider
  CredProtectW → KERB_INTERACTIVE_UNLOCK_LOGON → Windows LSA
```

Credential Provider는 네트워크나 Android 키를 직접 다루지 않는다. 서비스는 로그인 UI와 분리되어 WSS 연결, challenge 상태, 공개키 검증, 자격 증명 저장을 담당한다. Android는 Windows 비밀번호를 받지 않으며 승인 메시지에 ECDSA 서명만 보낸다.

## 설정·페어링 경로

1. 관리자 설정 앱이 보안 Named Pipe로 Windows 계정을 `LogonUserW` 검증 후 서비스에 저장한다.
2. 서비스가 현재 LAN 주소, 인증서 fingerprint, 2분·1회용 token을 포함한 JSON을 만든다.
3. Android가 고정된 인증서로 `/pair`에 공개키와 phone ID를 등록한다.
4. 서비스는 원문 장치 토큰 대신 SHA-256 hash만 구성 파일에 저장한다.
5. Android는 장치 토큰을 Android Keystore AES-GCM 키로 암호화해 저장한다.
6. 실제 휴대폰 인증 테스트 성공 후 10분 동안만 Credential Provider 활성화 조건이 충족된다.

Windows 구성은 한 PC에 여러 휴대폰을 저장하고, `PreferredPhoneId`가 있으면 해당 휴대폰을 우선 사용한다. Android 구성은 여러 PC를 암호화된 목록으로 보관하고 선택된 PC의 WSS 연결만 유지한다. 모든 인증 결과에는 UTC 시각, 결과, 휴대폰 ID/이름, 요청 ID와 WSS 원격 IP를 남기며, 서명 불일치·등록되지 않은 연결·비정상 응답은 `Suspicious` 플래그를 기록한다.

선택형 자동 잠금은 Windows 서비스의 `AgentPipeService`가 대화형 사용자 세션의 `PhoneUnlock.Agent`와 보안 Named Pipe로 연결되는 구조다. Android는 10초 간격으로 인증된 WebSocket heartbeat를 보내고, 설정한 유예 시간 동안 heartbeat가 끊기면 대화형 에이전트가 `LockWorkStation`을 호출한다. GPS나 단순 Wi-Fi 이름은 사용하지 않는다. 별도로 켠 근접 자동 잠금 해제는 서비스가 `Global\PhoneUnlock.ProximityUnlock` 이벤트를 신호하고 Credential Provider가 이를 받아 heartbeat를 확인한 뒤 저장 자격 증명으로 자동 로그인한다.

## 개발용 수동 경로

`PhoneUnlock.Desktop`과 Android 메인 화면의 수동 JSON 영역은 네트워크 없이 canonical payload와 ECDSA 상호운용을 진단하기 위해 남아 있다. 실제 잠금 해제는 위 서비스 경로를 사용한다.

Credential Provider는 기존 PIN, 비밀번호, Windows Hello 공급자를 비활성화하지 않는다.
