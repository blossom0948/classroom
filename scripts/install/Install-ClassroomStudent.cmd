@echo off
setlocal EnableExtensions

set "PACKAGE_ROOT=%~dp0"
set "ENROLLMENT=%~1"

if not defined ENROLLMENT (
  for /f "delims=" %%F in ('dir /b /a-d "%PACKAGE_ROOT%classroom-enrollment-*.json" 2^>nul') do (
    if defined FOUND set "MULTIPLE=1"
    set "FOUND=%PACKAGE_ROOT%%%F"
  )
  if defined MULTIPLE (
    echo 등록 파일이 여러 개입니다. 사용할 JSON 파일을 이 파일 위로 끌어다 놓고 다시 실행하세요.
    pause
    exit /b 2
  )
  if not defined FOUND (
    echo classroom-enrollment-*.json 파일을 이 패키지 폴더에 넣어 주세요.
    pause
    exit /b 2
  )
  set "ENROLLMENT=%FOUND%"
) else (
  if not exist "%ENROLLMENT%" if exist "%PACKAGE_ROOT%%~1" set "ENROLLMENT=%PACKAGE_ROOT%%~1"
)

echo Classroom 학생 앱 설치를 위해 관리자 권한 확인 창을 엽니다.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File','%PACKAGE_ROOT%Install-ClassroomStudent.ps1','-PackageRoot','%PACKAGE_ROOT%','-EnrollmentFile','%ENROLLMENT%'); exit $process.ExitCode"
if errorlevel 1 (
  echo 설치가 완료되지 않았습니다. 관리자 권한 또는 학교 장치 정책을 확인하세요.
  pause
  exit /b 1
)
echo.
echo 설치가 완료되었습니다. 학생용 상태 창이 알림 영역에서 실행됩니다.
pause
