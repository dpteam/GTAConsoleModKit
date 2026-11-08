#!/usr/bin/env python3
"""Fingerprint the unpacked game data, drop duplicated trees, clean CSO/ISO.

ISO hashes only tell you that two *images* differ; for modding what matters is
whether the **game data** is the same. Different firmware versions ship an
identical USRDIR and only a new EBOOT.BIN, so the tree digest groups them.

For every folder in refs/GAMES/extracted this computes:

  * `dataSha256`  - digest over PSP_GAME/USRDIR (the actual game data)
  * `treeSha256`  - digest over the whole PSP_GAME tree (includes EBOOT/PRX)

Folders with the same `dataSha256` are duplicates: one is kept, the rest are
deleted and recorded in the manifest.

Usage:
    python tools/prune_games.py [--dry-run] [--keep-all] [--delete-images] [--delete-archives]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAMES = ROOT / "refs" / "GAMES"
EXTRACTED = GAMES / "extracted"
MANIFEST_JSON = GAMES / "manifest.json"

sys.path.insert(0, str(ROOT / "tools"))
import import_games  # noqa: E402  (shares the manifest renderer)


def human(n: float) -> str:
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or unit == "TB":
            return ("%d %s" % (n, unit)) if unit == "B" else ("%.1f %s" % (n, unit))
        n /= 1024.0
    return "%.1f TB" % n


def tree_digest(folder: Path, only_usrdir: bool) -> tuple[str, int, int]:
    """SHA-256 over (relative path, size, content) of every file, sorted."""
    digest = hashlib.sha256()
    files = 0
    total = 0
    base = folder / "PSP_GAME" / "USRDIR" if only_usrdir else folder
    if not base.is_dir():
        return "", 0, 0
    for path in sorted(p for p in base.rglob("*") if p.is_file()):
        rel = path.relative_to(base).as_posix().upper()
        size = path.stat().st_size
        digest.update(rel.encode("utf-8"))
        digest.update(b"\0")
        digest.update(str(size).encode("ascii"))
        digest.update(b"\0")
        with open(path, "rb") as fh:
            while True:
                block = fh.read(8 << 20)
                if not block:
                    break
                digest.update(block)
        files += 1
        total += size
    return digest.hexdigest(), files, total


def folder_size(folder: Path) -> int:
    return sum(p.stat().st_size for p in folder.rglob("*") if p.is_file())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--keep-all", action="store_true", help="only fingerprint, delete nothing")
    parser.add_argument("--delete-images", action="store_true",
                        help="remove *.iso and *.cso from refs/GAMES afterwards")
    parser.add_argument("--delete-archives", action="store_true",
                        help="remove *.7z and *.zip after their data is extracted")
    parser.add_argument("--reuse", action="store_true",
                        help="keep existing fingerprints instead of hashing everything again")
    args = parser.parse_args()

    if not MANIFEST_JSON.is_file():
        print("no manifest yet - run tools/import_games.py first")
        return 1

    manifest = json.loads(MANIFEST_JSON.read_text(encoding="utf-8"))
    records = manifest.get("images", [])
    by_slug = {r["slug"]: r for r in records if r.get("slug")}

    # RUS trees were not created by the importer, give them records too
    for extra in ("LCS", "VCS"):
        folder = EXTRACTED / extra
        if folder.is_dir() and extra not in by_slug:
            record = {"archive": "(RUS CSO, was already in the project)", "slug": extra,
                      "status": "extracted"}
            records.append(record)
            by_slug[extra] = record

    folders = sorted(p for p in EXTRACTED.iterdir() if p.is_dir())
    print("folders: %d" % len(folders))

    seen: dict[str, str] = {}
    for folder in folders:
        record = by_slug.get(folder.name)
        if record is None:
            record = {"archive": "?", "slug": folder.name, "status": "unknown"}
            records.append(record)
            by_slug[folder.name] = record

        if record.get("status") in ("duplicate-data", "duplicate"):
            continue

        if args.reuse and record.get("dataSha256"):
            if record["dataSha256"] in seen:
                record["status"] = "duplicate-data"
                record["duplicateOf"] = seen[record["dataSha256"]]
            else:
                seen[record["dataSha256"]] = folder.name
            print("  %-46s %8s  (fingerprint reused)" % (folder.name, human(folder_size(folder))))
            continue

        print("  %-46s %8s" % (folder.name, human(folder_size(folder))), end="", flush=True)
        data_sha, files, total = tree_digest(folder, only_usrdir=True)
        tree_sha, all_files, all_total = tree_digest(folder, only_usrdir=False)
        record["dataSha256"] = data_sha
        record["treeSha256"] = tree_sha
        record["files"] = all_files
        record["bytes"] = all_total
        record["dataFiles"] = files
        record["dataBytes"] = total

        if data_sha and data_sha in seen:
            owner = seen[data_sha]
            record["status"] = "duplicate-data"
            record["duplicateOf"] = owner
            print("  = duplicate data of %s" % owner)
        else:
            if data_sha:
                seen[data_sha] = folder.name
            print("  data %s…" % data_sha[:12])

    # delete duplicate trees
    if not args.keep_all and not args.dry_run:
        for record in records:
            if record.get("status") != "duplicate-data":
                continue
            folder = EXTRACTED / record["slug"]
            if folder.is_dir():
                print("removing duplicate tree %s" % record["slug"])
                shutil.rmtree(folder, ignore_errors=True)

    # remove CSO/ISO images (the archives stay and can be re-expanded)
    if args.delete_images and not args.dry_run:
        for pattern in ("*.iso", "*.ISO", "*.cso", "*.CSO", "*.zso", "*.ZSO"):
            for image in GAMES.glob(pattern):
                print("removing image %s (%s)" % (image.name, human(image.stat().st_size)))
                image.unlink()

    if args.delete_archives and not args.dry_run:
        # safety: only delete an archive when its data is already on disk
        ready = {r["archive"] for r in records
                 if r.get("status") in ("extracted", "duplicate", "duplicate-data")}
        skipped = []
        for pattern in ("*.7z", "*.zip", "*.7z.001", "*.rar"):
            for archive in GAMES.glob(pattern):
                if archive.name not in ready:
                    skipped.append(archive.name)
                    continue
                record = next(r for r in records if r["archive"] == archive.name)
                if record.get("status") == "extracted":
                    folder = EXTRACTED / record["slug"]
                    if not (folder / "PSP_GAME" / "USRDIR").is_dir():
                        skipped.append(archive.name)
                        continue
                print("removing archive %s (%s)" % (archive.name, human(archive.stat().st_size)))
                archive.unlink()
        if skipped:
            print("NOT removed (no verified extraction): %s" % ", ".join(skipped))

    MANIFEST_JSON.write_text(json.dumps({"images": records}, indent=2, ensure_ascii=False),
                             encoding="utf-8")
    import_games.write_markdown(records)
    print("\nmanifest updated -> %s" % (GAMES / "MANIFEST.md").relative_to(ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main())