# 학교 관리 장치 운영 가이드

## 이 설치 앱이 적용하는 범위

학생용 설치 앱은 `C:\Program Files\Blossom Classroom Student`에 학생 서비스와 화면 앱을 설치하고, `ClassroomStudentService`를 자동 시작 Windows 서비스로 등록합니다. 서비스 복구 동작도 등록하며, 로그인한 학생 계정에는 학생 화면을 다시 연결하는 시작 항목을 만듭니다.

따라서 설치가 정상적으로 끝난 장치는 Windows를 다시 시작한 뒤에도 서비스가 먼저 연결을 준비하고, 학생 화면은 로그인한 사용자 세션에서 다시 열려야 합니다. 교사 콘솔의 학생 상세 정보에는 연결된 설치 앱 버전과 자동 연결 설정을 표시합니다.

학교 IT 담당자는 장치에서 다음 읽기 전용 점검을 실행할 수 있습니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\enterprise\Test-ClassroomDeviceReadiness.ps1
```

이 스크립트는 서비스, 설치 파일, 현재 사용자 자동 시작 항목만 읽고 시스템 설정을 바꾸지 않습니다.

## 앱이 보장할 수 있는 것과 학교 정책이 필요한 것

Classroom은 일반적인 창 닫기에는 관리자 종료 비밀번호 확인을 사용하고, 설치 후 자동 연결 상태를 표시합니다. 그러나 일반 앱이나 자체 감시 루프만으로 Windows 관리자를 포함한 사용자의 작업 관리자 종료, 제거, 정책 변경을 안전하게 차단하는 것은 보안 경계가 될 수 없습니다. 이를 앱 안에서 우회하거나 숨기는 방식은 사용하지 않습니다.

학교 소유 장치에서 필요한 보호는 Windows와 MDM이 적용해야 합니다.

1. 학생은 표준 사용자 계정으로 사용하고, 로컬 관리자 권한은 학교 IT만 가집니다.
2. 학교 테넌트에 장치를 Intune 또는 Intune for Education으로 등록합니다. 앱 배포, 업데이트 상태, 보안 기준을 중앙에서 확인합니다.
3. 수업 전용 장치라면 Assigned Access로 허용된 앱 중심의 제한된 사용 경험을 구성합니다. 일반 수업 장치에는 수업에 필요한 앱을 먼저 검증합니다.
4. App Control for Business 또는 AppLocker는 먼저 감사(Audit) 모드로 적용하고, Classroom의 서명된 배포 파일과 필요한 교육 앱이 허용되는지 확인한 뒤 강제 모드로 전환합니다.
5. 서비스가 중지되거나 장치가 오프라인이면 정책 차단 대신 Intune 알림·인벤토리·현장 점검 절차로 처리합니다.

학교가 자체 Intune 테넌트와 관리자 권한을 보유했다면 학교 IT 담당자가 진행합니다. 교육청이 테넌트, 라이선스, Windows 보안 정책 또는 장치 조달을 중앙 관리한다면 학교는 요구사항과 장치 목록을 교육청 정보화 부서에 전달하고, 교육청이 정책 배포를 진행해야 합니다.

## 실제 기기 수용 테스트

배포 전 최소 한 대씩 다음을 확인합니다.

- 재부팅 후 서비스가 `Running`, 시작 유형이 `Auto`인지
- 학생이 로그인한 뒤 학생 화면이 연결 상태를 표시하는지
- 학생 화면의 X 또는 종료 버튼에서 종료 비밀번호 확인 창이 보이는지
- 교사가 수업 중 화면 보기를 켜면 학생 앱에 공유 중 상태가 표시되는지
- 100%, 125%, 150%, 200% 배율과 1366×768, 1920×1080에서 글자·버튼이 잘리지 않는지
- 키보드 탭 이동, Windows 고대비, 화면 읽기 도구, 터치 입력에서 핵심 버튼을 사용할 수 있는지
- 20명 이상 수업에서 모니터 벽의 페이지와 전체 화면 전환이 읽기 쉬운지

## Microsoft 공식 참고

- [Microsoft Intune 개요](https://learn.microsoft.com/en-us/intune/fundamentals/what-is-intune)
- [Intune for Education 장치 등록](https://learn.microsoft.com/en-us/intune-education/how-should-i-enroll-devices)
- [Windows Assigned Access](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/)
- [App Control for Business](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/appcontrol)
- [App Control 정책을 감사 모드에서 검증하기](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/deployment/audit-appcontrol-policies)
