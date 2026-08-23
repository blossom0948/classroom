# Troubleshooting

## .NET 8 SDK를 찾을 수 없음

`dotnet --list-sdks`에 8.x가 있어야 한다. 런타임만 설치되어 있으면 빌드할 수 없다.

## Android 빌드가 JDK를 찾지 못함

AGP 9.0.1은 JDK 17과 Gradle 9.1.0이 필요하다. `JAVA_HOME`을 JDK 17 폴더로 설정하고 새 터미널에서 다시 실행한다.

## 생체인증 사용 불가

기기에 PIN/패턴 같은 보안 잠금과 강한 생체인증을 먼저 등록한다. 기기가 `BIOMETRIC_STRONG`을 제공하지 않으면 Phase 1 앱은 서명을 허용하지 않는다.

## Key permanently invalidated

생체정보를 추가/삭제하면 기존 키가 무효화될 수 있다. 앱에서 키를 다시 만든 뒤 Windows에 새 공개키를 사용한다. 향후 pairing 구현에서는 키 교체를 명시적으로 승인하게 된다.

## 서명 검증 실패

- Windows의 최신 요청과 Android에 붙여 넣은 요청이 같은지 확인한다.
- 30초 이내에 승인한다.
- 공개키와 응답 JSON을 줄임 없이 복사한다.
- Base64 문자열에 따옴표나 공백을 추가하지 않는다.

## Windows 로그인 문제

현재 Phase 1은 로그인 시스템을 수정하지 않는다. 향후 Credential Provider 테스트에서도 기본 PIN/비밀번호를 유지하고, 문제가 생기면 기본 로그인 옵션을 사용한다.
