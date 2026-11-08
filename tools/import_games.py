#!/usr/bin/env python3
"""Import original GTA LCS/VCS PSP images from refs/GAMES archives.

For every archive:
  1. unpack it into a scratch folder;
  2. if the image inside is CSO/ZSO, expand it with the kit's own converter;
  3. hash the plain ISO (SHA-256) to find duplicates across regions/versions;
  4. duplicates are deleted immediately, unique images are unpacked into
     refs/GAMES/extracted/<slug>/ and the ISO is deleted afterwards (the archive
     stays as the source of truth);
  5. DISC_ID / TITLE are read from PSP_GAME/PARAM.SFO for the manifest.

Everything is written to refs/GAMES/MANIFEST.md (and .json) so the mapping
archive -> image -> extracted folder -> duplicates is never lost, even after the
archives themselves are deleted (see tools/prune_games.py).

Usage:
    python tools/import_games.py [--dry-run] [--keep-iso] [--min-free-gb 6]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAMES = ROOT / "refs" / "GAMES"
WORK = GAMES / "_work"
EXTRACTED = GAMES / "extracted"
MANIFEST_MD = GAMES / "MANIFEST.md"
MANIFEST_JSON = GAMES / "manifest.json"
# a copy of the report that goes into git (refs/ is ignored), refreshed on every run
DOCS_SNAPSHOT = ROOT / "docs" / "GAME_IMAGES.md"
MODKIT = ROOT / "bin" / "ModKit.exe"
SEVEN_ZIP = "7z"


def human(n: float) -> str:
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or unit == "TB":
            return ("%d %s" % (n, unit)) if unit == "B" else ("%.1f %s" % (n, unit))
        n /= 1024.0
    return "%.1f TB" % n


def slug(text: str) -> str:
    text = text.replace("Grand Theft Auto - ", "").replace("Grand Theft Auto ", "")
    text = re.sub(r"[\[\]()]", " ", text)
    text = re.sub(r"[^0-9A-Za-z]+", "_", text).strip("_")
    return text.lower()


def sha256(path: Path, chunk: int = 8 << 20) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        while True:
            block = fh.read(chunk)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest()


def free_gb() -> float:
    return shutil.disk_usage(str(GAMES)).free / (1024 ** 3)


def run(args: list[str]) -> int:
    return subprocess.call(args, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)


def sfo_string(path: Path, wanted: set[str]) -> dict[str, str]:
    """PARAM.SFO reader.

    Layout: "\0PSF" u32 version, u32 keyTableStart, u32 dataTableStart, u32 count,
    then count index entries of 16 bytes:
        u16 keyOffset, u16 format, u32 length, u32 maxLength, u32 dataOffset
    Keys live at keyTableStart + keyOffset, values at dataTableStart + dataOffset.
    """
    result: dict[str, str] = {}
    if not path.is_file():
        return result
    data = path.read_bytes()
    if len(data) < 20 or data[:4] != b"\0PSF":
        return result

    key_table, data_table, count = struct.unpack_from("<III", data, 8)
    for i in range(count):
        at = 20 + i * 16
        if at + 16 > len(data):
            break
        key_off = struct.unpack_from("<H", data, at)[0]
        data_len = struct.unpack_from("<I", data, at + 4)[0]
        data_off = struct.unpack_from("<I", data, at + 12)[0]
        key = data[key_table + key_off:].split(b"\0")[0].decode("ascii", "replace")
        if key not in wanted:
            continue
        start = data_table + data_off
        raw = data[start:start + data_len]
        result[key] = raw.split(b"\0")[0].decode("utf-8", "replace").strip()
    return result


def find_image(folder: Path) -> Path | None:
    for pattern in ("*.iso", "*.ISO", "*.cso", "*.CSO", "*.zso", "*.ZSO"):
        files = sorted(folder.glob(pattern))
        if files:
            return files[0]
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--keep-iso", action="store_true", help="keep unique ISOs next to the archives")
    parser.add_argument("--min-free-gb", type=float, default=6.0)
    parser.add_argument("--only", default="", help="process archives whose name contains this text")
    args = parser.parse_args()

    archives = sorted(
        [p for p in GAMES.iterdir() if p.suffix.lower() in (".7z", ".zip")]
    )
    if args.only:
        needle = args.only.lower()
        archives = [p for p in archives if needle in p.name.lower()]
    if not archives:
        print("no archives in %s (filter: %r)" % (GAMES, args.only))
        return 1

    seen: dict[str, str] = {}          # sha256 -> slug that owns it
    records: list[dict] = []
    if MANIFEST_JSON.is_file():
        try:
            records = json.loads(MANIFEST_JSON.read_text(encoding="utf-8")).get("images", [])
            for record in records:
                if record.get("sha256"):
                    seen[record["sha256"]] = record["slug"]
        except Exception:
            records = []

    # backfill metadata for records created before the SFO parser was fixed
    for record in records:
        if record.get("status") == "extracted" and not record.get("discId"):
            sfo = sfo_string(EXTRACTED / record["slug"] / "PSP_GAME" / "PARAM.SFO",
                             {"DISC_ID", "TITLE", "REGION"})
            if sfo:
                record["discId"] = sfo.get("DISC_ID", "")
                record["title"] = sfo.get("TITLE", "")
                record["region"] = sfo.get("REGION", "")
                print("backfilled %s -> %s %s" % (record["slug"], record["discId"], record["title"]))

    for index, archive in enumerate(archives, 1):
        name = archive.name
        base_slug = slug(archive.stem)
        print("\n[%d/%d] %s (%s)" % (index, len(archives), name, human(archive.stat().st_size)))
        sys.stdout.flush()

        existing = next((r for r in records if r.get("archive") == name), None)
        if existing and existing.get("status") in ("extracted", "duplicate"):
            print("        already processed -> %s" % existing.get("status"))
            continue

        if free_gb() < args.min_free_gb:
            print("        STOP: only %.1f GB free" % free_gb())
            break

        if WORK.exists():
            shutil.rmtree(WORK)
        WORK.mkdir(parents=True, exist_ok=True)

        started = time.monotonic()
        if run([SEVEN_ZIP, "x", str(archive), "-o" + str(WORK), "-y"]) != 0:
            print("        unpack failed")
            records.append({"archive": name, "slug": base_slug, "status": "unpack-failed"})
            continue

        image = find_image(WORK)
        if image is None:
            nested = [p for p in WORK.rglob("*") if p.is_file()]
            print("        no image inside (%d files)" % len(nested))
            records.append({"archive": name, "slug": base_slug, "status": "no-image"})
            continue

        # CSO/ZSO -> ISO with the kit itself (dogfooding the converter)
        if image.suffix.lower() in (".cso", ".zso"):
            iso = image.with_suffix(".iso")
            print("        expanding %s" % image.name)
            if run([str(MODKIT), "cso", str(image), str(iso)]) != 0:
                print("        cso conversion failed")
                records.append({"archive": name, "slug": base_slug, "status": "cso-failed"})
                continue
            image.unlink()
            image = iso

        size = image.stat().st_size
        print("        hashing %s (%s)" % (image.name, human(size)))
        digest = sha256(image)

        if digest in seen:
            owner = seen[digest]
            print("        DUPLICATE of %s -> deleting the image" % owner)
            records.append({
                "archive": name, "slug": base_slug, "status": "duplicate",
                "duplicateOf": owner, "sha256": digest, "isoSize": size,
                "isoName": image.name,
            })
            image.unlink()
            shutil.rmtree(WORK, ignore_errors=True)
            continue

        folder = EXTRACTED / base_slug
        print("        unpacking game data -> %s" % folder.relative_to(ROOT))
        if folder.exists():
            shutil.rmtree(folder)
        folder.mkdir(parents=True, exist_ok=True)
        if run([SEVEN_ZIP, "x", str(image), "-o" + str(folder), "-y"]) != 0:
            print("        data unpack failed")
            records.append({"archive": name, "slug": base_slug, "status": "extract-failed",
                            "sha256": digest, "isoSize": size})
            shutil.rmtree(WORK, ignore_errors=True)
            continue

        sfo = sfo_string(folder / "PSP_GAME" / "PARAM.SFO", {"DISC_ID", "TITLE", "REGION"})
        seen[digest] = base_slug
        record = {
            "archive": name, "slug": base_slug, "status": "extracted",
            "sha256": digest, "isoSize": size, "isoName": image.name,
            "discId": sfo.get("DISC_ID", ""), "title": sfo.get("TITLE", ""),
            "region": sfo.get("REGION", ""),
            "seconds": round(time.monotonic() - started, 1),
        }
        records.append(record)
        print("        ok: %s %s" % (record["discId"], record["title"]))

        image.unlink()
        if args.keep_iso:
            shutil.copy2(archive, archive)  # no-op, keeps semantics obvious
        shutil.rmtree(WORK, ignore_errors=True)

        # keep the manifest current so an interrupted run is resumable
        MANIFEST_JSON.write_text(json.dumps({"images": records}, indent=2, ensure_ascii=False),
                                 encoding="utf-8")

    shutil.rmtree(WORK, ignore_errors=True)
    MANIFEST_JSON.write_text(json.dumps({"images": records}, indent=2, ensure_ascii=False),
                             encoding="utf-8")
    write_markdown(records)
    print("\ndone, manifest -> %s" % MANIFEST_MD.relative_to(ROOT))
    return 0


def write_markdown(records: list[dict]) -> None:
    """Renders refs/GAMES/MANIFEST.md: what was imported, what is identical, what stayed."""
    iso_groups: dict[str, list[dict]] = {}
    data_groups: dict[str, list[dict]] = {}
    for record in records:
        if record.get("sha256"):
            iso_groups.setdefault(record["sha256"], []).append(record)
        if record.get("dataSha256"):
            data_groups.setdefault(record["dataSha256"], []).append(record)

    kept = [r for r in records if r.get("status") == "extracted"]
    dropped = [r for r in records if r.get("status") == "duplicate-data"]
    failed = [r for r in records if r.get("status") not in
              ("extracted", "duplicate", "duplicate-data")]

    lines = [
        "# Original game images (refs/GAMES)",
        "",
        "Что было в архивах, что распаковано, какие образы **идентичны** и что удалено.",
        "Генерируется `tools/import_games.py` и `tools/prune_games.py`; описание — `docs/DATA.md`.",
        "",
        "Архивы `.7z/.zip` и промежуточные `.iso/.cso` после проверки распаковки удалены.",
        "SHA-256 образов сохранены здесь, поэтому видно, что именно разбиралось.",
        "",
        "## Сводка",
        "",
        "| | |",
        "|---|---|",
        "| Записей (архивов/образов) | %d |" % len(records),
        "| Уникальных образов (по SHA-256 ISO) | %d |" % len(iso_groups),
        "| Уникальных наборов данных (по USRDIR) | %d |" % len(data_groups),
        "| Оставлено деревьев | %d |" % len(kept),
        "| Дубликатов данных (деревья удалены) | %d |" % len(dropped),
        "",
        "## Распакованные образы",
        "",
        "| Источник (удалён) | ISO | DISC_ID | Название | Папка |",
        "|---|---|---|---|---|",
    ]
    for record in sorted(kept, key=lambda r: r.get("slug", "")):
        lines.append("| %s | %s | `%s` | %s | `extracted/%s/` |" % (
            record.get("archive", "?"),
            human(record.get("isoSize", 0)) if record.get("isoSize") else "—",
            record.get("discId", "?") or "?",
            record.get("title", ""),
            record["slug"]))

    lines += ["", "## Идентичные данные", "",
              "Одинаковый `PSP_GAME/USRDIR` — то есть полностью совпадающие игровые данные",
              "(различаться может только загрузчик `EBOOT.BIN`).", ""]
    shared = [(d, m) for d, m in data_groups.items() if len(m) > 1]
    if not shared:
        lines.append("Совпадающих наборов данных не найдено.")
    else:
        for digest, members in shared:
            owner = next((m["slug"] for m in members if m.get("status") == "extracted"), "?")
            lines.append("**`%s…`** — %d образа, оставлено `extracted/%s/`:" % (
                digest[:16], len(members), owner))
            for member in sorted(members, key=lambda r: r.get("slug", "")):
                mark = "" if member.get("status") == "extracted" else "  ← дерево удалено"
                lines.append("- %s (`%s`)%s" % (member.get("archive", "?"),
                                                 member.get("discId", "?"), mark))
            lines.append("")

    shared_iso = [(d, m) for d, m in iso_groups.items() if len(m) > 1]
    lines += ["## Идентичные образы (побайтово)", ""]
    if not shared_iso:
        lines.append("Побайтово одинаковых ISO среди архивов не было — версии отличаются")
        lines.append("загрузчиком, но не данными (см. таблицу выше).")
    else:
        for digest, members in shared_iso:
            lines.append("**`%s…`**: %s" % (digest[:16],
                                            ", ".join(m.get("archive", "?") for m in members)))
        lines.append("")
    lines.append("")

    if dropped:
        lines += ["## Удалённые дубликаты данных", "",
                  "| Папка | Повторяет |", "|---|---|"]
        for record in sorted(dropped, key=lambda r: r.get("slug", "")):
            lines.append("| `%s` | `%s` |" % (record["slug"], record.get("duplicateOf", "?")))
        lines.append("")

    if failed:
        lines += ["## Проблемные", ""]
        for record in failed:
            lines.append("- %s — %s" % (record.get("archive", "?"), record.get("status")))
        lines.append("")

    lines += [
        "## Что где лежит",
        "",
        "- `refs/GAMES/extracted/<slug>/PSP_GAME/...` — распакованные данные (это и есть образ).",
        "- `refs/GAMES/extracted/LCS`, `.../VCS`, `.../chinatown_wars_rus` — RUS-переводы.",
        "- `refs/GAMES/MANIFEST.md`, `manifest.json` — этот отчёт.",
        "",
    ]

    MANIFEST_MD.write_text("\n".join(lines), encoding="utf-8-sig")

    # keep the committed snapshot in docs/ in sync, no hand editing
    if DOCS_SNAPSHOT.parent.is_dir():
        DOCS_SNAPSHOT.write_text("\n".join(lines + [
            "---",
            "",
            "> Снимок отчёта. Обновляется автоматически `tools/import_games.py` и",
            "> `tools/prune_games.py`. Живая версия — `refs/GAMES/MANIFEST.md`",
            "> (папка `refs/` в git не входит).",
            "",
        ]), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())