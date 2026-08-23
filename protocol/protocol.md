# Phone Unlock Protocol v1

이 문서는 Phase 1과 이후 네트워크 전송에서 공통으로 사용하는 메시지 및 서명 규격의 기준 문서다. 모든 JSON은 UTF-8로 인코딩한다. 알 수 없는 필드는 향후 호환성을 위해 무시할 수 있지만, 필수 필드 누락과 알 수 없는 `version`은 거부한다.

## 공통 envelope

```json
{
  "version": 1,
  "type": "AUTH_REQUEST",
  "messageId": "d0bb3560-67f2-4d70-8461-0c43d0f96273",
  "timestamp": 1787490000,
  "payload": {}
}
```

| 필드 | 형식 | 규칙 |
|---|---|---|
| `version` | integer | 현재 값은 `1` |
| `type` | string | 아래에 정의된 대문자 메시지 종류 |
| `messageId` | UUID | 메시지마다 새 UUID, 소문자 canonical 문자열 권장 |
| `timestamp` | integer | UTC Unix seconds |
| `payload` | object | 메시지별 payload |

지원 종류:

```text
PAIR_REQUEST
PAIR_RESPONSE
DEVICE_HELLO
DEVICE_STATUS
AUTH_REQUEST
AUTH_APPROVED
AUTH_DENIED
AUTH_EXPIRED
PING
PONG
```

## AUTH_REQUEST

```json
{
  "version": 1,
  "type": "AUTH_REQUEST",
  "messageId": "d0bb3560-67f2-4d70-8461-0c43d0f96273",
  "timestamp": 1787490000,
  "payload": {
    "requestId": "c6a60298-33c4-49dc-b1ed-b1a046fa7347",
    "challenge": "QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=",
    "createdAt": 1787490000,
    "expiresAt": 1787490030,
    "computerId": "e66aa175-932a-4986-8b7d-1156640470a1",
    "computerName": "MY-PC"
  }
}
```

- `requestId`: 인증 시도마다 생성하는 UUID
- `challenge`: 암호학적 난수 32바이트의 표준 Base64(RFC 4648, padding 포함)
- `createdAt`: UTC Unix seconds
- `expiresAt`: `createdAt + 30`이 기본이며 수신 시각보다 커야 함
- `computerId`: 페어링된 PC의 UUID
- `computerName`: 표시용 이름이며 서명 대상이 아님

Android는 envelope version/type, UUID, Base64 길이, 최대 허용 수명 30초를 확인한 뒤에만 생체인증을 연다. 기기 시계 차이는 사전 검사에서 최대 ±30초 허용하지만 PC가 최종 만료를 강제한다.

## 서명 canonical payload

서명 입력은 JSON을 재직렬화한 값이 아니다. 아래 다섯 줄을 정확히 연결한 UTF-8 바이트다.

```text
PHONE-UNLOCK-V1
requestId=<requestId의 소문자 D 형식 UUID>
computerId=<computerId의 소문자 D 형식 UUID>
challenge=<AUTH_REQUEST의 Base64 문자열 그대로>
expiresAt=<10진수 Unix seconds>
```

규칙:

- 필드 순서는 고정한다.
- 줄 구분자는 LF 한 바이트(`0A`)만 사용한다.
- 마지막 줄 뒤에는 줄바꿈을 붙이지 않는다.
- 문자열 전체를 UTF-8(BOM 없음)로 인코딩한다.
- UUID는 하이픈이 포함된 소문자 `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` 형식이다.
- challenge는 디코딩 후 32바이트인지 확인하되 canonical payload에는 원래의 표준 Base64 표현을 넣는다.
- 정수는 부호 없는 10진수 표현을 사용한다.

고정 예시:

```text
PHONE-UNLOCK-V1
requestId=c6a60298-33c4-49dc-b1ed-b1a046fa7347
computerId=e66aa175-932a-4986-8b7d-1156640470a1
challenge=QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=
expiresAt=1787490030
```

## 서명 알고리즘과 키 형식

