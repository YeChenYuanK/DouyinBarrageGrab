#!/usr/bin/env python3
"""Kuaishou live WebSocket client (standalone).

Usage:
1) Load URL + first auth send frame from HAR-like json file.
2) Open websocket connection with browser-like headers.
3) Send auth frame and print incoming binary frames.

Notes:
- This script opens a NEW ws connection; it cannot attach to existing Chrome connection.
- Requires: websocket-client
"""

from __future__ import annotations

import argparse
import base64
import gzip
import json
import re
import signal
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Optional


@dataclass
class WsBootstrap:
    """Bootstrap data extracted from HAR-like ws export."""

    ws_url: str
    origin: str
    user_agent: str
    auth_send_payload: bytes
    heartbeat_payloads: list[bytes]
    source_tag: str


def _iter_entries(obj: object) -> Iterable[dict]:
    """Returns log entries from HAR-like object."""
    if not isinstance(obj, dict):
        return []
    return obj.get("log", {}).get("entries", []) or []


def _extract_ws_url(entry: dict) -> Optional[str]:
    """Extracts ws URL from entry request."""
    req = entry.get("request", {}) if isinstance(entry, dict) else {}
    url = req.get("url", "")
    if isinstance(url, str) and url.startswith(("ws://", "wss://")):
        return url
    return None


def _extract_header(entry: dict, name: str, fallback: str = "") -> str:
    """Extracts request header value from entry, case-insensitive."""
    req = entry.get("request", {}) if isinstance(entry, dict) else {}
    headers = req.get("headers", []) or []
    for h in headers:
        if not isinstance(h, dict):
            continue
        if str(h.get("name", "")).lower() == name.lower():
            return str(h.get("value", ""))
    return fallback


def _first_send_binary_payload(entry: dict) -> Optional[bytes]:
    """Finds first ws send frame opcode=2 and decodes base64 data."""
    msgs = entry.get("_webSocketMessages", []) if isinstance(entry, dict) else []
    for m in msgs:
        if not isinstance(m, dict):
            continue
        if m.get("type") != "send":
            continue
        if m.get("opcode") != 2:
            continue
        data = m.get("data", "")
        if not isinstance(data, str) or not data:
            continue
        try:
            return base64.b64decode(data, validate=True)
        except Exception:
            try:
                return base64.b64decode(data + "===")
            except Exception:
                continue
    return None


def _heartbeat_send_payloads(entry: dict) -> list[bytes]:
    """Finds heartbeat-like send opcode=2 payloads after auth frame."""
    msgs = entry.get("_webSocketMessages", []) if isinstance(entry, dict) else []
    payloads: list[bytes] = []
    first_send_seen = False
    for m in msgs:
        if not isinstance(m, dict):
            continue
        if m.get("type") != "send" or m.get("opcode") != 2:
            continue
        data = m.get("data", "")
        if not isinstance(data, str) or not data:
            continue
        try:
            raw = base64.b64decode(data, validate=True)
        except Exception:
            try:
                raw = base64.b64decode(data + "===")
            except Exception:
                continue

        if not first_send_seen:
            first_send_seen = True
            continue

        # heartbeat包一般较短；这里保守限制，避免误把业务包当心跳
        if 4 <= len(raw) <= 64:
            payloads.append(raw)

    # 去重并保持顺序
    out: list[bytes] = []
    seen = set()
    for p in payloads:
        k = p.hex()
        if k in seen:
            continue
        seen.add(k)
        out.append(p)
    return out


def _decode_ws_payload_b64(raw: str) -> Optional[bytes]:
    """Decodes websocket base64 payload safely."""
    if not raw or not isinstance(raw, str):
        return None
    try:
        return base64.b64decode(raw, validate=True)
    except Exception:
        try:
            return base64.b64decode(raw + "===")
        except Exception:
            return None


