# SmartThings Station 재실 센서 연결

Phone Unlock은 PC에서 SmartThings Station에 직접 Zigbee/Matter 무선 연결을 하지 않습니다. Station에 등록된 센서의 상태를 SmartThings API로 읽습니다. 따라서 PC에 Zigbee 동글이 없어도 되지만, PC는 인터넷에 연결되어 있어야 합니다.

## 준비

1. SmartThings 앱에서 SmartThings Station을 허브로 등록합니다.
2. 사람이 있는지 판단할 센서를 Station에 추가합니다. `occupancy`, `presence`, `motion` capability가 있는 센서를 사용할 수 있습니다.
3. 현재 직접 연결은 [SmartThings Personal Access Token](https://account.smartthings.com/tokens)을 사용합니다. 기기 읽기 권한(`r:devices:*`)만 허용한 토큰을 준비합니다.

## 가장 간단한 연결

1. Windows 설정 앱에서 PC를 선택하고 `사람이 없으면 PC 잠금`을 켭니다.
2. 연결 방식에서 `SmartThings Station`을 선택합니다.
3. `SmartThings 센서 · 센서 자동 연결`을 누릅니다.
4. 첫 연결에서만 고급 설정이 열립니다. 토큰을 한 번 입력하고 `센서 자동 연결`을 다시 누릅니다.
5. 센서가 하나면 바로 연결되고, 여러 개면 이름만 선택한 뒤 `선택한 센서로 연결`을 누릅니다.
6. 감지 해제 후 잠글 시간만 고르면 됩니다. 토큰은 Windows Credential Manager에 보관되고 화면에는 다시 표시되지 않습니다.

주소, device ID, component, capability, attribute를 직접 입력할 필요가 없습니다. 센서 목록은 `occupancySensor`, `presenceSensor`, `motionSensor`를 자동으로 검색합니다.

## 토큰을 아예 쓰지 않는 방법

SmartThings Station의 Zigbee/Matter 기기를 PC 프로그램이 토큰 없이 LAN에서 직접 읽는 공개 인터페이스는 제공되지 않습니다. 다음 대안 중 하나를 사용해야 합니다.

- **휴대폰 Bluetooth RSSI**: 별도 센서·계정·토큰이 없습니다. Windows 설정의 `Bluetooth 거리 기준 사용`을 켜고 기준 거리만 고르면 됩니다. 화장실처럼 휴대폰을 들고 이동하는 상황에는 이 방식이 가장 간단합니다.
- **Windows Dynamic Lock**: Windows 기본 기능입니다. 휴대폰을 Windows Bluetooth에 페어링하면 멀어질 때 약 1분 안에 PC를 잠급니다. 잠금 해제까지 자동으로 하지는 않습니다. [Microsoft Dynamic Lock 문서](https://learn.microsoft.com/en-us/windows/security/identity-protection/hello-for-business/hello-feature-dynamic-lock)
- **Home Assistant**: 이미 Home Assistant를 쓰고 있다면 SmartThings 통합이 기기를 entity로 자동 노출할 수 있습니다. 다만 Home Assistant 주소와 장기 토큰은 한 번 필요합니다. [Home Assistant SmartThings 통합](https://www.home-assistant.io/integrations/smartthings)
- **PC 직결 Zigbee/Matter**: USB Zigbee 동글 또는 별도 Matter 컨트롤러를 PC에 연결하는 방식입니다. SmartThings Station을 PC용 동글처럼 재사용하는 방식은 아닙니다.

## 계정 로그인 방식의 한계

SmartThings 계정 로그인(OAuth)은 로컬 PC 앱이 SmartThings 사이트의 로그인 세션을 읽는 방식이 아닙니다. 공식 OAuth를 사용하려면 SmartThings API Access App, Client ID/Secret, 공개 HTTPS callback이 필요합니다. 따라서 이 저장소에는 가짜 계정 로그인이나 사이트 스크래핑을 넣지 않았고, 현재는 개인용 토큰 방식으로 동작합니다. OAuth 자격 증명과 callback 서버를 준비하면 토큰을 직접 붙여 넣지 않는 연결 흐름으로 확장할 수 있습니다.

또한 SmartThings의 개인용 토큰은 현재 생성 시점에 따라 만료될 수 있습니다. 만료되면 `센서 자동 연결`을 다시 눌러 새 토큰만 입력하면 됩니다. 장기 연동은 SmartThings가 권장하는 OAuth Service Integration 방식이지만, 공개 HTTPS 서버와 API Access App 자격 증명이 필요합니다.

공식 흐름은 [SmartThings OAuth 문서](https://developer.smartthings.com/docs/service-integrations/oauth), [API Access App 설정 문서](https://developer.smartthings.com/docs/service-integrations/app-setup), [이벤트 구독·웹훅 문서](https://developer.smartthings.com/docs/service-integrations/subscribe-to-events)를 기준으로 합니다.
