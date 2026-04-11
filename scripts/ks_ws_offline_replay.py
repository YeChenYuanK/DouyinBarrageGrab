#!/usr/bin/env python3
"""Offline replay for Kuaishou WebSocket HAR/json samples.

This script scans HAR-like files that contain `_webSocketMessages`,
decodes opcode=2 binary frames from base64, and tries to extract
runtime auth params using protobuf field-level parsing:
1) direct KsAuthRequest decode
2) KsSocketMessage envelope decode -> payloadType=200/AUTH -> KsAuthRequest
"""

from __future__ import annotations

import argparse
import base64
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Tuple


@dataclass
class AuthHit:
    """Represents one parsed auth hit."""

    file_path: str
    message_index: int
    mode: str
    payload_type: str
    live_stream_id: str
    token_len: int
    page_id: str


@dataclass
class ParseResult:
    """Aggregate parse counters and auth hits."""

    total_files: int = 0
    total_messages: int = 0
    opcode2_messages: int = 0
    decoded_messages: int = 0
    decode_failures: int = 0
    auth_hits: List[AuthHit] = None
    token_candidates: Dict[str, List["TokenCandidate"]] = None

    def __post_init__(self) -> None:
        if self.auth_hits is None:
            self.auth_hits = []
        if self.token_candidates is None:
            self.token_candidates = {}


@dataclass
class TokenCandidate:
    """Represents one token-like candidate from offline reverse scan."""

    file_path: str
    message_index: int
    source: str
    value: str
    length: int
    score: int


def _read_varint(data: bytes, pos: int) -> Tuple[Optional[int], int]:
    """Reads a protobuf varint from data[pos:]."""
    shift = 0
    value = 0
    while pos < len(data) and shift <= 63:
        b = data[pos]
        pos += 1
        value |= (b & 0x7F) << shift
        if (b & 0x80) == 0:
            return value, pos
        shift += 7
    return None, pos


def _parse_len_delimited_fields(data: bytes) -> Dict[int, List[bytes]]:
    """Parses protobuf bytes and returns all length-delimited fields by number."""
    out: Dict[int, List[bytes]] = {}
    pos = 0
    size = len(data)
    while pos < size:
        key, pos2 = _read_varint(data, pos)
        if key is None:
            break
        pos = pos2
        field_no = key >> 3
        wire_type = key & 0x07

        if wire_type == 0:  # varint
            _, pos = _read_varint(data, pos)
            if pos > size:
                break
            continue
        if wire_type == 1:  # 64-bit
            pos += 8
            if pos > size:
                break
            continue
        if wire_type == 2:  # length-delimited
            length, pos = _read_varint(data, pos)
            if length is None or pos + length > size:
                break
            chunk = data[pos : pos + length]
            pos += length
            out.setdefault(field_no, []).append(chunk)
            continue
        if wire_type == 5:  # 32-bit
            pos += 4
            if pos > size:
                break
            continue

        # Unsupported/invalid wire type
        break
    return out


def _first_utf8(fields: Dict[int, List[bytes]], field_no: int) -> str:
    """Decodes first occurrence of a length-delimited field as UTF-8 string."""
    chunks = fields.get(field_no)
    if not chunks:
        return ""
    for chunk in chunks:
        try:
            text = chunk.decode("utf-8", errors="strict").strip()
            if text:
                return text
        except UnicodeDecodeError:
            continue
    return ""


def _parse_auth_request(payload: bytes) -> Optional[Dict[str, str]]:
    """Parses KsAuthRequest fields from protobuf bytes."""
    fields = _parse_len_delimited_fields(payload)
    live_stream_id = _first_utf8(fields, 3)
    if not live_stream_id:
        return None
    return {
        "kpn": _first_utf8(fields, 1),
        "kpf": _first_utf8(fields, 2),
        "live_stream_id": live_stream_id,
        "token": _first_utf8(fields, 4),
        "page_id": _first_utf8(fields, 5),
    }


def _looks_like_live_stream_id(value: str) -> bool:
    """Checks whether value looks like a real liveStreamId."""
    if not value:
        return False
    return re.fullmatch(r"[A-Za-z0-9_-]{8,128}", value) is not None


def _looks_like_token(value: str) -> bool:
    """Checks whether value looks like a ws auth token."""
    if not value:
        return False
    # Token is usually base64/base64url-like and fairly long.
    return re.fullmatch(r"[A-Za-z0-9._%+/=-]{16,1024}", value) is not None


