# 0.4.0-beta.12

## 화면과 사용성

- Android 앱을 어두운 카드형 화면으로 정리하고 PC·자동화·기록 하단 탭을 추가했습니다.
- PC 상세 화면에서 원격 잠금·잠금 해제·전원 제어를 바로 사용할 수 있습니다.
- Android 뒤로가기는 설정·기록 화면에서 PC 화면으로 돌아가며, 화면 전환에 짧은 애니메이션을 적용했습니다.
- Windows 설정 앱도 어두운 카드형 스타일로 통일하고 뒤로가기 버튼, Esc, Alt+←로 휴대폰 목록으로 돌아가도록 했습니다.
- 휴대폰에서 실행한 PC 연결·잠금·잠금 해제·전원 요청을 기록 탭에서 확인할 수 있습니다.

## 연결·자동화

- QR에 포함된 LAN·VPN 주소 후보를 Android가 자동으로 순서대로 시도합니다.
- Android 진단에 현재 VPN 연결 여부와 저장된 주소 후보 사용 상태를 표시합니다.
- Windows 재실 센서 설정에 현재 상태 테스트 버튼을 추가했습니다. Home Assistant 또는 SmartThings API가 실제로 응답하는지 바로 확인할 수 있습니다.
- Wake-on-LAN을 지정 브로드캐스트와 전체 브로드캐스트, UDP 7·9번 포트로 짧게 반복 전송하도록 보강했습니다.

## 알려진 조건

- VPN은 Phone Unlock이 몰래 설치하거나 로그인할 수 없습니다. PC와 Android에 Tailscale 또는 WireGuard를 한 번 설치·로그인하고, VPN이 연결된 상태에서 QR을 다시 만들면 이후 주소 후보를 자동 선택합니다.
- PC가 완전히 꺼진 상태의 원격 켜기는 PC BIOS/UEFI, 유선 네트워크 카드, 공유기의 Wake-on-LAN 전달 설정이 모두 필요합니다. VPN만으로 다른 장소의 LAN 브로드캐스트를 전달할 수는 없습니다.
- 재실 센서는 센서 자체가 Zigbee/Matter로 연결되어 있다는 것만으로 Phone Unlock에 직접 노출되지 않습니다. Home Assistant 또는 SmartThings API에 센서가 등록되어 있어야 하며, SmartThings Station은 계정 API 인증이 필요합니다.
- Credential Provider와 재실 자동 잠금은 Windows 기본 PIN·비밀번호 복구 경로를 확인한 뒤 사용하세요.
