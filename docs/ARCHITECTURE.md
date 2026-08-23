# Architecture

## Phase 1

```text
Windows Desktop
  challenge 생성 + pending 저장
      ↓ 요청 JSON 수동 전달
Android Phone Unlock
  요청 검증 → BiometricPrompt → Keystore 서명
      ↓ 공개키 + 응답 JSON 수동 전달
Windows Core
  요청 일치/만료 확인 → ECDSA 검증 → 1회 소비
```

수동 전달은 네트워크와 암호 로직을 분리해 먼저 검증하기 위한 개발 경로다. Desktop 앱은 실제 Windows 잠금 상태나 자격 증명에 접근하지 않는다.

## 이후 단계

1. 2분 만료 pairing token과 공개키 등록
2. 인증서 fingerprint pinning을 적용한 LAN WebSocket
3. 최소 권한 Windows Service와 로컬 Named Pipe IPC
4. VM에서 복구 절차를 먼저 검증한 Credential Provider V2
5. 테스트 성공 후에만 선택적으로 활성화하는 설치 프로그램

Credential Provider는 기존 PIN, 비밀번호, Windows Hello 공급자를 비활성화하지 않는다.
