#!/usr/bin/env python3
import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path
from urllib.parse import parse_qsl, unquote, urlparse


URL_RE = re.compile(r"(?i)\b(?:https?|rtmp)://[^\s\"'<>]+")
SCAN_GLOBS = ("**/*.log", "**/*.txt", "**/*.jsonl")


def clean_url(raw: str) -> str:
    url = raw.strip().strip(".,;)]}")
    if "%3A%2F%2F" in url.lower():
        try:
            url = unquote(url)
        except Exception:
            pass
    return url


def extract_urls(text: str):
    for m in URL_RE.finditer(text):
        u = clean_url(m.group(0))
        if u:
            yield u


def pair_key(a: str, b: str):
    return (a, b) if a <= b else (b, a)


def main():
    parser = argparse.ArgumentParser(description="Build URL param dependency graph from logs")
    parser.add_argument("--logs-dir", default="E:/DouyinBarrageGrab/Output/logs")
    parser.add_argument("--out-dir", default="E:/DouyinBarrageGrab/Output/logs")
    parser.add_argument("--max-files", type=int, default=2000)
    parser.add_argument("--host-include", default="", help="Comma-separated host keywords to include")
    parser.add_argument("--host-exclude", default="", help="Comma-separated host keywords to exclude")
    parser.add_argument("--min-param-count", type=int, default=20)
    parser.add_argument("--min-pair-count", type=int, default=10)
    parser.add_argument("--suffix", default="", help="Optional output filename suffix, e.g. _kuaishou")
    args = parser.parse_args()

    logs_dir = Path(args.logs_dir)
    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    include_keywords = [x.strip().lower() for x in args.host_include.split(",") if x.strip()]
    exclude_keywords = [x.strip().lower() for x in args.host_exclude.split(",") if x.strip()]

    files = []
    for g in SCAN_GLOBS:
        files.extend(logs_dir.glob(g))
    files = sorted(set(files))[: args.max_files]

    param_count = Counter()
    pair_count = Counter()
    host_count = Counter()
    url_total = 0
    url_with_params = 0
    param_hosts = defaultdict(Counter)

    for fp in files:
        try:
            text = fp.read_text(encoding="utf-8", errors="ignore")
        except Exception:
            continue
        for raw_url in extract_urls(text):
            p = urlparse(raw_url)
            host = (p.hostname or "").lower()
            if include_keywords and not any(k in host for k in include_keywords):
                continue
            if exclude_keywords and any(k in host for k in exclude_keywords):
                continue

            url_total += 1
            host_count[host] += 1

            keys = []
            for k, _ in parse_qsl(p.query, keep_blank_values=True):
                k = (k or "").strip()
                if not k:
                    continue
                keys.append(k)

            unique_keys = sorted(set(keys))
            if not unique_keys:
                continue

            url_with_params += 1
            for k in unique_keys:
                param_count[k] += 1
                param_hosts[k][host] += 1

            for i in range(len(unique_keys)):
                for j in range(i + 1, len(unique_keys)):
                    pair_count[(unique_keys[i], unique_keys[j])] += 1

    # Filter low-support params to avoid noisy graph.
    kept_params = {k for k, c in param_count.items() if c >= args.min_param_count}
    nodes = []
    for k, c in param_count.most_common():
        if k not in kept_params:
            continue
        top_hosts = [h for h, _ in param_hosts[k].most_common(5)]
        nodes.append({"param": k, "count": c, "topHosts": top_hosts})

    edges = []
    for (a, b), c in pair_count.most_common():
        if c < args.min_pair_count:
            continue
        if a not in kept_params or b not in kept_params:
            continue
        conf_ab = c / param_count[a] if param_count[a] else 0.0
        conf_ba = c / param_count[b] if param_count[b] else 0.0
        edges.append(
            {
                "a": a,
                "b": b,
                "count": c,
                "confidenceAtoB": round(conf_ab, 4),
                "confidenceBtoA": round(conf_ba, 4),
            }
        )

    output = {
        "meta": {
            "logsDir": str(logs_dir),
            "filesScanned": len(files),
            "urlTotal": url_total,
            "urlWithParams": url_with_params,
            "hostInclude": include_keywords,
            "hostExclude": exclude_keywords,
            "minParamCount": args.min_param_count,
            "minPairCount": args.min_pair_count,
        },
        "topHosts": [{"host": h, "count": c} for h, c in host_count.most_common(30)],
        "nodes": nodes,
        "edges": edges,
    }

    suffix = args.suffix.strip()
    out_name = f"url_param_dependency_graph{suffix}.json" if suffix else "url_param_dependency_graph.json"
    out_path = out_dir / out_name
    out_path.write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")

    print("[url-dependency] done")
    print(f"- files_scanned: {len(files)}")
    print(f"- url_total: {url_total}")
    print(f"- url_with_params: {url_with_params}")
    print(f"- nodes: {len(nodes)}")
    print(f"- edges: {len(edges)}")
    print(f"- output: {out_path}")
    print("- top_edges:")
    for e in edges[:12]:
        print(
            f"  {e['a']} <-> {e['b']} count={e['count']} "
            f"confAtoB={e['confidenceAtoB']} confBtoA={e['confidenceBtoA']}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
