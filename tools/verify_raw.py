"""Verifies the .RAW SFX banks: structure, and the decoder against an independent one.

Two independent checks, the same spirit as the texture gallery verification:

  1. structure - every bank must be a chain of 48-byte records whose sizes add up
     to exactly the file size (there is no index, so this is the only proof the
     boundaries are right);
  2. audio - every sample of every bank is decoded twice: by the toolkit
     (`raw-wav` -> WAV) and by the PS-ADPCM decoder below, written from the format
     description and sharing no code with the toolkit. The PCM must match exactly.

Usage: python tools/verify_raw.py [--limit N] [--stats]
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import shutil
import struct
import subprocess
import sys
import wave

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'bin', 'ModKit.exe')
AUDIO = os.path.join(ROOT, 'refs', 'GAMES', 'extracted', 'LCS', 'PSP_GAME', 'USRDIR', 'AUDIO')
WORK = os.path.join(os.environ.get('TEMP', '.'), 'verify_raw')

COEFS = ((0, 0), (60, 0), (115, -52), (98, -55), (122, -60))


def trunc_div(value, divisor):
    """C# integer division truncates toward zero; Python // floors."""
    if value >= 0:
        return value // divisor
    return -((-value) // divisor)


def decode_psadpcm(payload):
    """Independent PS-ADPCM decoder (16-byte frames, 28 samples each)."""
    samples = []
    hist1 = hist2 = 0
    frames = len(payload) // 16
    for frame in range(frames):
        base = frame * 16
        header = payload[base]
        shift = header & 0x0F
        filt = (header >> 4) & 0x0F
        if filt > 4:
            filt = 0
        c1, c2 = COEFS[filt]
        for i in range(14):
            byte = payload[base + 2 + i]
            for nibble in (byte & 0x0F, byte >> 4):
                if nibble >= 8:
                    nibble -= 16
                value = (nibble << 12) >> shift
                value += trunc_div(hist1 * c1 + hist2 * c2, 64)
                if value > 32767:
                    value = 32767
                if value < -32768:
                    value = -32768
                hist2 = hist1
                hist1 = value
                samples.append(value)
    return samples


def read_bank(data):
    entries = []
    offset = 0
    problem = None
    while offset + 48 <= len(data):
        magic = data[offset:offset + 4]
        blank = magic == b'\x00\x00\x00\x00'
        version, _reserved = struct.unpack_from('>II', data, offset + 4)
        size, rate = struct.unpack_from('>II', data, offset + 12)
        if magic not in (b'VAGp', b'\x00\x00\x00\x00'):
            problem = 'bad magic at %d' % offset
            break
        if size == 0 or size % 16 or offset + 48 + size > len(data):
            problem = 'bad size %d at %d' % (size, offset)
            break
        name = data[offset + 0x20:offset + 0x30].split(b'\x00')[0].decode('ascii', 'replace')
        entries.append({
            'index': len(entries), 'offset': offset, 'size': size,
            'rate': rate, 'version': version, 'name': name, 'magic': not blank,
        })
        offset += 48 + size
    return entries, problem, offset


def wav_samples(path):
    with wave.open(path, 'rb') as handle:
        if handle.getnchannels() != 1 or handle.getsampwidth() != 2:
            raise ValueError('unexpected WAV layout')
        frames = handle.getnframes()
        rate = handle.getframerate()
        data = handle.readframes(frames)
    return list(struct.unpack('<%dh' % frames, data)), rate


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--limit', type=int, default=0)
    parser.add_argument('--stats', action='store_true')
    args = parser.parse_args()

    banks = sorted(glob.glob(os.path.join(AUDIO, '**', '*.RAW'), recursive=True))
    if not banks:
        print('no .RAW banks at', AUDIO)
        return 1
    if args.limit:
        banks = banks[:args.limit]

    if os.path.isdir(WORK):
        shutil.rmtree(WORK)
    os.makedirs(WORK)

    structural = []
    entries_total = 0
    samples_total = 0
    mismatches = []
    silent = 0
    quiet = 0
    clipped = 0
    rates = {}
    versions = {}

    for number, bank in enumerate(banks):
        with open(bank, 'rb') as fh:
            data = fh.read()
        entries, problem, consumed = read_bank(data)
        entries_total += len(entries)
        if problem or consumed != len(data):
            structural.append((os.path.basename(bank), problem or
                               'ends at %d of %d' % (consumed, len(data))))
            continue

        for entry in entries:
            versions[entry['version']] = versions.get(entry['version'], 0) + 1
            rates[entry['rate']] = rates.get(entry['rate'], 0) + 1

        out = os.path.join(WORK, os.path.splitext(os.path.basename(bank))[0])
        result = subprocess.run([EXE, 'raw-wav', bank, out], capture_output=True, text=True)
        if result.returncode != 0:
            structural.append((os.path.basename(bank), 'raw-wav failed: ' + result.stderr[-200:]))
            continue

        files = {}
        for path in glob.glob(os.path.join(out, '*.wav')):
            index = int(os.path.basename(path).split('_')[0])
            files[index] = path

        for entry in entries:
            path = files.get(entry['index'])
            if not path:
                mismatches.append((os.path.basename(bank), entry['index'], 'no WAV written'))
                continue
            payload = data[entry['offset'] + 48:entry['offset'] + 48 + entry['size']]
            expected = decode_psadpcm(payload)
            actual, rate = wav_samples(path)
            samples_total += len(expected)

            if rate != entry['rate']:
                mismatches.append((os.path.basename(bank), entry['index'],
                                   'rate %d != %d' % (rate, entry['rate'])))
                continue
            if len(expected) != len(actual):
                mismatches.append((os.path.basename(bank), entry['index'],
                                   'length %d != %d' % (len(actual), len(expected))))
                continue
            if expected != actual:
                first = next(i for i in range(len(expected)) if expected[i] != actual[i])
                mismatches.append((os.path.basename(bank), entry['index'],
                                   'first difference at sample %d: %d != %d' % (
                                       first, actual[first], expected[first])))
                continue

            if args.stats:
                peak = max(abs(s) for s in expected) if expected else 0
                if peak == 0:
                    silent += 1
                elif peak < 100:
                    quiet += 1
                if peak >= 32767:
                    clipped += 1

        print('  %2d/%d %-16s %3d samples%s' % (
            number + 1, len(banks), os.path.basename(bank), len(entries),
            '' if not problem else '  <-- ' + problem))

    print()
    print('banks                 : %d' % len(banks))
    print('entries               : %d' % entries_total)
    print('samples compared      : %d' % samples_total)
    print('structural problems   : %d' % len(structural))
    for row in structural[:8]:
        print('   ', row)
    print('decoder mismatches    : %d' % len(mismatches))
    for row in mismatches[:8]:
        print('   ', row)
    if args.stats:
        print('silent (peak == 0)    : %d' % silent)
        print('very quiet (< 100)    : %d' % quiet)
        print('clipped (peak 32767)  : %d' % clipped)
    print('versions              : %s' % dict(sorted(versions.items())))
    print('sample rates          : %s' % dict(sorted(rates.items(), key=lambda kv: -kv[1])[:8]))

    shutil.rmtree(WORK, ignore_errors=True)
    ok = not structural and not mismatches
    print()
    print('RESULT:', 'structure and audio match' if ok else 'PROBLEMS')
    return 0 if ok else 2


if __name__ == '__main__':
    sys.exit(main())