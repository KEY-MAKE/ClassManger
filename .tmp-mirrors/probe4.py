# -*- coding: utf-8 -*-
"""复测: ghfast.top 大文件 / ghproxy.net 长超时 / raw / jsDelivr 大文件"""
import os, sys, json, gzip, io, zipfile, subprocess, time

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "results")
TMP = os.path.join(HERE, "dl")
os.makedirs(OUT, exist_ok=True); os.makedirs(TMP, exist_ok=True)
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36")

CASES = [
    # (label, url, expect_size, timeout)
    ("ghfast.top-MID-5.8MB",
     "https://ghfast.top/https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-windows-amd64-v3.8.1.zip",
     5870805, 180),
    ("ghfast.top-MID-retry2",
     "https://ghfast.top/https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-windows-amd64-v3.8.1.zip",
     5870805, 180),
    ("ghproxy.net-SMALL-longtimeout",
     "https://ghproxy.net/https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-v3.8.1.tar.gz",
     763252, 180),
    ("ghproxy.net-MID-5.8MB",
     "https://ghproxy.net/https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-windows-amd64-v3.8.1.zip",
     5870805, 180),
    ("raw-README-small",
     "https://raw.githubusercontent.com/git-lfs/git-lfs/main/README.md", 10702, 60),
    ("jsdelivr-README",
     "https://cdn.jsdelivr.net/gh/git-lfs/git-lfs@v3.8.1/README.md", 10702, 60),
]


def sniff(head):
    if not head: return "EMPTY", "0 字节"
    low = head[:600].lower()
    if head[:4] == b"PK\x03\x04": return "ZIP", "zip 魔数正确"
    if head[:2] == b"\x1f\x8b": return "GZIP", "gzip 魔数正确"
    if b"<!doctype html" in low: return "HTML", "HTML 错误页伪装成文件"
    if b"<html" in low: return "HTML", "HTML 页面"
    if low[:1] in (b"{", b"["): return "JSON", "JSON 错误信息"
    return "UNKNOWN", "未知类型 %r" % head[:24]


def verify(data, kind):
    try:
        if kind == "GZIP":
            with gzip.GzipFile(fileobj=io.BytesIO(data)) as g: g.read(8192)
            return True, "gzip 可正常解压"
        if kind == "ZIP":
            z = zipfile.ZipFile(io.BytesIO(data))
            if z.testzip(): return False, "zip CRC 损坏"
            return True, "zip 可完整解包(%d 条目)" % len(z.namelist())
    except Exception as e:
        return False, "校验失败: %s" % str(e)[:90]
    return False, ""


def run(label, url, expect, timeout):
    of = os.path.join(TMP, "r_%d.bin" % int(time.time()*1000 % 1e9))
    hf = of + ".hdr"
    cmd = ["curl", "-s", "-L", "-D", hf, "-o", of, "-m", str(timeout), "-A", UA,
           "-H", "Accept-Encoding: identity",
           "-w", "%{http_code}|%{size_download}|%{time_total}|%{speed_download}|"
                 "%{url_effective}|%{content_type}|%{num_redirects}"]
    cmd.append(url)
    t0 = time.time()
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout+25)
        raw, stderr, rc = p.stdout.strip(), p.stderr.strip()[:200], p.returncode
    except subprocess.TimeoutExpired:
        raw, stderr, rc = "", "PROCESS_TIMEOUT", -9
    parts = raw.split("|")
    rec = {"label": label, "url": url, "curl_exit": rc, "stderr": stderr,
           "expect_size": expect, "timeout": timeout}
    if len(parts) >= 6:
        rec.update({"status": int(parts[0] or 0), "size": int(parts[1] or 0),
                    "time": float(parts[2] or 0),
                    "speed_kb": round(float(parts[3] or 0)/1024, 1),
                    "final_url": parts[4], "content_type": parts[5],
                    "num_redirects": int(parts[6] or 0) if len(parts) > 6 else 0})
    else:
        rec.update({"status": 0, "size": 0, "time": round(time.time()-t0, 2),
                    "speed_kb": 0, "final_url": "", "content_type": "",
                    "num_redirects": 0})
    data = open(of, "rb").read() if os.path.exists(of) else b""
    for f in (of, hf):
        try: os.remove(f)
        except Exception: pass
    rec["size"] = len(data)
    rec["size_match"] = (len(data) == expect)
    kind, detail = sniff(data[:600])
    rec["kind"], rec["kind_detail"] = kind, detail
    if kind in ("GZIP", "ZIP") and len(data) > 100:
        ok, d2 = verify(data, kind)
        rec["content_valid"], rec["content_detail"] = ok, d2
    else:
        rec["content_valid"], rec["content_detail"] = False, detail
    if 0 < len(data) <= 600:
        rec["body_preview"] = data.decode("utf-8", "replace")[:250]
    return rec


if __name__ == "__main__":
    out = []
    for label, url, exp, to in CASES:
        r = run(label, url, exp, to)
        print(json.dumps(r, ensure_ascii=False), flush=True)
        out.append(r)
        with open(os.path.join(OUT, "retest.json"), "w", encoding="utf-8") as f:
            json.dump(out, f, ensure_ascii=False, indent=2)
    print("DONE", flush=True)
