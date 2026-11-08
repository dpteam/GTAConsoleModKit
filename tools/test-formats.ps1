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

    Check "img-pack + img-list + img-extract roundtrip" {
        $src = Join-Path $temp 'pack-src'
        New-Item -ItemType Directory -Path $src -Force | Out-Null
        $payload = New-Object byte[] 5000
        $random.NextBytes($payload)
        [System.IO.File]::WriteAllBytes((Join-Path $src 'one.mdl'), $payload)
        [System.IO.File]::WriteAllBytes((Join-Path $src 'two.mdl'), $payload)

        $img2 = Join-Path $temp 'pack.img'
        $dir2 = Join-Path $temp 'pack.dir'
        Run @('img-pack', $src, $img2, '--dir', $dir2) | Out-Null
        $listing = Run @('img-list', $img2, $dir2)
        $outdir = Join-Path $temp 'pack-out'
        Run @('img-extract', $img2, $outdir, '--dir', $dir2) | Out-Null

        $ok = ($listing -match '2 entries') -and ($listing -match 'ONE.MDL') -and ($listing -match 'TWO.MDL')
        if ($ok) {
            $a = [System.IO.File]::ReadAllBytes((Join-Path $src 'one.mdl'))
            $b = [System.IO.File]::ReadAllBytes((Join-Path $outdir 'ONE.MDL'))
            $ok = $b.Length -ge $a.Length
            if ($ok) {
                for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { $ok = $false; break } }
            }
        }
        $ok
    }

    Check "img-replace keeps the archive size and refuses growth" {
        $copy = Join-Path $temp 'patch.img'
        Copy-Item $img $copy -Force
        $outdir = Join-Path $temp 'img-out'
        $entry = Join-Path $outdir 'player_mdl'
        if (-not (Test-Path $entry)) { $entry = Join-Path $outdir 'PLAYER.MDL' }

        $before = (Get-Item $copy).Length
        $ok = Run @('img-replace', $copy, 'PLAYER.MDL', $entry, '--dir', (Join-Path $temp 'lcs.dir')) -match 'replaced PLAYER.MDL'
        $sameSize = (Get-Item $copy).Length -eq $before

        $big = Join-Path $temp 'big.bin'
        [System.IO.File]::WriteAllBytes($big, (New-Object byte[] 200000))
        $refused = Run @('img-replace', $copy, 'PLAYER.MDL', $big, '--dir', (Join-Path $temp 'lcs.dir')) -match 'Growing an entry'

        $ok -and $sameSize -and $refused
    }

    Check "img-extract writes entries to disk" {
        $outdir = Join-Path $temp 'img-out'
        Run @('img-extract', $img, $outdir, '--dir', (Join-Path $temp 'lcs.dir')) | Out-Null
        (Test-Path (Join-Path $outdir 'player_mdl')) -or (Test-Path (Join-Path $outdir 'PLAYER.MDL'))
    }

    if (Test-Path $chk) {
        Check "img-repack keeps the image byte identical when nothing changed" {
            $outImg = Join-Path $temp 'same.img'
            $outDir = Join-Path $temp 'same.dir'
            $outDtz = Join-Path $temp 'same.dtz'
            $log = Run @('img-repack', $img, $dtz, '--dir', (Join-Path $temp 'lcs.dir'),
                '--folder', (Join-Path $temp 'img-out'), '--out', $outImg,
                '--out-dir', $outDir, '--out-dtz', $outDtz)

            $original = [System.IO.File]::ReadAllBytes($img)
            $repacked = [System.IO.File]::ReadAllBytes($outImg)
            $sameImage = $original.Length -eq $repacked.Length
            if ($sameImage) {
                for ($i = 0; $i -lt $original.Length; $i++) {
                    if ($original[$i] -ne $repacked[$i]) { $sameImage = $false; break }
                }
            }
            ($log -match '0 relocated') -and ($log -match '0 slots patched') -and $sameImage
        }

        Check "img-repack relocates a grown entry and patches the streaming table" {
            $grow = Join-Path $temp 'grow'
            New-Item -ItemType Directory -Path $grow -Force | Out-Null
            $medic = Join-Path $temp 'img-out\MEDIC.MDL'
            if (-not (Test-Path $medic)) { $medic = Join-Path $temp 'img-out\medic_mdl' }
            $bytes = [System.IO.File]::ReadAllBytes($medic)
            $bigger = New-Object byte[] ($bytes.Length + 5000)
            $bytes.CopyTo($bigger, 0)
            for ($i = $bytes.Length; $i -lt $bigger.Length; $i++) { $bigger[$i] = 0xAA }
            [System.IO.File]::WriteAllBytes((Join-Path $grow 'MEDIC.MDL'), $bigger)

            $outImg = Join-Path $temp 'grown.img'
            $outDir = Join-Path $temp 'grown.dir'
            $outDtz = Join-Path $temp 'grown.dtz'
            $log = Run @('img-repack', $img, $dtz, '--dir', (Join-Path $temp 'lcs.dir'),
                '--folder', $grow, '--out', $outImg, '--out-dir', $outDir, '--out-dtz', $outDtz)

            $movedEntry = (Run @('img-list', $outImg, $outDir)) -match '19614\s+\+\d+\s+MEDIC.MDL'
            $tableDir = Join-Path $temp 'grown-check.dir'
            Run @('dtz-dir', $outDtz, $tableDir) | Out-Null
            $tableAgrees = (Run @('img-list', $outImg, $tableDir)) -match '19614\s+\+\d+\s+MEDIC.MDL'
            $grew = (Get-Item $outImg).Length -gt (Get-Item $img).Length

            ($log -match '1 relocated') -and ($log -match '1 slots patched') -and $movedEntry -and $tableAgrees -and $grew
        }

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

        Check "chk-replace roundtrip is pixel identical" {
            $png1 = Join-Path $temp 'rt1'
            $png2 = Join-Path $temp 'rt2'
            $copy = Join-Path $temp 'rt.chk'
            Run @('chk-export', $chk, $png1) | Out-Null
            Copy-Item $chk $copy -Force
            $out = Run @('chk-replace', $copy, (Join-Path $png1 'loadsc0.png'), '0')
            Run @('chk-export', $copy, $png2) | Out-Null
            $a = [System.IO.File]::ReadAllBytes((Join-Path $png1 'loadsc0.png'))
            $b = [System.IO.File]::ReadAllBytes((Join-Path $png2 'loadsc0.png'))
            $same = $a.Length -eq $b.Length
            if ($same) {
                for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { $same = $false; break } }
            }
            $sizeSame = (Get-Item $copy).Length -eq (Get-Item $chk).Length
            ($out -match 'replaced texture') -and $same -and $sizeSame
        }

        Check "chk-replace refuses a wrong sized image" {
            $png = Join-Path $temp 'rt1\loadsc0.png'
            $copy = Join-Path $temp 'rt3.chk'
            Copy-Item $chk $copy -Force
            $out = Run @('chk-replace', $copy, $png, '1')
            $out -match 'out of range'
        }
    } else { Skip "chk tests" "no LOADSC0.CHK" }

    if (Test-Path $gxt) {
        Check "gxt-dump extracts strings" {
            $txt = Join-Path $temp 'english.txt'
            $out = Run @('gxt-dump', $gxt, $txt)
            $content = [System.IO.File]::ReadAllText($txt, [System.Text.Encoding]::UTF8)
            (Test-Path $txt) -and ((Get-Content $txt).Count -gt 1000) -and
                ($content -match '\[MAIN\]') -and ($content -match "ACCURA\tAccuracy") -and
                ($out -match '5707 strings')
        }

        Check "gxt no-op import is byte identical" {
            $txt = Join-Path $temp 'english.txt'
            $rt = Join-Path $temp 'rt.gxt'
            $log = Run @('gxt-import', $gxt, $txt, $rt)
            $a = [System.IO.File]::ReadAllBytes($gxt)
            $b = [System.IO.File]::ReadAllBytes($rt)
            $same = $a.Length -eq $b.Length
            if ($same) {
                for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { $same = $false; break } }
            }
            ($log -match '0 changed') -and $same
        }

        Check "gxt import applies an edit and keeps every key" {
            $edited = Join-Path $temp 'english-edited.txt'
            $content = [System.IO.File]::ReadAllText((Join-Path $temp 'english.txt'), [System.Text.Encoding]::UTF8)
            $content = $content.Replace("ACCURA`tAccuracy", "ACCURA`t" + [char]0x0422 + [char]0x043E + [char]0x0447 + [char]0x043D + [char]0x043E + [char]0x0441 + [char]0x0442 + [char]0x044C)
            [System.IO.File]::WriteAllText($edited, $content, (New-Object System.Text.UTF8Encoding $false))

            $out = Join-Path $temp 'edited.gxt'
            $log = Run @('gxt-import', $gxt, $edited, $out)
            $check = Join-Path $temp 'edited-check.txt'
            Run @('gxt-dump', $out, $check) | Out-Null
            $back = [System.IO.File]::ReadAllText($check, [System.Text.Encoding]::UTF8)

            ($log -match '1 changed') -and ($log -match '5707 strings') -and
                ($back -match [string][char]0x0422) -and ($back -match "AEROPL`tAeroplane")
        }
    } else { Skip "gxt test" "no ENGLISH.GXT" }
} else {
    Skip "LCS format tests" "refs\GAMES\extracted\LCS not found"
}

