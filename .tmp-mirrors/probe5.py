# -*- coding: utf-8 -*-
"""候选镜像服务批量实测 + ghfast.top 稳定性重复测试"""
import os, sys, json, gzip, io, zipfile, subprocess, time
from concurrent.futures import ThreadPoolExecutor

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "results")
TMP = os.path.join(HERE, "dl")
os.makedirs(OUT, exist_ok=True); os.makedirs(TMP, exist_ok=True)
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36")
SMALL = "https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-v3.8.1.tar.gz"
SMALL_SIZE = 763252
MID = "https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-windows-amd64-v3.8.1.zip"
MID_SIZE = 5870805

CANDIDATES = [
    ("gh.llkk.cc", "https://gh.llkk.cc/"),
    ("githubproxy.cc", "https://githubproxy.cc/"),
    ("ghproxy.it", "https://ghproxy.it/"),
    ("githubfast.com", "https://githubfast.com/"),
    ("wget.la", "https://wget.la/"),
    ("gh.moeyy.xyz", "https://gh.moeyy.xyz/"),
    ("ghproxy.com", "https://ghproxy.com/"),
    ("hk.gh-proxy.org", "https://hk.gh-proxy.org/"),
    ("cdn.gh-proxy.org", "https://cdn.gh-proxy.org/"),
    ("gh.catmak.name", "https://gh.catmak.name/"),
    ("g.blfrp.cn", "https://g.blfrp.cn/"),
    ("gh.isteed.cc", "https://gh.isteed.cc/"),
    ("gh.biejieshi.com", "https://gh.biejieshi.com/"),
    ("github.cooluc.com", "https://github.cooluc.com/"),
    ("gh.mili.one", "https://gh.mili.one/"),
    ("github.ur1.fun", "https://github.ur1.fun/"),
    ("githubsg.lilyya.top", "https://githubsg.lilyya.top/"),
    ("github.geekery.cn", "https://github.geekery.cn/"),
    ("github.js.cn", "https://github.js.cn/"),
    ("down.npee.cn", "https://down.npee.cn/"),
    ("sciproxy.com", "https://sciproxy.com/"),
    ("github.abskoop.workers.dev", "https://github.abskoop.workers.dev/"),
    ("github.mspace.workers.dev", "https://github.mspace.workers.dev/"),
    ("github.boyce.workers.dev", "https://github.boyce.workers.dev/"),
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


def run(label, url, expect, timeout=70):
    of = os.path.join(TMP, "c_%d.bin" % int(time.time()*1e6 % 1e9))
    hf = of + ".hdr"
    cmd = ["curl", "-s", "-L", "-D", hf, "-o", of, "-m", str(timeout), "-A", UA,
           "-H", "Accept-Encoding: identity",
           "-w", "%{http_code}|%{size_download}|%{time_total}|%{speed_download}|"
                 "%{url_effective}|%{content_type}|%{num_redirects}"]
    cmd.append(url)
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout+25)
        raw, stderr, rc = p.stdout.strip(), p.stderr.strip()[:180], p.returncode
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
        rec.update({"status": 0, "size": 0, "time": 0, "speed_kb": 0,
                    "final_url": "", "content_type": "", "num_redirects": 0})
    data = open(of, "rb").read() if os.path.exists(of) else b""
    hdr = ""
    if os.path.exists(hf):
        hdr = open(hf, encoding="utf-8", errors="replace").read()
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
    rec["redirect_chain"] = [l.strip()[:120] for l in hdr.splitlines()
                             if l.lower().startswith("location:")]
    if 0 < len(data) <= 600:
        rec["body_preview"] = data.decode("utf-8", "replace")[:200]
    return rec


def one_service(args):
    name, prefix = args
    r = run(name, prefix + SMALL, SMALL_SIZE, timeout=70)
    print(json.dumps(r, ensure_ascii=False), flush=True)
    return r


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "cand"
    out = []
    if mode == "cand":
        with ThreadPoolExecutor(max_workers=6) as ex:
            out = list(ex.map(one_service, CANDIDATES))
        with open(os.path.join(OUT, "candidates.json"), "w", encoding="utf-8") as f:
            json.dump(out, f, ensure_ascii=False, indent=2)
    elif mode == "stability":
        # ghfast.top 稳定性: 小文件 5 次 + 大文件 5 次
        for i in range(5):
            r = run("ghfast-SMALL-run%d" % (i+1), "https://ghfast.top/" + SMALL, SMALL_SIZE, 70)
            print(json.dumps(r, ensure_ascii=False), flush=True); out.append(r)
        for i in range(5):
            r = run("ghfast-MID-run%d" % (i+1), "https://ghfast.top/" + MID, MID_SIZE, 90)
            print(json.dumps(r, ensure_ascii=False), flush=True); out.append(r)
        with open(os.path.join(OUT, "stability.json"), "w", encoding="utf-8") as f:
            json.dump(out, f, ensure_ascii=False, indent=2)
    print("DONE", flush=True)


if __name__ == "__main__":
    main()
