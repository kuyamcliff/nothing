import json
import os
from datetime import datetime
from pathlib import Path


LOG_PATH = Path.home() / ".nothing" / "log.json"


def _load() -> list:
    if not LOG_PATH.exists():
        return []
    with open(LOG_PATH) as f:
        return json.load(f)


def _save(entries: list) -> None:
    LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
    with open(LOG_PATH, "w") as f:
        json.dump(entries, f, indent=2)


def record(label: str, seconds: int) -> None:
    entries = _load()
    entries.append({
        "timestamp": datetime.now().isoformat(),
        "label": label,
        "seconds": seconds,
    })
    _save(entries)


def show_history(limit: int = 10) -> None:
    entries = _load()
    if not entries:
        print("\n  No nothing sessions recorded yet.\n")
        return

    print(f"\n  Last {min(limit, len(entries))} sessions:\n")
    for e in entries[-limit:]:
        ts = datetime.fromisoformat(e["timestamp"]).strftime("%Y-%m-%d %H:%M")
        m, s = divmod(e["seconds"], 60)
        dur = f"{m}m {s}s" if m else f"{s}s"
        print(f"  {ts}  [{e['label']:20s}]  {dur}")
    print()

    total = sum(e["seconds"] for e in entries)
    h, rem = divmod(total, 3600)
    m = rem // 60
    print(f"  Total nothing: {h}h {m}m across {len(entries)} sessions\n")


def clear_history() -> None:
    _save([])
    print("\n  History cleared.\n")
