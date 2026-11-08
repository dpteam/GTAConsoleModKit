"""Second opinion on the texture gallery: the thumbnail path and "does it look sane".

`verify_gallery.py` proves the full-size PNGs are byte-identical to `chk-export`.
This one checks what byte equality cannot:

  * thumbnails (`w=64`) have the size implied by the full texture (no squashed or
    stretched thumbs);
  * no texture decodes to a degenerate image (one colour), which is what a wrong
    palette offset or a mis-detected format looks like;
  * the reported width/height match the PNG that comes out.

PNGs are collected in batches and analysed by a single PowerShell process per
batch - one process per texture would take an hour.

Usage: python tools/verify_gallery_stats.py [--port 44700] [--sample 0]
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'bin', 'ModKit.exe')
LCS = os.path.join(ROOT, 'refs', 'GAMES', 'extracted', 'LCS', 'PSP_GAME')
IMG = os.path.join(LCS, 'USRDIR', 'MODELS', 'GTA3PSPHR.IMG')
DTZ = os.path.join(LCS, 'USRDIR', 'CHK', 'PSPHR', 'GAME.DTZ')
WORK = os.path.join(os.environ.get('TEMP', '.'), 'verify_gallery_stats')
BATCH = 300

# Reads every *.png in a folder and prints "name|width|height|distinct colours".
ANALYSE = r'''
Add-Type -AssemblyName System.Drawing
$folder = $args[0]
foreach ($file in Get-ChildItem -Path $folder -Filter *.png) {
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $stream = New-Object System.IO.MemoryStream(, $bytes)
    $bmp = [System.Drawing.Bitmap]::FromStream($stream)
    $colors = New-Object 'System.Collections.Generic.HashSet[int]'
    $stepX = [Math]::Max(1, [int]($bmp.Width / 32))
    $stepY = [Math]::Max(1, [int]($bmp.Height / 32))
    for ($y = 0; $y -lt $bmp.Height; $y += $stepY) {
        for ($x = 0; $x -lt $bmp.Width; $x += $stepX) {
            [void]$colors.Add($bmp.GetPixel($x, $y).ToArgb())
        }
    }
    Write-Output ($file.Name + '|' + $bmp.Width + '|' + $bmp.Height + '|' + $colors.Count)
    $bmp.Dispose()
    $stream.Dispose()
}
'''


def run(*args):
    result = subprocess.run([EXE] + [str(a) for a in args], capture_output=True, text=True)
    if result.returncode != 0:
        print('FAILED:', ' '.join(str(a) for a in args))
        print(result.stdout[-1200:], result.stderr[-1200:])
        sys.exit(1)
    return result.stdout


def get(base, path):
    with urllib.request.urlopen(base + path) as response:
        return response.read()


def analyse_folder(folder, script_path):
    result = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                             '-File', script_path, folder], capture_output=True, text=True)
    sizes = {}
    for line in result.stdout.splitlines():
        parts = line.strip().split('|')
        if len(parts) != 4:
            continue
        try:
            sizes[parts[0]] = (int(parts[1]), int(parts[2]), int(parts[3]))
        except ValueError:
            continue
    return sizes


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, default=44700)
    parser.add_argument('--sample', type=int, default=0,
                        help='limit to N textures (0 = every texture of every chunk)')
    args = parser.parse_args()
    base = 'http://127.0.0.1:%d' % args.port

    if not os.path.exists(IMG):
        print('no LCS image at', IMG)
        return 1

    if os.path.isdir(WORK):
        shutil.rmtree(WORK)
    os.makedirs(WORK)
    script_path = os.path.join(WORK, 'analyse.ps1')
    with open(script_path, 'w', encoding='utf-8') as fh:
        fh.write(ANALYSE)

    body = json.dumps({'name': 'gallery stats', 'game': 'LCS', 'owner': 'verify'}).encode()
    request = urllib.request.Request(base + '/api/projects', data=body,
                                     headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(request) as response:
        project = json.loads(response.read())['id']

    directory = os.path.join(WORK, 'lcs.dir')
    run('dtz-dir', DTZ, directory)
    with open(IMG, 'rb') as fh:
        data = fh.read()
    request = urllib.request.Request(
        base + '/api/projects/%s/upload?path=MODELS/GTA3PSPHR.IMG' % project,
        data=data, headers={'Content-Type': 'application/octet-stream'})
    urllib.request.urlopen(request).read()
    with open(directory, 'rb') as fh:
        data = fh.read()
    request = urllib.request.Request(
        base + '/api/projects/%s/upload?path=MODELS/GTA3PSPHR.DIR' % project,
        data=data, headers={'Content-Type': 'application/octet-stream'})
    urllib.request.urlopen(request).read()

    index_doc = json.loads(get(base, '/api/projects/%s/img-textures?path=%s' % (
        project, urllib.parse.quote('MODELS/GTA3PSPHR.IMG'))))

    items = []
    for chunk in index_doc['chunks']:
        for texture in chunk['textures']:
            items.append((chunk['entry'], texture))

    formats = {}
    for _, texture in items:
        formats[texture['format']] = formats.get(texture['format'], 0) + 1
    print('textures: %d, formats: %s' % (len(items), formats))

    if args.sample:
        step = max(1, len(items) // args.sample)
        items = items[::step][:args.sample]
        print('sampling %d textures' % len(items))

    flat = []
    wrong_size = []
    bad_thumb = []
    checked = 0
    folder = os.path.join(WORK, 'batch')

    for start in range(0, len(items), BATCH):
        batch = items[start:start + BATCH]
        if os.path.isdir(folder):
            shutil.rmtree(folder)
        os.makedirs(folder)

        meta = {}
        for number, (entry, texture) in enumerate(batch):
            quoted = urllib.parse.quote(entry)
            prefix = '%04d' % number
            base_url = '/api/projects/%s/img-texture?path=%s&entry=%s&index=%d' % (
                project, urllib.parse.quote('MODELS/GTA3PSPHR.IMG'), quoted, texture['index'])
            with open(os.path.join(folder, prefix + '_full.png'), 'wb') as fh:
                fh.write(get(base, base_url))
            with open(os.path.join(folder, prefix + '_thumb.png'), 'wb') as fh:
                fh.write(get(base, base_url + '&w=64'))
            meta[prefix] = (entry, texture)

        sizes = analyse_folder(folder, script_path)

        for prefix, (entry, texture) in meta.items():
            full = sizes.get(prefix + '_full.png')
            thumb = sizes.get(prefix + '_thumb.png')
            checked += 1
            if not full:
                wrong_size.append((entry, texture['index'], 'unreadable'))
                continue
            if full[0] != texture['width'] or full[1] != texture['height']:
                wrong_size.append((entry, texture['index'], full[:2],
                                   (texture['width'], texture['height'])))
            if full[2] <= 1:
                flat.append((entry, texture['name'], full[2]))
            if thumb:
                expected = max(1, int(round(full[1] * min(64, full[0]) / float(full[0]))))
                if thumb[0] != min(64, full[0]) or abs(thumb[1] - expected) > 1:
                    bad_thumb.append((entry, texture['index'], thumb[:2], (min(64, full[0]), expected)))
            else:
                bad_thumb.append((entry, texture['index'], 'unreadable thumb'))

        print('  checked %d/%d' % (checked, len(items)))

    print()
    print('checked          : %d' % checked)
    print('wrong dimensions : %d' % len(wrong_size))
    for row in wrong_size[:6]:
        print('   ', row)
    print('single-colour    : %d' % len(flat))
    for row in flat[:10]:
        print('   ', row)
    print('bad thumbnail    : %d' % len(bad_thumb))
    for row in bad_thumb[:6]:
        print('   ', row)

    body = json.dumps({'path': 'MODELS/GTA3PSPHR.IMG'}).encode()
    request = urllib.request.Request(base + '/api/projects/%s/delete' % project,
                                     data=body, headers={'Content-Type': 'application/json'})
    try:
        urllib.request.urlopen(request).read()
    except Exception:
        pass
    request = urllib.request.Request(base + '/api/projects/' + project, method='DELETE')
    try:
        urllib.request.urlopen(request).read()
    except Exception:
        pass
    shutil.rmtree(WORK, ignore_errors=True)

    ok = not wrong_size and not bad_thumb
    print()
    print('RESULT:', 'dimensions and thumbnails consistent' if ok else 'PROBLEMS')
    return 0 if ok else 2


if __name__ == '__main__':
    sys.exit(main())