- curve: NIST P-256 / `secp256r1`
- signature: ECDSA with SHA-256 / Android `SHA256withECDSA`
- signature encoding: ASN.1 DER SEQUENCE `(r, s)`, 표준 Base64
- public key encoding: X.509 `SubjectPublicKeyInfo` DER, 표준 Base64
- private key: Android Keystore 내부 non-exportable 키

Windows의 `ECDsa.VerifyData`는 `DSASignatureFormat.Rfc3279DerSequence`를 명시한다.

## AUTH_APPROVED

```json
{
  "version": 1,
  "type": "AUTH_APPROVED",
  "messageId": "94145d99-e1f4-403a-8675-83caac130dce",
  "timestamp": 1787490004,
  "payload": {
    "requestId": "c6a60298-33c4-49dc-b1ed-b1a046fa7347",
    "computerId": "e66aa175-932a-4986-8b7d-1156640470a1",
    "challenge": "QkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkJCQkI=",
    "expiresAt": 1787490030,
    "phoneId": "phone-installation-uuid",
    "signature": "MEUCIQ..."
  }
}
```

PC 검증 순서:

1. envelope version/type 확인
2. `requestId`가 pending인지 확인
3. `computerId`, challenge, `expiresAt`가 원 요청과 정확히 같은지 확인
4. 현재 시각이 만료 전인지 확인
5. 페어링되어 활성화된 `phoneId`의 저장 공개키 선택
6. canonical payload를 재생성하고 DER ECDSA 서명 검증
7. 성공한 `requestId`를 원자적으로 1회 소비
8. 이후 같은 응답은 replay로 거부

잘못된 서명 자체는 pending 요청을 소비하지 않는다. 단, rate limit과 보안 로그를 적용한다.

## AUTH_DENIED / AUTH_EXPIRED

```json
{
  "version": 1,
  "type": "AUTH_DENIED",
  "messageId": "810ece35-b0ca-4f47-9488-0f2b4b3d541a",
  "timestamp": 1787490003,
  "payload": {
    "requestId": "c6a60298-33c4-49dc-b1ed-b1a046fa7347",
    "computerId": "e66aa175-932a-4986-8b7d-1156640470a1",
    "reason": "USER_DENIED"
  }
}
```

허용 reason 예시: `USER_DENIED`, `BIOMETRIC_CANCELLED`, `BIOMETRIC_FAILED`, `REQUEST_EXPIRED`, `KEY_INVALIDATED`.

## Pairing v1

설정 앱이 보여 주는 payload는 Windows 자격 증명을 포함하지 않는다.

```json
{
  "version": 1,
  "computerId": "e66aa175-932a-4986-8b7d-1156640470a1",
  "computerName": "MY-PC",
  "pairingToken": "base64url-32-random-bytes",
  "host": "192.168.0.10",
  "port": 48231,
  "expiresAt": 1787490120,
  "certificateFingerprint": "64자리 SHA-256 hex"
}
```

Android는 fingerprint가 정확히 일치하는 TLS 인증서로 `POST /pair`를 호출하고 `X-Pairing-Token` 헤더를 보낸다. body에는 `phoneId`, 표시용 `phoneName`, P-256 SubjectPublicKeyInfo `publicKey`가 들어간다. 성공 응답은 `computerId`, `phoneId`, 256비트 `deviceToken`, port와 같은 fingerprint를 반환한다.

pairing token은 256비트 난수이며 2분 뒤 만료되고 한 번만 소비된다. 이후 전송은 `wss://<host>:48231/ws?phoneId=...`와 `Authorization: Bearer <deviceToken>`을 사용한다. Android는 device token을 Keystore AES-GCM 키로 암호화해 저장하고 PC는 token의 SHA-256 hash만 저장한다.

## 시간과 replay

- 인증 기본 timeout: 30초
- pairing 기본 timeout: 120초
- PC가 인증 request의 유일한 시간 기준을 생성한다.
- Android는 로컬 시계로 사전 만료를 확인하고 PC는 최종 만료를 강제한다.
- 성공한 request ID와 challenge는 즉시 폐기한다.
- 만료 항목은 메모리/저장소에서 주기적으로 정리하되, 이미 성공한 request ID의 짧은 replay tombstone을 유지한다.
