@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File ""%~dp0Install-PhoneUnlock.ps1""'"
if errorlevel 1 (
  echo.
  echo Phone Unlock installation did not complete.
  pause
)
endlocal