def _is_valid_auth(auth: Optional[Dict[str, str]]) -> bool:
    """Validates parsed auth payload to reduce false positives."""
    if not auth:
        return False
    kpn = auth.get("kpn", "")
    kpf = auth.get("kpf", "")
    live_stream_id = auth.get("live_stream_id", "")
    token = auth.get("token", "")

    # Strong signals first: known fixed fields + expected id/token shape.
    if kpn == "LIVE_STREAM" and kpf == "WEB":
        return _looks_like_live_stream_id(live_stream_id) and _looks_like_token(token)

    # Fallback: allow if id+token both strongly match shape.
    return _looks_like_live_stream_id(live_stream_id) and _looks_like_token(token)


def _parse_envelope(payload: bytes) -> Optional[Dict[str, object]]:
    """Parses KsSocketMessage fields from protobuf bytes."""
    fields = _parse_len_delimited_fields(payload)
    payload_type = _first_utf8(fields, 2)
    payload_bytes = fields.get(3, [b""])[0]
    if not payload_type and not payload_bytes:
        return None
    return {
        "payload_type": payload_type,
        "payload": payload_bytes,
    }


def _score_candidate(value: str, source: str) -> int:
    """Scores candidate confidence using lightweight heuristics."""
    score = 0
    n = len(value)
    if n >= 64:
        score += 4
    elif n >= 32:
        score += 3
    elif n >= 16:
        score += 1

    if "=" in value or "/" in value or "+" in value:
        score += 2
    if "." in value or "-" in value or "_" in value:
        score += 1
    if source.startswith("kv."):
        score += 4
    if "payload" in source:
        score += 1
    return score


def _collect_token_candidates_from_bytes(
    data: bytes,
    source: str,
    depth: int,
    max_depth: int,
    out: Dict[str, TokenCandidate],
    file_path: str,
    message_index: int,
) -> None:
    """Extracts token-like candidates from protobuf-ish bytes recursively."""
    if not data or depth > max_depth:
        return

    text = data.decode("utf-8", errors="ignore")
    if text:
        # Strong key-value candidates
        for m in re.finditer(
            r"(websocketToken|wsToken|accessToken|serviceToken|token)\s*[:=]\s*[\"']?([A-Za-z0-9._%+/=-]{16,1024})",
            text,
            flags=re.IGNORECASE,
        ):
            value = m.group(2).strip()
            score = _score_candidate(value, "kv.text")
            out[value] = TokenCandidate(
                file_path=file_path,
                message_index=message_index,
                source="kv.text",
                value=value,
                length=len(value),
                score=score,
            )

        # Generic base64/base64url-like candidates
        for m in re.finditer(r"[A-Za-z0-9._%+/=-]{24,1024}", text):
            value = m.group(0).strip()
            if not _looks_like_token(value):
                continue
            score = _score_candidate(value, f"regex.d{depth}.{source}")
            exist = out.get(value)
            if exist is None or score > exist.score:
                out[value] = TokenCandidate(
                    file_path=file_path,
                    message_index=message_index,
                    source=f"regex.d{depth}.{source}",
                    value=value,
                    length=len(value),
                    score=score,
                )

    fields = _parse_len_delimited_fields(data)
    for field_no, chunks in fields.items():
        for idx, chunk in enumerate(chunks):
            sub_source = f"{source}.f{field_no}[{idx}]"
            _collect_token_candidates_from_bytes(
                chunk,
                sub_source,
                depth + 1,
                max_depth,
                out,
                file_path,
                message_index,
            )


def _scan_first_send_candidates(file_path: Path) -> List[TokenCandidate]:
    """Scans first send opcode=2 frame for token-like fields."""
    for message_index, msg in enumerate(_load_ws_messages(file_path)):
        if msg.get("type") != "send" or msg.get("opcode") != 2:
            continue
        data = _try_decode_base64(msg.get("data", ""))
        if data is None:
            return []

        hit_map: Dict[str, TokenCandidate] = {}

        # Layer 1: raw frame bytes
        _collect_token_candidates_from_bytes(
            data=data,
            source="frame",
            depth=0,
            max_depth=2,
            out=hit_map,
            file_path=str(file_path),
            message_index=message_index,
        )

        # Layer 2: envelope payload bytes
        env = _parse_envelope(data)
        if env and isinstance(env.get("payload"), bytes):
            _collect_token_candidates_from_bytes(
                data=env["payload"],
                source=f"payload.{str(env.get('payload_type', '')).strip() or 'unknown'}",
                depth=0,
                max_depth=2,
                out=hit_map,
                file_path=str(file_path),
                message_index=message_index,
            )

        return sorted(hit_map.values(), key=lambda x: (-x.score, -x.length, x.source))
    return []


def _load_ws_messages(file_path: Path) -> Iterable[dict]:
    """Loads _webSocketMessages from HAR-like file."""
    try:
        obj = json.loads(file_path.read_text(encoding="utf-8"))
    except Exception:
        # Fallback for potential BOM/mixed encoding
        obj = json.loads(file_path.read_text(encoding="utf-8-sig"))

    entries = (
        obj.get("log", {}).get("entries", [])
        if isinstance(obj, dict)
        else []
    )
    for entry in entries:
        for msg in entry.get("_webSocketMessages", []) or []:
            if isinstance(msg, dict):
                yield msg


