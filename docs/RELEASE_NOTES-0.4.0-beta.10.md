# 0.4.0-beta.10

## 재실 센서 자동 잠금 해제

- 재실 센서가 사람을 다시 감지하면 Phone Unlock Credential Provider가 자동 잠금 해제를 시도합니다.
- `휴대폰 또는 재실 센서가 감지되면 자동 로그인` 옵션으로 켤 수 있습니다.
- SmartThings/Home Assistant API가 잠시 응답하지 않을 때는 이를 사람 없음으로 처리하지 않아 오탐 잠금 해제를 줄였습니다.
- 재실 센서가 감지 해제되면 기존처럼 설정한 대기 시간 뒤 PC를 잠급니다.

## 업데이트 후

1. Windows에서 `PhoneUnlock-Setup.exe`를 설치합니다.
2. `사람이 없으면 PC 잠금`과 `휴대폰 또는 재실 센서가 감지되면 자동 로그인`을 켭니다.
3. SmartThings Station을 사용하는 경우 `센서 자동 연결`로 센서를 연결합니다.
