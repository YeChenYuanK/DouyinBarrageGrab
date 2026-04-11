#!/usr/bin/env python3
import datetime
import frida
import os
import sys
import time


JS_SOURCE = r"""
const kw = /(live|room|stream|push|author|token|gift|kuaishou|gifshow|apijs|wsukwai)/i;

function now() {
  return (new Date()).toISOString();
}

function s(v) {
  try { return (v || "").toString(); } catch (_) { return ""; }
}

function readW(ptrVal) {
  try {
    if (ptrVal.isNull()) return "";
    return Memory.readUtf16String(ptrVal) || "";
  } catch (_) { return ""; }
}

function readA(ptrVal) {
  try {
    if (ptrVal.isNull()) return "";
    return Memory.readCString(ptrVal) || "";
  } catch (_) { return ""; }
}

function readBytes(ptrVal, n) {
  try {
    if (ptrVal.isNull() || n <= 0) return "";
    const max = Math.min(n, 2048);
    const ba = Memory.readByteArray(ptrVal, max);
    if (!ba) return "";
    const u8 = new Uint8Array(ba);
    let out = "";
    for (let i = 0; i < u8.length; i++) {
      const c = u8[i];
      if (c >= 32 && c <= 126) out += String.fromCharCode(c);
      else if (c === 10 || c === 13 || c === 9) out += " ";
      else out += ".";
    }
    return out;
  } catch (_) { return ""; }
}

function hit(text) {
  const t = s(text);
  return kw.test(t);
}

function emit(kind, data) {
  send({ ts: now(), kind: kind, data: data });
}

const connMap = {};
const reqMap = {};

function hookWinHttp() {
  const dll = "winhttp.dll";
  const pConnect = Module.findExportByName(dll, "WinHttpConnect");
  const pOpenReq = Module.findExportByName(dll, "WinHttpOpenRequest");
  const pSendReq = Module.findExportByName(dll, "WinHttpSendRequest");
  const pWrite = Module.findExportByName(dll, "WinHttpWriteData");
  const pRead = Module.findExportByName(dll, "WinHttpReadData");

  if (pConnect) {
    Interceptor.attach(pConnect, {
      onEnter(args) {
        this.host = readW(args[1]);
        this.port = args[2].toInt32();
      },
      onLeave(retval) {
        const h = retval.toString();
        connMap[h] = { host: this.host, port: this.port };
        if (hit(this.host)) emit("WinHttpConnect", `host=${this.host}:${this.port}`);
      }
    });
  }

  if (pOpenReq) {
    Interceptor.attach(pOpenReq, {
      onEnter(args) {
        this.hConnect = args[0].toString();
        this.method = readW(args[1]);
        this.path = readW(args[2]);
      },
      onLeave(retval) {
        const hReq = retval.toString();
        const c = connMap[this.hConnect] || {};
        reqMap[hReq] = {
          host: c.host || "",
          port: c.port || 0,
          method: this.method || "",
          path: this.path || ""
        };
        const txt = `${this.method} https://${c.host || "?"}${this.path || ""}`;
        if (hit(txt)) emit("WinHttpOpenRequest", txt);
      }
    });
  }

  if (pSendReq) {
    Interceptor.attach(pSendReq, {
      onEnter(args) {
        const hReq = args[0].toString();
        const m = reqMap[hReq] || {};
        const opt = args[3];
        const optLen = args[4].toInt32();
        const body = readBytes(opt, optLen);
        this.msg = `${m.method || "?"} https://${m.host || "?"}${m.path || ""} body=${body}`;
        if (hit(this.msg)) emit("WinHttpSendRequest", this.msg);
      }
    });
  }

  if (pWrite) {
    Interceptor.attach(pWrite, {
      onEnter(args) {
        const hReq = args[0].toString();
        const m = reqMap[hReq] || {};
        const n = args[2].toInt32();
        const body = readBytes(args[1], n);
        const msg = `${m.method || "?"} https://${m.host || "?"}${m.path || ""} chunk=${body}`;
        if (hit(msg)) emit("WinHttpWriteData", msg);
      }
    });
  }

  if (pRead) {
    Interceptor.attach(pRead, {
      onEnter(args) {
        this.hReq = args[0].toString();
        this.buf = args[1];
        this.sz = args[2].toInt32();
        this.pRead = args[3];
      },
      onLeave(retval) {
        try {
          if (retval.toInt32() === 0) return;
          let n = 0;
          if (!this.pRead.isNull()) n = Memory.readU32(this.pRead);
          if (n <= 0) return;
          const m = reqMap[this.hReq] || {};
          const txt = readBytes(this.buf, Math.min(n, this.sz));
          const msg = `${m.method || "?"} https://${m.host || "?"}${m.path || ""} resp=${txt}`;
          if (hit(msg)) emit("WinHttpReadData", msg);
        } catch (_) {}
      }
    });
  }
}

function hookWinInet() {
  const dll = "wininet.dll";
  const pOpenReqW = Module.findExportByName(dll, "HttpOpenRequestW");
  const pSendReqW = Module.findExportByName(dll, "HttpSendRequestW");
  const pReadFile = Module.findExportByName(dll, "InternetReadFile");

  if (pOpenReqW) {
    Interceptor.attach(pOpenReqW, {
      onEnter(args) {
        this.method = readW(args[1]);
        this.path = readW(args[2]);
        this.ver = readW(args[3]);
      },
      onLeave(retval) {
        const hReq = retval.toString();
        reqMap[hReq] = { host: "", method: this.method || "", path: this.path || "" };
        const msg = `${this.method || "?"} ${this.path || ""} ${this.ver || ""}`;
        if (hit(msg)) emit("HttpOpenRequestW", msg);
      }
    });
  }

  if (pSendReqW) {
    Interceptor.attach(pSendReqW, {
      onEnter(args) {
        const hReq = args[0].toString();
        const m = reqMap[hReq] || {};
        const hdr = readW(args[1]);
        const body = readBytes(args[3], args[4].toInt32());
        const msg = `${m.method || "?"} ${m.path || ""} hdr=${hdr} body=${body}`;
        if (hit(msg)) emit("HttpSendRequestW", msg);
      }
    });
  }

  if (pReadFile) {
    Interceptor.attach(pReadFile, {
      onEnter(args) {
        this.hReq = args[0].toString();
        this.buf = args[1];
        this.sz = args[2].toInt32();
        this.pRead = args[3];
      },
      onLeave(retval) {
        try {
          if (retval.toInt32() === 0) return;
          const n = this.pRead.isNull() ? 0 : Memory.readU32(this.pRead);
          if (n <= 0) return;
          const m = reqMap[this.hReq] || {};
          const txt = readBytes(this.buf, Math.min(n, this.sz));
          const msg = `${m.method || "?"} ${m.path || ""} resp=${txt}`;
          if (hit(msg)) emit("InternetReadFile", msg);
        } catch (_) {}
      }
    });
  }
}

setImmediate(function() {
  try { hookWinHttp(); } catch (e) { emit("ERR", "hookWinHttp " + e); }
  try { hookWinInet(); } catch (e) { emit("ERR", "hookWinInet " + e); }
  emit("READY", "http probe ready");
});
"""