def _try_decode_base64(raw: str) -> Optional[bytes]:
    """Decodes base64 frame data."""
    if not raw or not isinstance(raw, str):
        return None
    try:
        return base64.b64decode(raw, validate=True)
    except Exception:
        # Some exports omit padding
        try:
            return base64.b64decode(raw + "===")
        except Exception:
            return None


def run_scan(sample_dir: Path) -> ParseResult:
    """Scans all json files under sample_dir and extracts auth hits."""
    result = ParseResult()
    files = sorted(sample_dir.glob("*.json"))
    result.total_files = len(files)

    for file_path in files:
        result.token_candidates[str(file_path)] = _scan_first_send_candidates(file_path)[:8]
        message_index = 0
        for msg in _load_ws_messages(file_path):
            result.total_messages += 1
            opcode = msg.get("opcode")
            if opcode != 2:
                message_index += 1
                continue

            result.opcode2_messages += 1
            data = _try_decode_base64(msg.get("data", ""))
            if data is None:
                result.decode_failures += 1
                message_index += 1
                continue

            result.decoded_messages += 1

            # Mode A: direct auth decode
            auth = _parse_auth_request(data)
            if _is_valid_auth(auth):
                result.auth_hits.append(
                    AuthHit(
                        file_path=str(file_path),
                        message_index=message_index,
                        mode="direct_auth",
                        payload_type="",
                        live_stream_id=auth["live_stream_id"],
                        token_len=len(auth["token"]),
                        page_id=auth["page_id"],
                    )
                )
                message_index += 1
                continue

            # Mode B: envelope -> auth payload
            env = _parse_envelope(data)
            if env:
                payload_type = str(env.get("payload_type", "")).strip()
                payload = env.get("payload", b"")
                if isinstance(payload, bytes) and (
                    payload_type == "200" or payload_type.upper() == "AUTH"
                ):
                    auth2 = _parse_auth_request(payload)
                    if _is_valid_auth(auth2):
                        result.auth_hits.append(
                            AuthHit(
                                file_path=str(file_path),
                                message_index=message_index,
                                mode="envelope_auth",
                                payload_type=payload_type,
                                live_stream_id=auth2["live_stream_id"],
                                token_len=len(auth2["token"]),
                                page_id=auth2["page_id"],
                            )
                        )

            message_index += 1

    return result


def print_report(result: ParseResult) -> None:
    """Prints human-readable report."""
    print("=== Kuaishou WS Offline Replay Report ===")
    print(f"files={result.total_files}")
    print(f"messages_total={result.total_messages}")
    print(f"messages_opcode2={result.opcode2_messages}")
    print(f"messages_decoded={result.decoded_messages}")
    print(f"decode_failures={result.decode_failures}")
    print(f"auth_hits={len(result.auth_hits)}")

    token_pos = sum(1 for h in result.auth_hits if h.token_len > 0)
    print(f"auth_token_positive={token_pos}")

    if result.auth_hits:
        print("--- auth hit details ---")
        for hit in result.auth_hits:
            print(
                f"{Path(hit.file_path).name}"
                f" msg#{hit.message_index}"
                f" mode={hit.mode}"
                f" payloadType={hit.payload_type or '-'}"
                f" liveStreamId={hit.live_stream_id}"
                f" tokenLen={hit.token_len}"
                f" pageId={hit.page_id or '-'}"
            )

    print("--- first send token candidates ---")
    for file_path, candidates in result.token_candidates.items():
        name = Path(file_path).name
        if not candidates:
            print(f"{name}: none")
            continue
        print(f"{name}:")
        for c in candidates:
            preview = c.value if len(c.value) <= 72 else (c.value[:36] + "..." + c.value[-24:])
            print(
                f"  msg#{c.message_index} score={c.score} len={c.length} "
                f"source={c.source} value={preview}"
            )


def main(argv: List[str]) -> int:
    """CLI entrypoint."""
    parser = argparse.ArgumentParser(description="Replay Kuaishou WS HAR/json samples.")
    parser.add_argument(
        "--sample-dir",
        required=True,
        help="Directory containing HAR-like json files with _webSocketMessages.",
    )
    args = parser.parse_args(argv)

    sample_dir = Path(args.sample_dir).expanduser().resolve()
    if not sample_dir.exists() or not sample_dir.is_dir():
        print(f"invalid sample dir: {sample_dir}", file=sys.stderr)
        return 2

    result = run_scan(sample_dir)
    print_report(result)
    return 0 if result.total_files > 0 else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
