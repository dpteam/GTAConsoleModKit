"""Verifies the texture gallery end to end, texture by texture.

Ground truth: `chk-export` on every CHK extracted from the same IMG (the path the
tests already prove pixel-identical). The gallery is then asked for the same
textures through the HTTP API - exactly what the browser gets - and every PNG is
compared byte for byte.

Usage: python tools/verify_gallery.py [--port 44700] [--project <id>] [--keep]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import urllib.parse
import urllib.request
from collections import Counter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'bin', 'ModKit.exe')
LCS = os.path.join(ROOT, 'refs', 'GAMES', 'extracted', 'LCS', 'PSP_GAME')
IMG = os.path.join(LCS, 'USRDIR', 'MODELS', 'GTA3PSPHR.IMG')
DTZ = os.path.join(LCS, 'USRDIR', 'CHK', 'PSPHR', 'GAME.DTZ')
WORK = os.path.join(os.environ.get('TEMP', '.'), 'verify_gallery')


def run(*args, quiet=True):
    result = subprocess.run([EXE] + [str(a) for a in args],
                            capture_output=True, text=True)
    if result.returncode != 0:
        print('FAILED:', ' '.join(str(a) for a in args))
        print(result.stdout[-1500:], result.stderr[-1500:])
        sys.exit(1)
    return result.stdout if not quiet else ''


def sha(path):
    with open(path, 'rb') as fh:
        return hashlib.sha256(fh.read()).hexdigest()


def get(base, path):
    with urllib.request.urlopen(base + path) as response:
        return response.read()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, default=44700)
    parser.add_argument('--project', default='')
    parser.add_argument('--keep', action='store_true')
    args = parser.parse_args()
    base = 'http://127.0.0.1:%d' % args.port

    if not os.path.exists(IMG):
        print('no LCS image at', IMG)
        return 1

    project = args.project
    if not project:
        body = json.dumps({'name': 'gallery verify', 'game': 'LCS', 'owner': 'verify'}).encode()
        request = urllib.request.Request(base + '/api/projects', data=body,
                                         headers={'Content-Type': 'application/json'})
        with urllib.request.urlopen(request) as response:
            project = json.loads(response.read())['id']
        print('project:', project)

    # ---------------------------------------------------------------- fixtures
    if os.path.isdir(WORK):
        shutil.rmtree(WORK)
    os.makedirs(WORK)

    print('extracting the .DIR and the CHK files...')
    directory = os.path.join(WORK, 'lcs.dir')
    run('dtz-dir', DTZ, directory)
    extracted = os.path.join(WORK, 'chk')
    run('img-extract', IMG, extracted, '--dir', directory)
    chunk_files = sorted(f for f in os.listdir(extracted) if f.upper().endswith('.CHK'))
    print('  %d CHK files' % len(chunk_files))

    # put the image and its .DIR into the project: the gallery then sees the same
    # chunk names as the extraction
    print('uploading the image into the project...')
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

    # ------------------------------------------------------------ ground truth
    print('exporting every texture from every chunk (ground truth)...')
    truth = {}          # chunk name -> Counter(bytes hash)
    truth_files = 0
    for index, name in enumerate(chunk_files):
        target = os.path.join(WORK, 'png', name)
        run('chk-export', os.path.join(extracted, name), target)
        hashes = Counter()
        if os.path.isdir(target):
            for png in os.listdir(target):
                hashes[sha(os.path.join(target, png))] += 1
                truth_files += 1
        truth[name] = hashes
        if (index + 1) % 100 == 0:
            print('  %d/%d chunks' % (index + 1, len(chunk_files)))

    # ---------------------------------------------------------------- gallery
    print('asking the gallery...')
    index_doc = json.loads(get(base, '/api/projects/%s/img-textures?path=%s' % (
        project, urllib.parse.quote('MODELS/GTA3PSPHR.IMG'))))
    chunks = index_doc.get('chunks') or []
    print('  gallery reports %d textures in %d chunks (format: %s)' % (
        index_doc.get('textures', -1), len(chunks), index_doc.get('format')))

    seen = Counter()
    mismatched_chunks = []
    mismatched_textures = []
    checked = 0
    for chunk in chunks:
        entry = chunk['entry']
        hashes = Counter()
        for texture in chunk['textures']:
            url = '/api/projects/%s/img-texture?path=%s&entry=%s&index=%d' % (
                project, urllib.parse.quote('MODELS/GTA3PSPHR.IMG'),
                urllib.parse.quote(entry), texture['index'])
            body = get(base, url)
            hashes[hashlib.sha256(body).hexdigest()] += 1
            checked += 1
        seen[entry] = hashes
        if entry not in truth:
            mismatched_chunks.append('%s: not a CHK in the extraction' % entry)
        elif truth[entry] != hashes:
            missing = truth[entry] - hashes
            extra = hashes - truth[entry]
            mismatched_chunks.append('%s: %d missing, %d different' % (
                entry, sum(missing.values()), sum(extra.values())))
            for digest in list(extra)[:3]:
                mismatched_textures.append((entry, digest[:12]))

    # --------------------------------------------------------------- summary
    print()
    print('chunks with textures : %d (extraction: %d)' % (
        len(chunks), sum(1 for name, counter in truth.items() if counter)))
    print('textures compared    : %d (extraction: %d)' % (checked, truth_files))
    print('chunks that differ   : %d' % len(mismatched_chunks))
    for line in mismatched_chunks[:10]:
        print('   ', line)

    empty = [name for name, counter in truth.items() if not counter]
    print('chunks without textures (not listed by design): %s' % ', '.join(sorted(empty)))

    ok = (checked == truth_files and not mismatched_chunks and len(chunks) ==
          sum(1 for counter in truth.values() if counter))
    print()
    print('RESULT:', 'every texture matches byte for byte' if ok else 'MISMATCH')

    if not args.keep:
        for path in ('MODELS/GTA3PSPHR.IMG', 'MODELS/GTA3PSPHR.DIR'):
            body = json.dumps({'path': path}).encode()
            request = urllib.request.Request(base + '/api/projects/%s/delete' % project,
                                             data=body, headers={'Content-Type': 'application/json'})
            try:
                urllib.request.urlopen(request).read()
            except Exception:
                pass
        if not args.project:
            request = urllib.request.Request(base + '/api/projects/' + project, method='DELETE')
            try:
                urllib.request.urlopen(request).read()
            except Exception:
                pass
        shutil.rmtree(WORK, ignore_errors=True)
    return 0 if ok else 2


if __name__ == '__main__':
    sys.exit(main())