def _build_bootstrap_from_entry(entry: dict, source_tag: str) -> Optional[WsBootstrap]:
    """Builds one bootstrap object from one HAR entry."""
    ws_url = _extract_ws_url(entry)
    if not ws_url:
        return None
    auth_payload = _first_send_binary_payload(entry)
    if not auth_payload:
        return None
    heartbeat_payloads = _heartbeat_send_payloads(entry)
    origin = _extract_header(entry, "origin", "https://live.kuaishou.com")
    user_agent = _extract_header(
        entry,
        "user-agent",
        (
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36"
        ),
    )
    return WsBootstrap(
        ws_url=ws_url,
        origin=origin,
        user_agent=user_agent,
        auth_send_payload=auth_payload,
        heartbeat_payloads=heartbeat_payloads,
        source_tag=source_tag,
    )


def load_bootstraps_from_har(file_path: Path, max_sessions: int) -> list[WsBootstrap]:
    """Loads multiple bootstrap candidates from HAR-like file."""
    text = file_path.read_text(encoding="utf-8", errors="ignore")
    obj = None
    try:
        obj = json.loads(text)
    except Exception:
        try:
            obj = json.loads(text.lstrip("\ufeff"))
        except Exception:
            # Tolerate trailing non-JSON text (e.g. copied Request/Response headers).
            obj, _ = json.JSONDecoder().raw_decode(text.lstrip("\ufeff"))

    out: list[WsBootstrap] = []
    seen = set()
    for i, entry in enumerate(_iter_entries(obj), start=1):
        bs = _build_bootstrap_from_entry(entry, source_tag=f"entry#{i}")
        if not bs:
            continue
        key = (bs.ws_url, bs.auth_send_payload.hex())
        if key in seen:
            continue
        seen.add(key)
        out.append(bs)
        if max_sessions > 0 and len(out) >= max_sessions:
            break

    return out


def load_bootstrap_from_har(file_path: Path) -> WsBootstrap:
    """Loads one bootstrap info from HAR-like file."""
    items = load_bootstraps_from_har(file_path, max_sessions=1)
    if not items:
        raise ValueError(f"no websocket entry with send/opcode=2 found in: {file_path}")
    return items[0]


def _preview_hex(data: bytes, max_len: int = 24) -> str:
    """Returns short hex preview."""
    if not data:
        return ""
    head = data[:max_len]
    return " ".join(f"{b:02X}" for b in head)


def _read_varint(data: bytes, idx: int) -> tuple[Optional[int], int]:
    """Reads protobuf varint."""
    shift = 0
    value = 0
    n = len(data)
    while idx < n and shift <= 63:
        b = data[idx]
        idx += 1
        value |= (b & 0x7F) << shift
        if (b & 0x80) == 0:
            return value, idx
        shift += 7
    return None, idx


def _extract_len_delimited_chunks(data: bytes, max_chunks: int = 256) -> list[bytes]:
    """Extracts protobuf length-delimited chunks from bytes."""
    out: list[bytes] = []
    idx = 0
    n = len(data)
    while idx < n and len(out) < max_chunks:
        key, idx2 = _read_varint(data, idx)
        if key is None:
            break
        idx = idx2
        wt = key & 0x07
        if wt == 0:
            _, idx = _read_varint(data, idx)
            continue
        if wt == 1:
            idx += 8
            continue
        if wt == 2:
            ln, idx = _read_varint(data, idx)
            if ln is None:
                break
            if idx + ln > n:
                break
            chunk = data[idx : idx + ln]
            idx += ln
            out.append(chunk)
            continue
        if wt == 5:
            idx += 4
            continue
        break
    return out


def _maybe_gzip_decompress(data: bytes) -> Optional[bytes]:
    """Decompresses gzip data if it has gzip header."""
    if not data or len(data) < 3:
        return None
    if not (data[0] == 0x1F and data[1] == 0x8B and data[2] == 0x08):
        return None
    try:
        return gzip.decompress(data)
    except Exception:
        return None


def _extract_zh_text_hints(data: bytes, min_len: int = 2) -> list[str]:
    """Extracts Chinese-rich text snippets from bytes."""
    if not data:
        return []
    text = data.decode("utf-8", errors="ignore")
    if not text:
        return []
    candidates = re.findall(rf"[\u4e00-\u9fffA-Za-z0-9，。！？、；：“”‘’（）【】《》…\s]{{{max(2, min_len)},80}}", text)
    out: list[str] = []
    seen = set()
    for c in candidates:
        v = c.strip()
        if not v:
            continue
        zh_count = sum(1 for ch in v if "\u4e00" <= ch <= "\u9fff")
        if zh_count < 2:
            continue
        if "http" in v.lower():
            continue
        if len(v) > 60:
            v = v[:60]
        if v in seen:
            continue
        seen.add(v)
        out.append(v)
        if len(out) >= 8:
            break
    return out


