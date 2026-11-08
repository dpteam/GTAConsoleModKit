@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem  DPT.PSP GTA Modding Kit - build (no NuGet, no MSBuild)
rem  Targets .NET Framework 4.0 so it still runs on Windows XP SP3.
rem ============================================================

set "ROOT=%~dp0"
set "SRC=%ROOT%src\ModKit"
set "OUT=%ROOT%bin"

if not exist "%OUT%" mkdir "%OUT%"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe from .NET Framework 4.x was not found.
  echo         Install .NET Framework 4.0 or newer and try again.
  exit /b 1
)

echo [1/4] collecting sources...
if exist "%OUT%\sources.rsp" del "%OUT%\sources.rsp"
for /r "%SRC%" %%f in (*.cs) do echo "%%f">>"%OUT%\sources.rsp"

echo [2/4] compiling with %CSC%
"%CSC%" /nologo /target:exe /platform:anycpu /langversion:4 /optimize+ /debug:pdbonly ^
  /warn:4 /define:TRACE ^
  /out:"%OUT%\ModKit.exe" ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Xml.dll ^
  @"%OUT%\sources.rsp"

if errorlevel 1 (
  echo.
  echo [ERROR] build failed.
  echo         If the message mentions ModKit.exe being in use, close the running
  echo         instance or the web UI and build again.
  exit /b 1
)

echo [3/4] copying assets...
if exist "%OUT%\public" rmdir /s /q "%OUT%\public"
xcopy "%ROOT%public" "%OUT%\public\" /E /I /Y /Q >nul
if not exist "%OUT%\data" mkdir "%OUT%\data"
if exist "%ROOT%data\*.txt" copy "%ROOT%data\*.txt" "%OUT%\data\" /Y >nul
if exist "%ROOT%data\*.json" copy "%ROOT%data\*.json" "%OUT%\data\" /Y >nul
xcopy "%ROOT%config.example.ini" "%OUT%\" /Y /Q >nul
if not exist "%OUT%\logs" mkdir "%OUT%\logs"
if not exist "%OUT%\workspace" mkdir "%OUT%\workspace"

echo [4/4] done.
echo.
echo   exe      : %OUT%\ModKit.exe
echo   web      : run.cmd      then http://127.0.0.1:44700/
echo   headless : ModKit.exe identify somefile.chk
echo.
endlocal