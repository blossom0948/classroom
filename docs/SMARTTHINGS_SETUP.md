# SmartThings Station 재실 센서 연결

Phone Unlock은 PC에서 SmartThings Station에 직접 Zigbee/Matter 무선 연결을 하지 않습니다. Station에 등록된 센서의 상태를 SmartThings API로 읽습니다. 따라서 PC에 Zigbee 동글이 없어도 되지만, PC는 인터넷에 연결되어 있어야 합니다.

## 준비

1. SmartThings 앱에서 SmartThings Station을 허브로 등록합니다.
2. 사람이 있는지 판단할 센서를 Station에 추가합니다. `occupancy`, `presence`, `motion` capability가 있는 센서를 사용할 수 있습니다.
3. 현재 버전은 개인용 [SmartThings Personal Access Token](https://account.smartthings.com/tokens)을 사용하므로 기기 읽기 권한을 허용한 토큰을 준비합니다. Windows 설정의 `센서 연결 상세` 안에 토큰 입력이 접혀 있습니다.

## Phone Unlock 설정

1. Windows 설정 앱에서 PC를 선택하고 `사람이 없으면 PC 잠금`을 켭니다.
2. 연결 방식에서 `SmartThings Station`을 선택합니다.
3. `센서 연결 상세`를 열고 SmartThings 토큰을 입력한 뒤 `센서 목록 불러오기`를 누릅니다.
4. 목록에서 센서를 선택하고 감지 해제 후 잠글 시간을 고릅니다.
5. `SmartThings 재실 센서 연동 완료`가 표시되면 저장된 토큰은 Windows Credential Manager에 보관됩니다.

## 계정 로그인 방식의 한계

SmartThings 계정 로그인(OAuth)은 로컬 PC 앱이 SmartThings 사이트의 로그인 세션을 읽는 방식이 아닙니다. 공식 OAuth를 사용하려면 SmartThings API Access App, Client ID/Secret, 공개 HTTPS callback이 필요합니다. 따라서 이 저장소에는 가짜 계정 로그인이나 사이트 스크래핑을 넣지 않았고, 현재는 개인용 토큰 방식으로 동작합니다. OAuth 자격 증명과 callback 서버를 준비하면 토큰을 직접 붙여 넣지 않는 연결 흐름으로 확장할 수 있습니다.

공식 흐름은 [SmartThings OAuth 문서](https://developer.smartthings.com/docs/service-integrations/oauth)와 [API Access App 설정 문서](https://developer.smartthings.com/docs/service-integrations/app-setup)를 기준으로 합니다.