def _is_noise_text(text: str) -> bool:
    """Checks whether text looks like system/activity text instead of chat."""
    if not text:
        return True
    noise_parts = (
        "本主播已开启",
        "玩法由",
        "公司提供",
        "相关权利义务",
        "如您参与玩法",
        "公开信息和行为",
        "同步至该程序",
        "直播间",
        "粉丝团",
        "守护等级",
        "贵族",
        "秒后关闭",
        "正在点赞主播",
        "去点赞",
    )
    if any(p in text for p in noise_parts):
        return True
    return False


def _looks_like_nickname(text: str) -> bool:
    """Checks whether text looks like nickname."""
    if not text:
        return False
    v = text.strip()
    if not (1 <= len(v) <= 18):
        return False
    if _is_noise_text(v):
        return False
    if any(x in v for x in ("http", "www.", ".com")):
        return False
    if any(x in v for x in ("主播", "直播", "玩法", "公司", "程序", "关闭", "点赞")):
        return False
    zh_count = sum(1 for ch in v if "\u4e00" <= ch <= "\u9fff")
    ascii_count = sum(1 for ch in v if ch.isascii() and ch.isalnum())
    if zh_count == 0 and ascii_count == 0:
        return False
    if len(v) >= 10 and zh_count <= 1:
        return False
    return True


def _looks_like_chat_content(text: str) -> bool:
    """Checks whether text looks like chat content."""
    if not text:
        return False
    v = text.strip()
    if not (2 <= len(v) <= 50):
        return False
    if _is_noise_text(v):
        return False
    if any(x in v for x in ("http", "www.", ".com")):
        return False
    zh_count = sum(1 for ch in v if "\u4e00" <= ch <= "\u9fff")
    if zh_count < 2:
        return False
    return True


def _extract_chat_pairs_from_hints(hints: list[str]) -> list[tuple[str, str]]:
    """Extracts chat-like nickname/content pairs from text hints."""
    out: list[tuple[str, str]] = []
    seen = set()
    normalized = [h.strip().replace("\n", " ") for h in hints if h and h.strip()]

    for h in normalized:
        if "：" in h or ":" in h:
            sep = "：" if "：" in h else ":"
            nick, content = h.split(sep, 1)
            nick = nick.strip()
            content = content.strip()
            if _looks_like_nickname(content) and len(content) <= 6:
                continue
            if _looks_like_nickname(nick) and _looks_like_chat_content(content):
                k = f"{nick}|{content}"
                if k not in seen:
                    seen.add(k)
                    out.append((nick, content))

    for i, h in enumerate(normalized):
        if not _looks_like_nickname(h):
            continue
        for j in range(i + 1, min(i + 4, len(normalized))):
            nxt = normalized[j]
            if nxt == h:
                continue
            if not _looks_like_chat_content(nxt):
                continue
            if _looks_like_nickname(nxt) and len(nxt) <= 6:
                continue
            k = f"{h}|{nxt}"
            if k in seen:
                continue
            seen.add(k)
            out.append((h, nxt))
            break

    return out


