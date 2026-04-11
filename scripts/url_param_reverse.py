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
    # Try one decode pass for encoded URLs.
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


def is_likely_dynamic(values):
    if not values:
        return False
    total = sum(values.values())
    distinct = len(values)
    top_count = values.most_common(1)[0][1]
    top_ratio = top_count / total if total else 1.0
    # Heuristic: dynamic if many distinct values and no strong dominant value.
    return distinct >= 5 and top_ratio <= 0.4


def main():
    parser = argparse.ArgumentParser(description="Reverse-engineer URL query params from captured logs")
    parser.add_argument("--logs-dir", default="E:/DouyinBarrageGrab/Output/logs")
    parser.add_argument("--out-dir", default="E:/DouyinBarrageGrab/Output/logs")
    parser.add_argument("--max-files", type=int, default=2000)
    parser.add_argument("--max-sample-values", type=int, default=8)
    args = parser.parse_args()

    logs_dir = Path(args.logs_dir)
    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    if not logs_dir.exists():
        print(f"[url-reverse] logs dir not found: {logs_dir}")
        return 1

    files = []
    for g in SCAN_GLOBS:
        files.extend(logs_dir.glob(g))
    files = sorted(set(files))[: args.max_files]

    total_urls = 0
    url_counter = Counter()
    host_counter = Counter()
    path_counter = Counter()
    scheme_counter = Counter()
    param_counter = Counter()
    param_values = defaultdict(Counter)
    param_host_paths = defaultdict(Counter)

    for fp in files:
        try:
            text = fp.read_text(encoding="utf-8", errors="ignore")
        except Exception:
            continue
        for url in extract_urls(text):
            total_urls += 1
            url_counter[url] += 1
            p = urlparse(url)
            host = (p.hostname or "").lower()
            path = p.path or "/"
            hp = f"{host}{path}"
            host_counter[host] += 1
            path_counter[hp] += 1
            scheme_counter[(p.scheme or "").lower()] += 1
            for k, v in parse_qsl(p.query, keep_blank_values=True):
                key = k.strip()
                if not key:
                    continue
                val = v.strip()
                param_counter[key] += 1
                param_values[key][val] += 1
                param_host_paths[key][hp] += 1

    dictionary = {
        "meta": {
            "logsDir": str(logs_dir),
            "filesScanned": len(files),
            "totalUrls": total_urls,
        },
        "topSchemes": [{"scheme": k, "count": c} for k, c in scheme_counter.most_common(10)],
        "topHosts": [{"host": k, "count": c} for k, c in host_counter.most_common(30)],
        "topPaths": [{"hostPath": k, "count": c} for k, c in path_counter.most_common(40)],
        "topUrls": [{"url": k, "count": c} for k, c in url_counter.most_common(40)],
        "topParams": [],
    }

    stability_items = []
    for key, count in param_counter.most_common():
        values = param_values[key]
        hosts = param_host_paths[key]
        distinct = len(values)
        top_value = values.most_common(1)[0][0] if values else ""
        top_value_count = values.most_common(1)[0][1] if values else 0
        dynamic = is_likely_dynamic(values)
        sample_values = [v for v, _ in values.most_common(args.max_sample_values)]
        sample_hosts = [h for h, _ in hosts.most_common(8)]
        dictionary["topParams"].append(
            {
                "param": key,
                "count": count,
                "distinctValues": distinct,
                "topValue": top_value,
                "topValueCount": top_value_count,
                "sampleValues": sample_values,
                "sampleHostPaths": sample_hosts,
            }
        )
        stability_items.append(
            {
                "param": key,
                "count": count,
                "distinctValues": distinct,
                "dynamicLikely": dynamic,
                "stableLikely": not dynamic,
                "sampleValues": sample_values,
            }
        )

    stability_report = {
        "meta": dictionary["meta"],
        "summary": {
            "paramCount": len(param_counter),
            "dynamicParamCount": sum(1 for x in stability_items if x["dynamicLikely"]),
            "stableParamCount": sum(1 for x in stability_items if x["stableLikely"]),
        },
        "params": stability_items,
    }

    dict_path = out_dir / "url_param_dictionary.json"
    stability_path = out_dir / "url_param_stability_report.json"
    dict_path.write_text(json.dumps(dictionary, ensure_ascii=False, indent=2), encoding="utf-8")
    stability_path.write_text(json.dumps(stability_report, ensure_ascii=False, indent=2), encoding="utf-8")

    print("[url-reverse] done")
    print(f"- files_scanned: {len(files)}")
    print(f"- total_urls: {total_urls}")
    print(f"- unique_params: {len(param_counter)}")
    print(f"- dictionary: {dict_path}")
    print(f"- stability: {stability_path}")
    print("- top_params:")
    for row in dictionary["topParams"][:12]:
        print(
            f"  {row['param']}: count={row['count']} distinct={row['distinctValues']} "
            f"top={row['topValue']}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
