<#
    Format tests for DPT.PSP GTA Modding Kit.

    Uses the extracted game dumps from refs\GAMES\extracted when they are present;
    otherwise only the self-contained tests (ISO/CSO roundtrip, generated data) run.
    Everything goes to a temporary folder and is removed afterwards.

    Usage: powershell -File tools\test-formats.ps1 [-Games "refs\GAMES\extracted"]
#>
param(
    [string]$Games = "",
    [string]$Exe = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'bin\ModKit.exe' }
if (-not $Games) { $Games = Join-Path $root 'refs\GAMES\extracted' }

if (-not (Test-Path $Exe)) {
    Write-Host "ModKit.exe not found at $Exe - run build.cmd first" -ForegroundColor Red
    exit 1
}

$script:passed = 0
$script:failed = 0
$script:skipped = 0
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("modkit-test-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

function Check([string]$name, [scriptblock]$test) {
    try {
        if (& $test) {
            $script:passed++
            Write-Host ("  PASS  " + $name) -ForegroundColor Green
        } else {
            $script:failed++
            Write-Host ("  FAIL  " + $name) -ForegroundColor Red
        }
    } catch {
        $script:failed++
        Write-Host ("  FAIL  " + $name + " :: " + $_.Exception.Message) -ForegroundColor Red
    }
}

function Skip([string]$name, [string]$why) {
    $script:skipped++
    Write-Host ("  SKIP  " + $name + " (" + $why + ")") -ForegroundColor DarkYellow
}

function Run([string[]]$arguments) {
    $output = & $Exe @arguments 2>&1 | Out-String
    return $output
}

Write-Host ""
Write-Host "format tests (temp: $temp)" -ForegroundColor Cyan

# ------------------------------------------------ self contained: ISO + CSO
$small = Join-Path $temp 'small\PSP_GAME\SYSDIR'
New-Item -ItemType Directory -Path $small -Force | Out-Null
$random = New-Object System.Random 1234
$bytes = New-Object byte[] 40000
$random.NextBytes($bytes)
[System.IO.File]::WriteAllBytes((Join-Path $small 'EBOOT.BIN'), $bytes)
Set-Content -Path (Join-Path $temp 'small\PSP_GAME\PARAM.SFO') -Value 'PARAM' -NoNewline

Check "iso-build produces an image" {
    $out = Join-Path $temp 'small.iso'
    Run @('iso-build', (Join-Path $temp 'small'), $out, 'TEST') | Out-Null
    (Test-Path $out) -and ((Get-Item $out).Length -gt 2048)
}

Check "iso-list reads the image back" {
    $out = Run @('iso-list', (Join-Path $temp 'small.iso'))
    ($out -match 'EBOOT.BIN') -and ($out -match 'PARAM.SFO') -and ($out -match '2 files')
}

Check "iso-extract restores identical bytes" {
    $dir = Join-Path $temp 'small-out'
    Run @('iso-extract', (Join-Path $temp 'small.iso'), $dir) | Out-Null
    $a = [System.IO.File]::ReadAllBytes((Join-Path $small 'EBOOT.BIN'))
    $b = [System.IO.File]::ReadAllBytes((Join-Path $dir 'PSP_GAME\SYSDIR\EBOOT.BIN'))
    ($a.Length -eq $b.Length) -and (-not (Compare-Object $a $b))
}

Check "cso compress + expand is byte identical" {
    $cso = Join-Path $temp 'small.cso'
    $back = Join-Path $temp 'small-back.iso'
    Run @('cso', '--compress', (Join-Path $temp 'small.iso'), $cso) | Out-Null
    Run @('cso', $cso, $back) | Out-Null
    if (-not (Test-Path $cso)) { return $false }
    if (-not (Test-Path $back)) { return $false }
    $original = [System.IO.File]::ReadAllBytes((Join-Path $temp 'small.iso'))
    $restored = [System.IO.File]::ReadAllBytes($back)
    $same = ($original.Length -eq $restored.Length)
    if ($same) {
        for ($i = 0; $i -lt $original.Length; $i++) {
            if ($original[$i] -ne $restored[$i]) { $same = $false; break }
        }
    }
    $smaller = (Get-Item $cso).Length -lt (Get-Item (Join-Path $temp 'small.iso')).Length
    return ($same -and $smaller)
}

Check "identify recognises a plain iso" {
    (Run @('identify', (Join-Path $temp 'small.iso'))) -match 'iso'
}

Check "img roundtrip via .DIR keeps bytes" {
    $src = Join-Path $temp 'img-src'
    New-Item -ItemType Directory -Path $src -Force | Out-Null
    $payload = New-Object byte[] 5000
    $random.NextBytes($payload)
    [System.IO.File]::WriteAllBytes((Join-Path $src 'thing.mdl'), $payload)

    # build a DIR by hand: one entry at sector 0, size 3
    $dir = Join-Path $temp 'packed.dir'
    $entry = New-Object byte[] 32
    [BitConverter]::GetBytes([uint32]0).CopyTo($entry, 0)
    [BitConverter]::GetBytes([uint32]3).CopyTo($entry, 4)
    [System.Text.Encoding]::ASCII.GetBytes('THING.MDL').CopyTo($entry, 8)
    [System.IO.File]::WriteAllBytes($dir, $entry)

    $img = Join-Path $temp 'packed.img'
    [System.IO.File]::WriteAllBytes($img, (New-Object byte[] 6144))
    $list = Run @('img-list', $img, $dir)
    $list -match 'THING.MDL' -and $list -match '1 entries'
}

Check "gxt of a generated table is rejected cleanly" {
    $fake = Join-Path $temp 'fake.gxt'
    [System.IO.File]::WriteAllBytes($fake, [System.Text.Encoding]::ASCII.GetBytes('NOTGXT'))
    (Run @('identify', $fake)) -match 'unknown'
}

# ------------------------------------------------------- real game dumps
$lcsRoot = Join-Path $Games 'LCS\PSP_GAME\USRDIR'
$dtz = Join-Path $lcsRoot 'CHK\PSPHR\GAME.DTZ'
$img = Join-Path $lcsRoot 'MODELS\GTA3PSPHR.IMG'
$chk = Join-Path $lcsRoot 'TXD\LOADSC0.CHK'
$gxt = Join-Path $lcsRoot 'TEXT\ENGLISH.GXT'

if (Test-Path $dtz) {
    Write-Host "  (real LCS dump found)" -ForegroundColor DarkGray

    Check "dtz-inspect reports the streaming table" {
        $out = Run @('dtz-inspect', $dtz)
        $out -match 'LCS PSP' -and $out -match '4101 model infos' -and $out -match 'streamedModels: 536'
    }

    Check "dtz-dir writes a full directory" {
        $dir = Join-Path $temp 'lcs.dir'
        $out = Run @('dtz-dir', $dtz, $dir)
        (Test-Path $dir) -and $out -match '536 mdl' -and ((Get-Item $dir).Length -eq 1121 * 32)
    }

    Check "img-list understands the exported DIR" {
        $dir = Join-Path $temp 'lcs.dir'
        $out = Run @('img-list', $img, $dir)
        $out -match '1121 entries' -and $out -match 'PLAYER\.MDL' -and $out -match 'PLAYER\.CHK'
    }

    Check "img-extract writes entries to disk" {
        $outdir = Join-Path $temp 'img-out'
        Run @('img-extract', $img, $outdir, '--dir', (Join-Path $temp 'lcs.dir')) | Out-Null
        (Test-Path (Join-Path $outdir 'player_mdl')) -or (Test-Path (Join-Path $outdir 'PLAYER.MDL'))
    }

    if (Test-Path $chk) {
        Check "chk-list finds the loading screen texture" {
            $out = Run @('chk-list', $chk)
            $out -match '512x256' -and $out -match 'pal8' -and $out -match '1 textures'
        }

        Check "chk-export produces a non-trivial png" {
            $outdir = Join-Path $temp 'png'
            Run @('chk-export', $chk, $outdir) | Out-Null
            $file = Join-Path $outdir 'loadsc0.png'
            (Test-Path $file) -and ((Get-Item $file).Length -gt 20000)
        }
    } else { Skip "chk tests" "no LOADSC0.CHK" }

    if (Test-Path $gxt) {
        Check "gxt-dump extracts strings" {
            $txt = Join-Path $temp 'english.txt'
            $out = Run @('gxt-dump', $gxt, $txt)
            (Test-Path $txt) -and ((Get-Content $txt).Count -gt 1000)
        }
    } else { Skip "gxt test" "no ENGLISH.GXT" }
} else {
    Skip "LCS format tests" "refs\GAMES\extracted\LCS not found"
}

# ------------------------------------------------------------------ cleanup
Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue

Write-Host ""
if ($script:failed -eq 0) {
    Write-Host ("format tests: " + $script:passed + " passed" +
        $(if ($script:skipped -gt 0) { ", " + $script:skipped + " skipped" } else { "" })) -ForegroundColor Green
    exit 0
} else {
    Write-Host ("format tests: " + $script:passed + " passed, " + $script:failed + " failed") -ForegroundColor Red
    exit 1
}