def run_offline_chat_scan(har_path: Path, max_messages: int, max_hits: int) -> int:
    """Scans HAR ws receive frames and prints chat-like Chinese text."""
    text = har_path.read_text(encoding="utf-8", errors="ignore")
    try:
        obj = json.loads(text)
    except Exception:
        try:
            obj = json.loads(text.lstrip("\ufeff"))
        except Exception:
            obj, _ = json.JSONDecoder().raw_decode(text.lstrip("\ufeff"))

    total_recv = 0
    hit_count = 0
    print("=== Offline Chat Scan ===")
    print(f"har={har_path}")

    for entry in _iter_entries(obj):
        msgs = entry.get("_webSocketMessages", []) if isinstance(entry, dict) else []
        for i, m in enumerate(msgs):
            if not isinstance(m, dict):
                continue
            if m.get("type") != "receive" or m.get("opcode") != 2:
                continue
            raw = _decode_ws_payload_b64(m.get("data", ""))
            if raw is None:
                continue

            total_recv += 1
            if max_messages > 0 and total_recv > max_messages:
                break

            probe_list: list[bytes] = [raw]
            probe_list.extend(_extract_len_delimited_chunks(raw, max_chunks=64))

            all_hints: list[str] = []
            for probe in probe_list[:80]:
                if not probe:
                    continue
                all_hints.extend(_extract_zh_text_hints(probe, min_len=2))
                g = _maybe_gzip_decompress(probe)
                if g:
                    all_hints.extend(_extract_zh_text_hints(g, min_len=2))
                    for c in _extract_len_delimited_chunks(g, max_chunks=64):
                        all_hints.extend(_extract_zh_text_hints(c, min_len=2))

            dedup = []
            seen = set()
            for h in all_hints:
                if h in seen:
                    continue
                seen.add(h)
                dedup.append(h)
            if not dedup:
                continue

            hit_count += 1
            msg_ts = m.get("time")
            if isinstance(msg_ts, (int, float)):
                ts_text = f"{float(msg_ts):.3f}"
            else:
                ts_text = "na"
            chat_pairs = _extract_chat_pairs_from_hints(dedup)
            print(
                f"[offline-hit#{hit_count}] recv_msg={total_recv} "
                f"frame_idx={i} len={len(raw)} head={_preview_hex(raw)}"
            )
            if chat_pairs:
                for idx, (nick, content) in enumerate(chat_pairs[:3], start=1):
                    print(
                        f"  chat#{idx} ts={ts_text} "
                        f"nickname={nick} content={content}"
                    )
            else:
                for h in dedup[:6]:
                    print(f"  zh={h}")
            if max_hits > 0 and hit_count >= max_hits:
                print("hit limit reached")
                print(f"done: recv_frames={total_recv}, hits={hit_count}")
                return 0
        if max_messages > 0 and total_recv > max_messages:
            break

    print(f"done: recv_frames={total_recv}, hits={hit_count}")
    return 0


def _extract_text_hints(data: bytes, min_text_len: int) -> list[str]:
    """Extracts readable text hints from binary payload."""
    if not data:
        return []
    text = data.decode("utf-8", errors="ignore")
    if not text:
        return []
    hints = re.findall(rf"[ -~]{{{max(3, min_text_len)},}}", text)
    # keep order + de-dup
    out: list[str] = []
    seen = set()
    for h in hints:
        v = h.strip()
        if not v:
            continue
        if len(v) > 120:
            v = v[:117] + "..."
        if v in seen:
            continue
        seen.add(v)
        out.append(v)
        if len(out) >= 6:
            break
    return out


def _find_watch_hits(data: bytes, watch_texts: list[str]) -> list[str]:
    """Finds watch texts in decoded utf-8 payload and returns snippets."""
    if not watch_texts:
        return []
    text = data.decode("utf-8", errors="ignore")
    if not text:
        return []
    hits: list[str] = []
    for w in watch_texts:
        if not w:
            continue
        idx = text.find(w)
        if idx < 0:
            continue
        start = max(0, idx - 16)
        end = min(len(text), idx + len(w) + 16)
        snippet = text[start:end].replace("\n", "\\n")
        if len(snippet) > 140:
            snippet = snippet[:137] + "..."
        hits.append(f"{w} => {snippet}")
    return hits


