# 0.4.0-beta.8

## 이번 버전

- Android PC 상세 화면에 `PC 잠금`과 `잠금 해제` 버튼을 추가했습니다.
- Android 홈 화면 위젯에서 선택한 PC를 잠그거나 생체인증 후 잠금 해제할 수 있습니다.
- 휴대폰 원격 잠금 요청은 인증된 WSS 연결과 PC·휴대폰·만료 시각·replay 검사를 거친 뒤 Windows 에이전트로 전달됩니다.
- Android 설정 화면을 PC 목록 → PC 상세 설정 순서로 정리하고, 진단 화면에서 불필요한 알림·배터리 바로가기 버튼을 제거했습니다.
- Windows 설정 화면을 연결된 휴대폰 목록 중심으로 다시 배치하고, 계정 암호와 센서 토큰 입력을 접힌 고급 영역으로 옮겼습니다.
- 버튼과 카드의 대비를 높이고, 원격 동작·인증·자동화·재실 센서·보안 기록을 분리해 읽기 쉽게 정리했습니다.

## SmartThings 계정 연결

이번 버전은 SmartThings 토큰 입력을 기본 화면에서 숨겨 설정을 단순화했습니다. SmartThings Station의 Zigbee/Matter 센서를 직접 읽는 기능은 기존처럼 동작하지만, 현재 연결은 개인용 API 토큰 방식입니다.

SmartThings 계정 로그인(OAuth)을 로컬 앱만으로 추가하려면 SmartThings API Access App의 Client ID/Secret과 공개 HTTPS 콜백 서버가 필요합니다. 해당 자격 증명과 콜백을 준비하면 다음 버전에서 브라우저 한 번 로그인 방식으로 연결할 수 있습니다.

## 업데이트 후

1. Windows에서 `PhoneUnlock-Setup.exe`를 설치하고 UAC에서 **예**를 누릅니다.
2. Android에서 최신 APK를 설치합니다. 기존 beta.7 release APK는 서명이 같아 연결 정보를 유지한 채 업데이트할 수 있습니다.
3. Android 홈 화면을 길게 눌러 **Phone Unlock 위젯**을 추가하고 선택한 PC의 `잠금 해제` 또는 `PC 잠금`을 사용합니다.
4. 원격 잠금이 동작하지 않으면 Windows 설정에서 `자동잠금 감시 시작`을 한 번 눌러 Phone Unlock Agent를 실행합니다.
