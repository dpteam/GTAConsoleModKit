"""Re-renders refs/GAMES/MANIFEST.md and docs/GAME_IMAGES.md from manifest.json.

Cheap: no hashing, no unpacking - just the report from the stored records.

    python tools/render_manifest.py
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import import_games  # noqa: E402


def main() -> int:
    if not import_games.MANIFEST_JSON.exists():
        print("no manifest.json yet - run tools/import_games.py first")
        return 1
    records = json.loads(import_games.MANIFEST_JSON.read_text(encoding="utf-8"))["images"]
    import_games.write_markdown(records)
    print("manifest re-rendered (%d records) -> %s, %s" % (
        len(records),
        import_games.MANIFEST_MD.relative_to(import_games.ROOT),
        import_games.DOCS_SNAPSHOT.relative_to(import_games.ROOT)))
    return 0


if __name__ == "__main__":
    sys.exit(main())