def run_live_client(
    bootstrap: WsBootstrap,
    recv_limit: int,
    timeout_sec: float,
    heartbeat_interval_sec: float,
    watch_texts: list[str],
    min_text_len: int,
    dump_text_hints: bool,
    log_prefix: str = "",
) -> int:
    """Connects ws, sends auth frame, and prints incoming frames."""
    try:
        import websocket  # type: ignore
    except Exception as ex:
        print("missing dependency: websocket-client", file=sys.stderr)
        print(f"pip install websocket-client  # detail: {ex}", file=sys.stderr)
        return 2

    stop = {"value": False}

    def _sig_handler(_signum: int, _frame: object) -> None:
        stop["value"] = True

    if threading.current_thread() is threading.main_thread():
        signal.signal(signal.SIGINT, _sig_handler)
        signal.signal(signal.SIGTERM, _sig_handler)

    headers = [
        f"Origin: {bootstrap.origin}",
        f"User-Agent: {bootstrap.user_agent}",
        "Cache-Control: no-cache",
        "Pragma: no-cache",
    ]

    def emit(msg: str) -> None:
        if log_prefix:
            print(f"{log_prefix} {msg}")
        else:
            print(msg)

    emit("=== Kuaishou WS Live Client ===")
    emit(f"source={bootstrap.source_tag}")
    emit(f"url={bootstrap.ws_url}")
    emit(f"origin={bootstrap.origin}")
    emit(f"auth_len={len(bootstrap.auth_send_payload)}")
    emit(f"auth_head={_preview_hex(bootstrap.auth_send_payload)}")
    emit(f"heartbeat_templates={len(bootstrap.heartbeat_payloads)}")

    ws = websocket.create_connection(
        bootstrap.ws_url,
        header=headers,
        timeout=timeout_sec,
        enable_multithread=True,
    )
    ws.settimeout(1.0)
    try:
        ws.send_binary(bootstrap.auth_send_payload)
        emit("[send] auth frame sent")

        recv_count = 0
        hb_send_count = 0
        watch_hit_count = 0
        hb_index = 0
        started = time.time()
        next_hb_at = started + heartbeat_interval_sec if heartbeat_interval_sec > 0 else float("inf")

        def _send_heartbeat(now: float) -> None:
            nonlocal hb_send_count, hb_index, next_hb_at
            if heartbeat_interval_sec <= 0:
                return
            if now < next_hb_at:
                return
            if not bootstrap.heartbeat_payloads:
                next_hb_at = now + heartbeat_interval_sec
                return
            payload = bootstrap.heartbeat_payloads[hb_index % len(bootstrap.heartbeat_payloads)]
            hb_index += 1
            ws.send_binary(payload)
            hb_send_count += 1
            emit(
                f"[send-hb#{hb_send_count}] len={len(payload)} "
                f"head={_preview_hex(payload)}"
            )
            next_hb_at = now + heartbeat_interval_sec

        while not stop["value"]:
            now = time.time()
            if recv_limit > 0 and recv_count >= recv_limit:
                break
            if now - started > timeout_sec:
                break
            _send_heartbeat(now)
            try:
                frame = ws.recv()
            except websocket.WebSocketTimeoutException:
                _send_heartbeat(time.time())
                continue
            except Exception as ex:
                emit(f"[recv-error] {ex}")
                break

            recv_count += 1
            if isinstance(frame, bytes):
                emit(
                    f"[recv#{recv_count}] type=binary len={len(frame)} "
                    f"head={_preview_hex(frame)}"
                )
                hits = _find_watch_hits(frame, watch_texts)
                if hits:
                    watch_hit_count += len(hits)
                    for h in hits:
                        emit(f"[watch-hit#{watch_hit_count}] recv#{recv_count} {h}")
                if dump_text_hints:
                    hints = _extract_text_hints(frame, min_text_len)
                    for h in hints:
                        emit(f"[text-hint] recv#{recv_count} {h}")
            else:
                text = str(frame)
                text = text if len(text) <= 160 else text[:157] + "..."
                emit(f"[recv#{recv_count}] type=text len={len(str(frame))} data={text}")

        emit(f"done: recv_count={recv_count}, hb_send_count={hb_send_count}, watch_hit_count={watch_hit_count}")
        return 0
    finally:
        try:
            ws.close()
        except Exception:
            pass


def run_probe_all(
    bootstraps: list[WsBootstrap],
    parallel_workers: int,
    recv_limit: int,
    timeout_sec: float,
    heartbeat_interval_sec: float,
    watch_texts: list[str],
    min_text_len: int,
    dump_text_hints: bool,
) -> int:
    """Runs multiple ws probes in parallel and prints per-session logs."""
    if not bootstraps:
        print("no bootstrap sessions to probe", file=sys.stderr)
        return 2

    max_workers = max(1, min(parallel_workers, len(bootstraps)))
    print(f"[probe] sessions={len(bootstraps)} parallel={max_workers}")
    failures = 0

    with ThreadPoolExecutor(max_workers=max_workers) as ex:
        futures = []
        for i, bs in enumerate(bootstraps, start=1):
            tag = f"[S{i:02d}]"
            futures.append(
                ex.submit(
                    run_live_client,
                    bs,
                    recv_limit,
                    timeout_sec,
                    heartbeat_interval_sec,
                    watch_texts,
                    min_text_len,
                    dump_text_hints,
                    tag,
                )
            )
        for fut in as_completed(futures):
            try:
                code = fut.result()
                if code != 0:
                    failures += 1
            except Exception as exx:
                failures += 1
                print(f"[probe-error] {exx}")

    print(f"[probe-done] failures={failures}/{len(bootstraps)}")
    return 0 if failures == 0 else 1


