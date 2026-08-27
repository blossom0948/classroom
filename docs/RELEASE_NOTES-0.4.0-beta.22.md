# 0.4.0-beta.22

## 수정

- PC 설정창이 XAML 로드 전에 저장된 테마 브러시를 변경해 종료되던 경로를 제거했습니다. 설치된 새 버전은 XAML을 먼저 로드하고, 시작 예외를 사용자에게 표시하면서 `setup-startup.log`에 기록합니다.
- 휴대폰·Windows 업데이트 확인을 GitHub REST API 단독 의존에서 저장소의 공개 `update.json` 매니페스트 우선 방식으로 바꿨습니다. 공유 IP에서 GitHub API 호출 제한을 받아도 업데이트 확인이 동작합니다.
- 업데이트 다운로드 주소는 HTTPS, 공식 저장소, 예상 APK/설치 파일 경로인지 확인한 뒤에만 엽니다.
- 설치 앱의 업데이트 확인도 동일한 공개 매니페스트를 사용하고, API는 호환성 fallback으로만 남겼습니다.

## 설치

- Windows는 `PhoneUnlock-Setup.exe`를 실행해 beta.22로 덮어 설치해야 설정창 수정이 반영됩니다.
- Android는 앱 설정의 업데이트 버튼에서 새 APK를 내려받아 Android의 설치 확인을 한 번 눌러야 합니다.
