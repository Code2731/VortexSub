@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-tabby.ps1" %*
set "runExit=%errorlevel%"
if not "%runExit%"=="0" pause
exit /b %runExit%
