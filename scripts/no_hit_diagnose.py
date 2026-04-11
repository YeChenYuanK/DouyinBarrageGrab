#!/usr/bin/env python3
import argparse
import json
from collections import Counter
from pathlib import Path


def read_jsonl(path: Path):
    rows = []
    if not path.exists():
        return rows
    for line in path.read_text(encoding="utf-8", errors="ignore").splitlines():
        line = line.strip().lstrip("\ufeff")
        if not line:
            continue
        try:
            rows.append(json.loads(line))
        except Exception:
            pass
    return rows


def read_summary(summary_path: Path):
    hosts = []
    if not summary_path.exists():
        return hosts
    for line in summary_path.read_text(encoding="utf-8", errors="ignore").splitlines():
        line = line.strip()
        if not line:
            continue
        # format: "host count"
        parts = line.rsplit(" ", 1)
        host = parts[0].strip()
        if host:
            hosts.append(host.lower())
    return hosts


def classify_reason(hosts):
    s = " ".join(hosts)
    if not hosts:
        return "EMPTY_CAPTURE"
    has_gifshow = "gifshow.com" in s
    has_ksapi = "ksapisrv.com" in s
    has_ws = "wsukwai.com" in s
    has_mate = "mate.gifshow.com" in s
    if has_gifshow and not has_ksapi:
        return "SINGLE_CLUSTER_GIFSHOW"
    if has_ksapi and not has_gifshow:
        return "SINGLE_CLUSTER_KSAPISRV"
    if (has_ws or has_mate) and (not has_gifshow and not has_ksapi):
        return "AUX_ONLY_WINDOW"
    return "UNKNOWN_PATTERN"


def main():
    parser = argparse.ArgumentParser(description="Diagnose NO_HIT rounds from signal + summary files")
    parser.add_argument("--signal", default="E:/DouyinBarrageGrab/Output/logs/live_control_signal.jsonl")
    parser.add_argument("--logs-dir", default="E:/DouyinBarrageGrab/Output/logs")
    parser.add_argument("--last", type=int, default=30, help="Inspect last N signal rows")
    args = parser.parse_args()

    signal_path = Path(args.signal)
    logs_dir = Path(args.logs_dir)
    rows = read_jsonl(signal_path)
    if not rows:
        print(f"[diagnose] no signal rows: {signal_path}")
        return 1
    rows = rows[-args.last :] if args.last > 0 else rows

    no_hit = [r for r in rows if str(r.get("level", "")).upper() == "NO_HIT"]
    print(f"[diagnose] inspected={len(rows)} no_hit={len(no_hit)}")
    if not no_hit:
        print("[diagnose] no NO_HIT rows in selected range")
        return 0

    reasons = Counter()
    for r in no_hit:
        trace = r.get("traceId", "")
        summary = logs_dir / f"{trace}.sni.summary.txt"
        hosts = read_summary(summary)
        reason = classify_reason(hosts)
        reasons[reason] += 1
        top5 = ", ".join(hosts[:5]) if hosts else "(empty)"
        print(
            f"- trace={trace} ts={r.get('ts','?')} totalSni={r.get('totalSni',0)} "
            f"uniqueSni={r.get('uniqueSni',0)} reason={reason} top={top5}"
        )

    print("[diagnose] reason_summary:")
    for k, v in reasons.most_common():
        print(f"  {k}: {v}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
