<#
    One-shot CI: build, start the server, run the format and web test suites, stop it.

    Usage:  powershell -File tools\ci-test.ps1 [-Port 44790] [-SkipFormats]
    Exit code 0 only when every suite passes.
#>
param(
    [int]$Port = 44790,
    [switch]$SkipFormats
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'bin\ModKit.exe'
$logDir = Join-Path $root 'bin\logs'

function Fail([string]$text) {
    Write-Host ""
    Write-Host $text -ForegroundColor Red
    exit 1
}

Write-Host "== build ==" -ForegroundColor Cyan
Push-Location $root
& cmd /c build.cmd
$buildCode = $LASTEXITCODE
Pop-Location
if ($buildCode -ne 0) { Fail "build failed" }
if (-not (Test-Path $exe)) { Fail "ModKit.exe missing after build" }

Write-Host ""
Write-Host "== formats ==" -ForegroundColor Cyan
if ($SkipFormats) {
    Write-Host "  skipped by flag" -ForegroundColor DarkYellow
} else {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-formats.ps1')
    if ($LASTEXITCODE -ne 0) { Fail "format suite failed" }
}

Write-Host ""
Write-Host "== server ==" -ForegroundColor Cyan
$process = Start-Process -FilePath $exe -ArgumentList @('serve', '--port', $Port) `
    -WorkingDirectory (Join-Path $root 'bin') -PassThru -WindowStyle Hidden

$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 300
    try {
        $health = Invoke-RestMethod -Uri ("http://127.0.0.1:" + $Port + "/api/health") -UseBasicParsing -TimeoutSec 2
        if ($health.ok) { $ready = $true; break }
    } catch { }
}
if (-not $ready) {
    if (-not $process.HasExited) { $process.Kill() }
    Fail "server did not become healthy on port $Port"
}
Write-Host ("  listening on port " + $Port + " (pid " + $process.Id + ")") -ForegroundColor DarkGray

Write-Host ""
Write-Host "== web ==" -ForegroundColor Cyan
$webFailed = $false
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-web.ps1') -Port $Port
    if ($LASTEXITCODE -ne 0) { $webFailed = $true }
} finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
}

if ($webFailed) {
    Write-Host ""
    Write-Host "== log tail ==" -ForegroundColor Cyan
    $log = Get-ChildItem -Path $logDir -Filter 'modkit-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) { Get-Content $log.FullName -Tail 40 }
    Fail "web suite failed"
}

Write-Host ""
Write-Host "ALL SUITES PASSED" -ForegroundColor Green
exit 0