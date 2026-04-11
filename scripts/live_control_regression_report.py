#!/usr/bin/env python3
import argparse
import json
from collections import Counter
from pathlib import Path


def load_lines(path: Path):
    rows = []
    if not path.exists():
        return rows
    for line in path.read_text(encoding="utf-8", errors="ignore").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            rows.append(json.loads(line))
        except Exception:
            continue
    return rows


def main():
    parser = argparse.ArgumentParser(description="Generate regression summary from live_control_signal.jsonl")
    parser.add_argument("--file", default="E:/DouyinBarrageGrab/Output/logs/live_control_signal.jsonl")
    parser.add_argument("--last", type=int, default=10, help="Only analyze last N records (default: 10)")
    args = parser.parse_args()

    path = Path(args.file)
    rows = load_lines(path)
    if not rows:
        print(f"[report] no valid records: {path}")
        return 1

    if args.last > 0:
        rows = rows[-args.last :]

    levels = Counter(str(r.get("level", "UNKNOWN")).upper() for r in rows)
    total = len(rows)
    confirmed = levels.get("CONFIRMED", 0)
    weak = levels.get("WEAK", 0)
    no_hit = levels.get("NO_HIT", 0)
    bypass = sum(1 for r in rows if bool(r.get("bypassConfirmed", False)))

    def ratio(v):
        return f"{(v * 100.0 / total):.1f}%"

    print("[report] live_control regression")
    print(f"- records: {total}")
    print(f"- confirmed: {confirmed} ({ratio(confirmed)})")
    print(f"- weak: {weak} ({ratio(weak)})")
    print(f"- no_hit: {no_hit} ({ratio(no_hit)})")
    print(f"- bypass_confirmed: {bypass} ({ratio(bypass)})")

    print("- latest:")
    for row in rows[-5:]:
        print(
            f"  {row.get('ts','?')} trace={row.get('traceId','?')} "
            f"level={row.get('level','?')} bypass={row.get('bypassConfirmed', False)} "
            f"gifshow={row.get('gifshowApiHits',0)} ksapisrv={row.get('ksapisrvApiHits',0)}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