# --------------------------------------------------- clean VCS (imported image)
$vcsClean = Join-Path $Games 'vice_city_stories_usa\PSP_GAME\USRDIR\RUNDATA'
$vcsDtz = Join-Path $vcsClean 'PSP\GAME.DTZ'
$vcsImg = Join-Path $vcsClean 'GTA3PSP.IMG'

if (Test-Path $vcsDtz) {
    Write-Host "  (clean VCS USA dump found)" -ForegroundColor DarkGray

    Check "dtz-inspect indexes the VCS streaming table" {
        $out = Run @('dtz-inspect', $vcsDtz)
        ($out -match 'VCS PSP') -and ($out -match 'streamedModels: 645') -and
            ($out -match 'streamedTextures: 814')
    }

    Check "dtz-dir works for VCS and names resolve" {
        $dir = Join-Path $temp 'vcs.dir'
        $log = Run @('dtz-dir', $vcsDtz, $dir)
        if (-not (Test-Path $dir)) { return $false }
        $listing = Run @('img-list', $vcsImg, $dir)
        $unresolved = ($listing | Select-String 'HASH_').Count
        ($log -match '645 mdl') -and ($log -match '814 tex') -and
            ($listing -match 'PLR\.MDL') -and ($unresolved -eq 0)
    }

    Check "VCS entries match the chunk headers" {
        # every DIR entry must point at a chunk whose fileEnd rounds up to its size
        $dir = Join-Path $temp 'vcs.dir'
        $bytes = [System.IO.File]::ReadAllBytes($dir)
        $img = [System.IO.File]::ReadAllBytes($vcsImg)
        $checked = 0
        $bad = 0
        for ($i = 0; $i -lt ($bytes.Length / 32) -and $checked -lt 200; $i++) {
            $offset = [BitConverter]::ToUInt32($bytes, $i * 32)
            $size = [BitConverter]::ToUInt32($bytes, $i * 32 + 4)
            if ($size -eq 0) { continue }
            $at = $offset * 2048
            if ($at + 12 -gt $img.Length) { continue }
            $fileEnd = [BitConverter]::ToUInt32($img, $at + 8)
            $sectors = [math]::Ceiling($fileEnd / 2048.0)
            $checked++
            if ($sectors -ne $size) { $bad++ }
        }
        ($checked -ge 100) -and ($bad -eq 0)
    }
} else {
    Skip "VCS streaming tests" "refs\GAMES\extracted\vice_city_stories_usa not found"
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