def log_line(fp, text):
    ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]
    line = f"{ts} {text}"
    print(line, flush=True)
    fp.write(line + "\n")
    fp.flush()


def main():
    if len(sys.argv) < 3:
        print("usage: frida_kwai_http_probe.py <output_log_path> <duration_seconds>")
        sys.exit(2)

    out_path = sys.argv[1]
    duration = int(sys.argv[2])
    os.makedirs(os.path.dirname(out_path), exist_ok=True)

    dev = frida.get_local_device()
    procs = [p for p in dev.enumerate_processes() if p.name.lower() == "kwailive.exe"]
    if not procs:
        print("no kwailive.exe process found")
        sys.exit(3)

    sessions = []
    scripts = []
    with open(out_path, "a", encoding="utf-8") as fp:
        log_line(fp, f"[FRIDA] attach_count={len(procs)} pids={[p.pid for p in procs]}")
        for p in procs:
            try:
                sess = dev.attach(p.pid)
                scr = sess.create_script(JS_SOURCE)

                def on_message(msg, data, pid=p.pid):
                    if msg.get("type") == "send":
                        payload = msg.get("payload", {})
                        kind = payload.get("kind", "MSG")
                        dt = payload.get("data", "")
                        log_line(fp, f"[PID {pid}] [{kind}] {dt}")
                    elif msg.get("type") == "error":
                        log_line(fp, f"[PID {pid}] [SCRIPT_ERROR] {msg}")

                scr.on("message", on_message)
                scr.load()
                sessions.append(sess)
                scripts.append(scr)
                log_line(fp, f"[FRIDA] attached pid={p.pid}")
            except Exception as ex:
                log_line(fp, f"[FRIDA] attach failed pid={p.pid} err={ex}")

        start = time.time()
        while time.time() - start < duration:
            time.sleep(0.2)

        for s in sessions:
            try:
                s.detach()
            except Exception:
                pass
        log_line(fp, "[FRIDA] done")


if __name__ == "__main__":
    main()
