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


def get_top3_clusters(row):
    clusters = row.get("topClusters") or row.get("topBlind") or []
    names = []
    for c in clusters[:3]:
        name = c.get("cluster") if isinstance(c, dict) else None
        if name:
            names.append(str(name).lower())
    return tuple(names)


def top3_consistency(blind_rows):
    if not blind_rows:
        return 0.0, ()
    seq = [get_top3_clusters(r) for r in blind_rows if get_top3_clusters(r)]
    if not seq:
        return 0.0, ()
    base = seq[0]
    same = sum(1 for x in seq if x == base)
    return same * 100.0 / len(seq), base


def main():
    parser = argparse.ArgumentParser(description="Generate viability report from live/blind signal files")
    parser.add_argument("--file", default="E:/DouyinBarrageGrab/Output/logs/live_control_signal.jsonl")
    parser.add_argument("--blind-file", default="E:/DouyinBarrageGrab/Output/logs/blind_control_signal.jsonl")
    parser.add_argument("--last", type=int, default=10, help="Only analyze last N records (default: 10)")
    args = parser.parse_args()

    path = Path(args.file)
    rows = load_lines(path)
    if not rows:
        print(f"[report] no valid records: {path}")
        return 1

    if args.last > 0:
        rows = rows[-args.last :]

    blind_path = Path(args.blind_file)
    blind_rows = load_lines(blind_path)
    if args.last > 0:
        blind_rows = blind_rows[-args.last :]

    levels = Counter(str(r.get("level", "UNKNOWN")).upper() for r in rows)
    total = len(rows)
    confirmed = levels.get("CONFIRMED", 0)
    weak = levels.get("WEAK", 0)
    no_hit = levels.get("NO_HIT", 0)
    bypass = sum(1 for r in rows if bool(r.get("bypassConfirmed", False)))
    top3_rate, top3_base = top3_consistency(blind_rows)

    def ratio(v):
        return f"{(v * 100.0 / total):.1f}%"

    print("[report] method viability")
    print(f"- records: {total}")
    print(f"- confirmed: {confirmed} ({ratio(confirmed)})")
    print(f"- weak: {weak} ({ratio(weak)})")
    print(f"- no_hit: {no_hit} ({ratio(no_hit)})")
    print(f"- bypass_confirmed: {bypass} ({ratio(bypass)})")
    if blind_rows:
        print(f"- blind_records: {len(blind_rows)}")
        print(f"- blind_top3_consistency: {top3_rate:.1f}%")
        if top3_base:
            print(f"- blind_top3_baseline: {list(top3_base)}")
    else:
        print(f"- blind_records: 0 (file missing or empty: {blind_path})")

    c_plus_w = confirmed + weak
    c_plus_w_rate = c_plus_w * 100.0 / total if total else 0.0
    no_hit_rate = no_hit * 100.0 / total if total else 0.0
    pass_top3 = top3_rate >= 80.0 if blind_rows else False
    pass_signal = c_plus_w_rate >= 90.0 and no_hit_rate <= 10.0
    print("- thresholds:")
    print(f"  top3_consistency>=80%: {'PASS' if pass_top3 else 'FAIL'}")
    print(f"  confirmed+weak>=90% and no_hit<=10%: {'PASS' if pass_signal else 'FAIL'}")
    print(f"- verdict: {'PASS' if (pass_top3 and pass_signal) else 'FAIL'}")

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
