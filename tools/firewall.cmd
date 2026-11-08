@echo off
rem ============================================================
rem  DPT.PSP GTA Modding Kit - firewall rule for the web UI
rem  (спросит UAC - нажмите «Да»). Порт читается из bin\config.ini.
rem
rem  extra options:
rem    firewall.cmd -AnySource   разрешить порт с любых адресов (не только LAN)
rem    firewall.cmd -Remove      удалить правила DPT.PSP
rem ============================================================
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0firewall.ps1" %*
endlocal