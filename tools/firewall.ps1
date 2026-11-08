# =========================================================================
#  DPT.PSP GTA Modding Kit - Windows Firewall setup
#
#  Adds an inbound TCP allow rule for the web UI so the board can be opened
#  from other machines on the LAN (phone, laptop, colleague's PC).
#
#    * port is read from bin\config.ini (server.webPort / webPortFallbacks)
#    * the rule is scoped to bin\ModKit.exe and recreated on every run
#    * requires admin rights: the script relaunches itself with a UAC prompt
#
#  usage (double click firewall.cmd, or):
#    powershell -ExecutionPolicy Bypass -File tools\firewall.ps1
#    ... -AnySource   allow the port from anywhere (default: LAN/private only)
#    ... -Remove      delete the DPT.PSP ModKit rules
# =========================================================================
[CmdletBinding()]
param(
    [switch]$AnySource,
    [switch]$Remove,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'bin\ModKit.exe'
$ini = Join-Path $root 'bin\config.ini'
$reportPath = Join-Path $env:TEMP 'dpt-modkit-firewall.log'
$report = New-Object 'System.Collections.Generic.List[string]'

function Add-Line([string]$text) {
    $report.Add($text)
    Write-Host $text
}

function Save-Report {
    try { Set-Content -Path $reportPath -Value $report -Encoding UTF8 } catch { }
}

# --------------------------------------------------------------- elevation

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
    if ($AnySource) { $argList += '-AnySource' }
    if ($Remove) { $argList += '-Remove' }
    if ($NoPause) { $argList += '-NoPause' }

    Write-Host "Нужны права администратора - сейчас появится запрос UAC (нажмите «Да»)." -ForegroundColor Yellow
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null
    } catch {
        Write-Host "Повышение прав отменено: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Запустите этот файл от имени администратора вручную." -ForegroundColor Red
    }
    Start-Sleep -Seconds 1
    exit
}

# ------------------------------------------------------------------- config

function Read-IniValues([string]$path) {
    $map = @{}
    if (-not (Test-Path $path)) { return $map }
    foreach ($line in Get-Content $path) {
        if ($line -match '^\s*([A-Za-z0-9_.]+)\s*=\s*(.*?)\s*$') {
            $map[$Matches[1].ToLowerInvariant()] = $Matches[2]
        }
    }
    return $map
}

$values = Read-IniValues $ini
$port = 44700
$fallbacks = @()
$bind = '0.0.0.0'
if ($values.ContainsKey('webport') -and $values['webport']) { $port = [int]$values['webport'] }
if ($values.ContainsKey('webportfallbacks') -and $values['webportfallbacks']) {
    $fallbacks = @($values['webportfallbacks'].Split(',') | ForEach-Object { $_.Trim() } |
        Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ })
}
if ($values.ContainsKey('bind') -and $values['bind']) { $bind = $values['bind'] }

$ports = @($port) + $fallbacks

# --------------------------------------------------------------- addresses

$lanRemote = @(
    'LocalSubnet',
    '127.0.0.1/32',
    '10.0.0.0/8',
    '172.16.0.0/12',
    '192.168.0.0/16',
    '169.254.0.0/16',
    '100.64.0.0/10'
)
$anyRemote = @('Any')
$remote = if ($AnySource) { $anyRemote } else { $lanRemote }

Add-Line ''
Add-Line '=== DPT.PSP GTA Modding Kit - firewall setup ==='
Add-Line ('config : ' + $(if (Test-Path $ini) { $ini } else { 'not found, using defaults' }))
Add-Line ('exe    : ' + $(if (Test-Path $exe) { $exe } else { 'NOT FOUND (rule will be port only)' }))
Add-Line ('port   : tcp/' + ($ports -join ', tcp/') + '   bind=' + $bind)
Add-Line ('source : ' + $(if ($AnySource) { 'any address' } else { 'LAN / private only' }))
Add-Line ''

if ($bind -eq '127.0.0.1') {
    Add-Line 'ВНИМАНИЕ: в config.ini bind=127.0.0.1 - сервер слушает только этот компьютер.'
    Add-Line '         Поменяйте на bind=0.0.0.0 и перезапустите, иначе правило не поможет.'
    Add-Line ''
}

$haveCmdlets = $null -ne (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue)

function Remove-Rule([string]$name) {
    if ($haveCmdlets) {
        Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue
    } else {
        netsh advfirewall firewall delete rule name="$name" | Out-Null
    }
}

