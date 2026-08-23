@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-PhoneUnlock.ps1"
if errorlevel 1 (
  echo.
  echo Phone Unlock installation failed.
  echo Log: %ProgramData%\PhoneUnlock\logs\install-latest.log
  pause
)
endlocal
