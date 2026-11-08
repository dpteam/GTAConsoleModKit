@echo off
setlocal
rem  Starts the web UI. Override port/bind with:  run.cmd --port 44800 --bind 0.0.0.0
set "ROOT=%~dp0"
if not exist "%ROOT%bin\ModKit.exe" (
  echo ModKit.exe not found, building first...
  call "%ROOT%build.cmd" || exit /b 1
)
cd /d "%ROOT%bin"
ModKit.exe serve %*
endlocal