function Add-Rule([string]$name, [string[]]$portList, [string[]]$remoteAddresses) {
    Remove-Rule $name
    if ($Remove) { return }

    if ($haveCmdlets) {
        # LocalPort takes String[]: a comma-joined string is rejected as "invalid port"
        $params = @{
            DisplayName   = $name
            Description   = 'DPT.PSP GTA Modding Kit - web UI'
            Direction     = 'Inbound'
            Action        = 'Allow'
            Protocol      = 'TCP'
            LocalPort     = $portList
            RemoteAddress = $remoteAddresses
            Profile       = 'Any'
            Enabled       = 'True'
        }
        if (Test-Path $exe) { $params['Program'] = $exe }
        New-NetFirewallRule @params | Out-Null
    } else {
        $cmd = 'netsh advfirewall firewall add rule name="' + $name + '" dir=in action=allow protocol=TCP localport=' +
               ($portList -join ',') + ' profile=any enable=yes'
        if (Test-Path $exe) { $cmd += ' program="' + $exe + '"' }
        if ($remoteAddresses -notcontains 'Any') { $cmd += ' remoteip=' + ($remoteAddresses -join ',') }
        Invoke-Expression $cmd | Out-Null
    }
}

# -------------------------------------------------------------------- apply

if ($Remove) {
    Add-Line 'Removing DPT.PSP ModKit firewall rules...'
} else {
    Add-Line 'Adding inbound TCP rule(s)...'
}

try {
    Add-Rule 'DPT.PSP ModKit' $ports $remote
    if ($Remove) {
        Add-Line '  removed : DPT.PSP ModKit'
    } else {
        Add-Line ('  ok      : DPT.PSP ModKit  tcp/' + ($ports -join ',') +
                  '  (' + $(if ($AnySource) { 'any source' } else { 'LAN/private only' }) + ')')
    }
} catch {
    Add-Line ('  FAILED  : DPT.PSP ModKit -> ' + $_.Exception.Message)
}

# ------------------------------------------------------------- diagnostics

Add-Line ''
Add-Line '--- listening sockets ---'
try {
    $netstat = netstat -ano | Select-String 'LISTENING'
    foreach ($p in $ports) {
        $hit = $netstat | Where-Object { $_.Line -match (':' + $p + '\s') } | Select-Object -First 1
        if ($hit) {
            $addr = ($hit.Line -split '\s+')[2]
            Add-Line ('  ' + $p + ' -> ' + $addr)
        } else {
            Add-Line ('  ' + $p + ' -> not listening (server stopped?)')
        }
    }
} catch {
    Add-Line ('  netstat failed: ' + $_.Exception.Message)
}

Add-Line ''
Add-Line '--- local IPv4 addresses (открывайте морду по этим адресам) ---'
try {
    $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' }
    foreach ($ip in $ips) {
        Add-Line ('  http://' + $ip.IPAddress + ':' + $port + '/   [' + $ip.InterfaceAlias + ']')
    }
} catch {
    try {
        $parsed = ipconfig | Select-String 'IPv4' | ForEach-Object {
            if ($_.Line -match '(\d+\.\d+\.\d+\.\d+)') { $Matches[1] }
        }
        foreach ($ip in $parsed) { Add-Line ('  http://' + $ip + ':' + $port + '/') }
    } catch {
        Add-Line ('  ' + $_.Exception.Message)
    }
}

Add-Line ''
Add-Line '--- local health check (обходит фаервол, проверяет сам сервер) ---'
try {
    $r = Invoke-WebRequest -UseBasicParsing -Uri ('http://127.0.0.1:' + $port + '/api/health') -TimeoutSec 5
    Add-Line ('  ' + $port + ' -> HTTP ' + [int]$r.StatusCode + ' ' + $r.Content)
} catch {
    Add-Line ('  ' + $port + ' -> FAILED: ' + $_.Exception.Message)
}

Add-Line ''
Add-Line '--- rules ---'
if ($haveCmdlets) {
    try {
        Get-NetFirewallRule -DisplayName 'DPT.PSP*' -ErrorAction SilentlyContinue |
            ForEach-Object {
                $pf = $_ | Get-NetFirewallPortFilter
                $af = $_ | Get-NetFirewallAddressFilter
                Add-Line ('  ' + $_.DisplayName + ' | ' + $_.Enabled + ' | ' + $_.Direction + '/' + $_.Action +
                          ' | tcp/' + $pf.LocalPort + ' | remote: ' + ($af.RemoteAddress -join ','))
            }
    } catch {
        Add-Line ('  ' + $_.Exception.Message)
    }
}

Add-Line ''
Add-Line 'Подсказки:'
Add-Line ('  * Морда: http://<LAN-IP>:' + $port + '/ - порт в config.ini, раздел [server].')
Add-Line '  * Аутентификации нет: наружу (Nginx Proxy Manager) - только вместе с ней.'
Add-Line '  * Правило привязано к bin\ModKit.exe; после переноса папки запустите скрипт снова.'
Add-Line ''
Add-Line ('Отчёт: ' + $reportPath)

Save-Report

if (-not $NoPause) {
    Write-Host ''
    Read-Host 'Готово. Enter - закрыть окно' | Out-Null
}