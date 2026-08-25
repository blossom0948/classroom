# SmartThings Station 재실 센서 연결

Phone Unlock은 PC에서 SmartThings Station에 직접 Zigbee/Matter 무선 연결을 하지 않습니다. Station에 등록된 센서의 상태를 SmartThings API로 읽습니다. 따라서 PC에 Zigbee 동글이 없어도 되지만, PC는 인터넷에 연결되어 있어야 합니다.

## 준비

1. SmartThings 앱에서 SmartThings Station을 허브로 등록합니다.
2. 사람이 있는지 판단할 센서를 Station에 추가합니다. `occupancy`, `presence`, `motion` capability가 있는 센서를 사용할 수 있습니다.
3. [SmartThings Personal Access Token](https://account.smartthings.com/tokens)을 만들고 기기 읽기 권한을 허용합니다.

## Phone Unlock 설정

1. Windows 설정 앱에서 PC를 선택하고 `사람이 없으면 PC 잠금`을 켭니다.
2. 연결 방식에서 `SmartThings Station`을 선택합니다.
3. SmartThings 토큰을 붙여 넣고 `SmartThings 센서 찾기`를 누릅니다.
4. 목록에서 센서를 선택하고 감지 해제 후 잠글 시간을 고릅니다.
5. `SmartThings 재실 센서 연동 완료`가 표시되면 저장된 토큰은 Windows Credential Manager에 보관됩니다.

SmartThings API 토큰은 개인 테스트용으로 사용합니다. 장기적으로 여러 사용자가 쓰는 제품으로 확장할 때는 SmartThings OAuth 서비스 연동으로 교체해야 합니다.
