#!/usr/bin/env python3
import datetime
import frida
import os
import re
import subprocess
import sys
import time


JS_SOURCE = r"""
const kw = /(live|room|stream|push|author|token|gift|douyin|webcast|zijie|bytedance|amemv)/i;
function now(){ return (new Date()).toISOString(); }
function s(v){ try { return (v || "").toString(); } catch (_) { return ""; } }
function readW(p){ try { if (p.isNull()) return ""; return Memory.readUtf16String(p) || ""; } catch (_) { return ""; } }
function readBytes(p, n){
  try {
    if (p.isNull() || n <= 0) return "";
    const max = Math.min(n, 2048);
    const ba = Memory.readByteArray(p, max);
    if (!ba) return "";
    const u8 = new Uint8Array(ba);
    let out = "";
    for (let i = 0; i < u8.length; i++) {
      const c = u8[i];
      if (c >= 32 && c <= 126) out += String.fromCharCode(c); else out += ".";
    }
    return out;
  } catch (_) { return ""; }
}
function hit(t){ return kw.test(s(t)); }
function emit(kind, data){ send({ts: now(), kind: kind, data: data}); }
function findExp(mod, name){
  try { return Module.getExportByName(mod, name); } catch (_) {}
  try { return Module.findExportByName(mod, name); } catch (_) {}
  return null;
}

const connMap = {};
const reqMap = {};

function hookWinHttp(){
  const pConnect = findExp("winhttp.dll", "WinHttpConnect");
  const pOpenReq = findExp("winhttp.dll", "WinHttpOpenRequest");
  const pSendReq = findExp("winhttp.dll", "WinHttpSendRequest");
  const pWrite = findExp("winhttp.dll", "WinHttpWriteData");
  if (pConnect) {
    Interceptor.attach(pConnect, {
      onEnter(args){ this.host = readW(args[1]); this.port = args[2].toInt32(); },
      onLeave(retval){
        connMap[retval.toString()] = {host: this.host, port: this.port};
        if (hit(this.host)) emit("WinHttpConnect", `host=${this.host}:${this.port}`);
      }
    });
  }
  if (pOpenReq) {
    Interceptor.attach(pOpenReq, {
      onEnter(args){ this.hc=args[0].toString(); this.m=readW(args[1]); this.p=readW(args[2]); },
      onLeave(retval){
        const c = connMap[this.hc] || {};
        reqMap[retval.toString()] = {host: c.host || "", method: this.m || "", path: this.p || ""};
        const msg = `${this.m} https://${c.host || "?"}${this.p || ""}`;
        if (hit(msg)) emit("WinHttpOpenRequest", msg);
      }
    });
  }
  if (pSendReq) {
    Interceptor.attach(pSendReq, {
      onEnter(args){
        const r=reqMap[args[0].toString()] || {};
        const body = readBytes(args[3], args[4].toInt32());
        const msg = `${r.method || "?"} https://${r.host || "?"}${r.path || ""} body=${body}`;
        if (hit(msg)) emit("WinHttpSendRequest", msg);
      }
    });
  }
  if (pWrite) {
    Interceptor.attach(pWrite, {
      onEnter(args){
        const r=reqMap[args[0].toString()] || {};
        const body = readBytes(args[1], args[2].toInt32());
        const msg = `${r.method || "?"} https://${r.host || "?"}${r.path || ""} chunk=${body}`;
        if (hit(msg)) emit("WinHttpWriteData", msg);
      }
    });
  }
}

setImmediate(function(){
  try { hookWinHttp(); emit("READY","webcast http probe ready"); }
  catch (e) { emit("ERR", e.toString()); }
});
"""


def now():
    return datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]


def wlog(fp, text):
    line = f"{now()} {text}"
    print(line, flush=True)
    fp.write(line + "\n")
    fp.flush()


def find_pids_by_path_keyword(keyword: str):
    cmd = [
        "wmic",
        "process",
        "where",
        f"ExecutablePath like '%{keyword}%'",
        "get",
        "ProcessId",
        "/value",
    ]
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="ignore")
    if p.returncode != 0:
        return []
    pids = []
    for m in re.finditer(r"ProcessId=(\d+)", p.stdout):
        try:
            pids.append(int(m.group(1)))
        except Exception:
            pass
    return sorted(set(pid for pid in pids if pid > 0))


def main():
    if len(sys.argv) < 4:
        print("usage: frida_webcast_http_probe.py <output_log_path> <duration_seconds> <path_keyword>")
        sys.exit(2)

    out_path = sys.argv[1]
    duration = int(sys.argv[2])
    keyword = sys.argv[3]
    os.makedirs(os.path.dirname(out_path), exist_ok=True)

    pids = find_pids_by_path_keyword(keyword)
    if not pids:
        print(f"no process found by path keyword: {keyword}")
        sys.exit(3)

    dev = frida.get_local_device()
    with open(out_path, "a", encoding="utf-8") as fp:
        wlog(fp, f"[FRIDA] keyword={keyword} attach_count={len(pids)} pids={pids}")
        sessions = []
        for pid in pids:
            try:
                s = dev.attach(pid)
                sc = s.create_script(JS_SOURCE)

                def on_message(msg, data, pid=pid):
                    if msg.get("type") == "send":
                        pl = msg.get("payload", {})
                        wlog(fp, f"[PID {pid}] [{pl.get('kind','MSG')}] {pl.get('data','')}")
                    elif msg.get("type") == "error":
                        wlog(fp, f"[PID {pid}] [SCRIPT_ERROR] {msg}")

                sc.on("message", on_message)
                sc.load()
                sessions.append(s)
                wlog(fp, f"[FRIDA] attached pid={pid}")
            except Exception as ex:
                wlog(fp, f"[FRIDA] attach failed pid={pid} err={ex}")

        t0 = time.time()
        while time.time() - t0 < duration:
            time.sleep(0.2)
        for s in sessions:
            try:
                s.detach()
            except Exception:
                pass
        wlog(fp, "[FRIDA] done")


if __name__ == "__main__":
    main()
