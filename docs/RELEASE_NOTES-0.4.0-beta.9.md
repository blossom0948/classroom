# 0.4.0-beta.9

## 재실 센서 연결 간소화

- Windows 설정의 SmartThings Station 연결을 `센서 자동 연결` 중심으로 바꿨습니다.
- API 주소, device ID, component, capability, attribute를 직접 입력하지 않아도 됩니다.
- SmartThings 센서를 하나 찾으면 자동으로 저장하고, 여러 개면 센서 이름만 선택할 수 있습니다.
- 저장된 토큰이 없을 때만 고급 설정을 열어 Personal Access Token을 한 번 입력하도록 했습니다.
- 만료되거나 권한이 없는 SmartThings 토큰은 다시 입력해야 한다는 메시지를 표시합니다.
- SmartThings Station을 토큰 없이 PC에서 직접 읽는 공개 LAN 인터페이스는 없으므로, 토큰 없이 사용하려면 휴대폰 Bluetooth RSSI 또는 Windows Dynamic Lock을 사용할 수 있도록 문서를 정리했습니다.

## 업데이트 후

1. Windows에서 `PhoneUnlock-Setup.exe`를 설치하고 UAC에서 **예**를 누릅니다.
2. PC 설정에서 `사람이 없으면 잠금`과 `SmartThings Station`을 선택합니다.
3. `센서 자동 연결`을 누르고, 처음 한 번만 SmartThings 토큰을 입력합니다.
4. 토큰 입력 자체를 피하려면 `Bluetooth 거리 기준 사용`을 사용합니다.
