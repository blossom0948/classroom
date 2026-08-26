# 0.4.0-beta.11

## V4 기능 확장

- LAN·VPN 주소 후보를 한 번에 페어링하고 Android가 연결 경로를 자동 재시도합니다.
- Tailscale/WireGuard 사설 VPN으로 PC와 휴대폰이 서로 다른 장소에 있어도 연결할 수 있습니다.
- 원격 절전·최대 절전·재시작·종료와 LAN Wake-on-LAN을 추가했습니다.
- Smart Arrival은 휴대폰이 돌아오면 생체인증을 요청한 뒤 승인된 경우에만 잠금을 해제합니다.
- Windows 설정 앱에 보안 점검, 휴대폰 즉시 차단, 전체 휴대폰 차단, 자동 기능 일시 중지를 추가했습니다.
- 자동잠금 Agent가 Windows 트레이에 상주하며 설정 열기와 PC 잠금 메뉴를 제공합니다.
- Android 홈 화면 위젯과 빠른 설정 타일에서 선택한 PC의 잠금·잠금 해제를 실행할 수 있습니다.
- 연결·인증·전원 요청 결과와 원격 IP를 기존 감사 기록에 남깁니다.

## 설치

1. Windows에서 `PhoneUnlock-Setup.exe`를 실행합니다. 설치 중 UAC에서 **예**를 눌러야 합니다.
2. Android에 `PhoneUnlock-Android.apk`를 설치하고 알림·자동 팝업 권한을 허용합니다.
3. 같은 LAN이면 바로 QR을 스캔합니다. 다른 장소에서 쓸 때는 PC와 Android에 Tailscale 또는 WireGuard를 연결한 뒤 QR을 다시 만듭니다.
4. 원격 연결의 자세한 조건은 [REMOTE_CONNECTION.md](REMOTE_CONNECTION.md)를 확인합니다.

공용 인터넷에 Phone Unlock 포트를 직접 공개하는 방식은 지원하지 않습니다. Credential Provider를 실제 로그인에 사용하기 전에는 Windows 기본 PIN·비밀번호 복구 경로를 확인하세요.
