# 원격 연결과 PC 깨우기

## 평상시 원격 연결

휴대폰은 연결 코드에 포함된 LAN·Tailscale·기타 사설 경로를 순서대로 시도합니다. Tailscale은 Phone Unlock과 같은 사설 경로를 제공하지만, 공용 포트를 열지 않는 구성이 기본입니다.

Android Tailscale 앱에서 `프로필 → 앱 기반 분할 터널링`으로 들어가 Samsung Wallet을 제외하면 Tailscale을 켠 상태에서도 Wallet 트래픽은 일반 네트워크로 나갈 수 있습니다. 이 기능은 Tailscale Android 1.70 이상에서 제공됩니다.

## 외부에서 PC 켜기

WOL은 같은 LAN의 브로드캐스트 영역에서 동작합니다. 꺼진 PC에는 Tailscale 클라이언트도 실행되어 있지 않으므로, 휴대폰에서 보낸 UDP 패킷만으로 집 안 PC를 깨울 수 없습니다. Tailscale의 공식 WOL 안내도 Tailscale이 Layer 3이고 WOL 매직 패킷은 Layer 2이므로 상시 전원 릴레이가 필요하다고 설명합니다.

권장 순서는 다음과 같습니다.

1. 공유기 관리 앱에 WOL 기능이 있으면 공유기의 WOL을 사용합니다.
2. NAS, Raspberry Pi, 미니 PC처럼 항상 켜진 장치에 UpSnap 같은 WOL 릴레이를 설치하고 Tailscale로 그 장치에만 접속합니다.
3. Phone Unlock에는 이후 릴레이의 HTTPS 주소를 등록하고, PC별 MAC 주소는 릴레이에만 보관합니다.

SmartThings Station은 Zigbee/Thread/Matter 장치를 관리하는 허브이지만 일반적인 WOL UDP 중계기로 동작하지 않습니다. Station만으로 임의 PC를 깨우는 것을 앱에서 자동으로 약속하지 않습니다. SmartThings 계정 연동은 OAuth 2.0과 공개 HTTPS 콜백 서버가 필요한 별도 통합입니다.

## 보안 주의

WOL 릴레이를 인터넷에 그대로 노출하지 말고 Tailscale ACL 또는 HTTPS 인증 뒤에 둡니다. PC가 켜진 뒤에는 Phone Unlock이 릴레이가 아니라 PC의 사설 주소로 자동 전환하게 구성합니다.
