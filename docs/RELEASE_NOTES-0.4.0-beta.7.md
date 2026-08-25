# 0.4.0-beta.7

## 이번 버전

- Windows와 Android 설정 화면을 PC·휴대폰 목록 중심으로 단순화했습니다.
- Windows 잠금화면 문구를 `휴대폰에서 생체인식으로 잠금 해제`로 정리했습니다.
- Android 로그인 요청은 전체 화면 생체인증 창을 우선 표시하고, Android가 제한하는 경우에만 알림으로 대체합니다.
- Android에서 PC 원격 잠금 해제를 생체인증 후 요청할 수 있습니다.
- 로그인 성공·실패, 연결 거부, 의심스러운 요청을 Windows 보안 기록에 남기고 휴대폰에 의심 연결 알림을 보냅니다.
- SmartThings Station 연동을 추가했습니다. SmartThings에 연결된 Zigbee/Matter 재실 센서를 검색해 선택할 수 있습니다.
- Home Assistant를 통한 Zigbee/Matter entity 연동도 유지합니다.
- 재실 센서가 감지 해제된 뒤 기본 10초가 지나면 PC를 잠급니다.
- Bluetooth RSSI 비콘으로 휴대폰 거리를 확인하고, 선택한 기준보다 멀어지면 자동 잠금할 수 있습니다.
- 표준·집·외출 자동 잠금 프로필과 여러 휴대폰/PC 선택 화면을 추가했습니다.

## SmartThings Station 사용

자세한 절차는 `docs/SMARTTHINGS_SETUP.md`를 확인하세요. SmartThings Station에 재실·동작 센서를 먼저 등록한 뒤 Windows 설정에서 `SmartThings Station` → 토큰 입력 → `SmartThings 센서 찾기` 순서로 선택하면 됩니다.

## 업데이트 후

1. Windows에서 `PhoneUnlock-Setup.exe`를 설치하고 UAC에서 **예**를 누릅니다.
2. Android에서 최신 APK를 설치하고 알림, Bluetooth 권한, 배터리 제한 해제를 허용합니다.
3. 자동 잠금을 사용할 PC에서 설정 앱의 `자동잠금 감시 시작`을 누릅니다.
4. SmartThings 재실 센서를 사용할 경우 센서 연결 상태와 API 토큰 만료 여부를 확인합니다.
