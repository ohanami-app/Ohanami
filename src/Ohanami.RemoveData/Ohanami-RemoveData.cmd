@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dpn0.ps1" %*
set "OHANAMI_EXIT=%ERRORLEVEL%"
pause
exit /b %OHANAMI_EXIT%