def main(argv: list[str]) -> int:
    """CLI entrypoint."""
    parser = argparse.ArgumentParser(description="Connect Kuaishou ws and send first auth frame from HAR.")
    parser.add_argument("--har", required=True, help="HAR-like json file path")
    parser.add_argument("--recv-limit", type=int, default=20, help="Max frames to print, 0 for unlimited")
    parser.add_argument("--timeout-sec", type=float, default=60.0, help="Connection/read timeout in seconds")
    parser.add_argument(
        "--heartbeat-interval-sec",
        type=float,
        default=20.0,
        help="Heartbeat send interval seconds, set 0 to disable",
    )
    parser.add_argument(
        "--watch-text",
        action="append",
        default=[],
        help="Watch keyword in recv payload (can be used multiple times)",
    )
    parser.add_argument(
        "--min-text-len",
        type=int,
        default=6,
        help="Min readable text length for --dump-text-hints",
    )
    parser.add_argument(
        "--dump-text-hints",
        action="store_true",
        help="Print readable text snippets extracted from binary frames",
    )
    parser.add_argument(
        "--probe-all",
        action="store_true",
        help="Probe multiple ws sessions extracted from HAR in parallel",
    )
    parser.add_argument(
        "--max-sessions",
        type=int,
        default=6,
        help="Max bootstrap sessions to load from HAR for --probe-all",
    )
    parser.add_argument(
        "--parallel-workers",
        type=int,
        default=3,
        help="Parallel workers for --probe-all",
    )
    parser.add_argument(
        "--offline-chat-scan",
        action="store_true",
        help="Parse ws receive frames from HAR and extract Chinese chat-like text",
    )
    parser.add_argument(
        "--offline-max-messages",
        type=int,
        default=500,
        help="Max receive messages to scan in --offline-chat-scan",
    )
    parser.add_argument(
        "--offline-max-hits",
        type=int,
        default=80,
        help="Max hit rows to print in --offline-chat-scan",
    )
    args = parser.parse_args(argv)

    har_path = Path(args.har).expanduser().resolve()
    if not har_path.exists():
        print(f"file not found: {har_path}", file=sys.stderr)
        return 2

    if args.offline_chat_scan:
        return run_offline_chat_scan(
            har_path=har_path,
            max_messages=args.offline_max_messages,
            max_hits=args.offline_max_hits,
        )

    if args.probe_all:
        try:
            bootstraps = load_bootstraps_from_har(har_path, max_sessions=args.max_sessions)
        except Exception as ex:
            print(f"bootstrap parse failed: {ex}", file=sys.stderr)
            return 2
        return run_probe_all(
            bootstraps=bootstraps,
            parallel_workers=args.parallel_workers,
            recv_limit=args.recv_limit,
            timeout_sec=args.timeout_sec,
            heartbeat_interval_sec=args.heartbeat_interval_sec,
            watch_texts=args.watch_text,
            min_text_len=args.min_text_len,
            dump_text_hints=args.dump_text_hints,
        )

    try:
        bootstrap = load_bootstrap_from_har(har_path)
    except Exception as ex:
        print(f"bootstrap parse failed: {ex}", file=sys.stderr)
        return 2

    return run_live_client(
        bootstrap=bootstrap,
        recv_limit=args.recv_limit,
        timeout_sec=args.timeout_sec,
        heartbeat_interval_sec=args.heartbeat_interval_sec,
        watch_texts=args.watch_text,
        min_text_len=args.min_text_len,
        dump_text_hints=args.dump_text_hints,
    )


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
