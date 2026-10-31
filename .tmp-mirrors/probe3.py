# -*- coding: utf-8 -*-
"""逐个服务独立测试，单个失败不影响其余。带独立进程级超时。"""
import os, sys, json, gzip, io, zipfile, subprocess, time

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "results")
TMP = os.path.join(HERE, "dl")
os.makedirs(OUT, exist_ok=True)
os.makedirs(TMP, exist_ok=True)

SMALL = "https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-v3.8.1.tar.gz"
SMALL_SIZE = 763252
MID = "https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-windows-amd64-v3.8.1.zip"
MID_SIZE = 5870805
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36")

PROXIES = [
    ("gh-proxy.com", "https://gh-proxy.com/"),
    ("ghfast.top", "https://ghfast.top/"),
    ("mirror.ghproxy.com", "https://mirror.ghproxy.com/"),
    ("hub.gitmirror.com", "https://hub.gitmirror.com/"),
    ("gh.api.99988866.xyz", "https://gh.api.99988866.xyz/"),
    ("ghproxy.net", "https://ghproxy.net/"),
    ("ghps.cc", "https://ghps.cc/"),
    ("gh.ddlc.top", "https://gh.ddlc.top/"),
]


def sniff(head):
    if not head:
        return "EMPTY", "0 字节"
    low = head[:600].lower()
    if head[:4] == b"PK\x03\x04":
        return "ZIP", "zip 魔数正确"
    if head[:2] == b"\x1f\x8b":
        return "GZIP", "gzip 魔数正确"
    if b"<!doctype html" in low:
        return "HTML", "HTML 错误页伪装成文件"
    if b"<html" in low:
        return "HTML", "HTML 页面"
    if low[:1] in (b"{", b"["):
        return "JSON", "JSON 错误信息"
    return "UNKNOWN", "未知类型 %r" % head[:24]


def verify(data, kind):
    try:
        if kind == "GZIP":
            with gzip.GzipFile(fileobj=io.BytesIO(data)) as g:
                g.read(8192)
            return True, "gzip 可正常解压"
        if kind == "ZIP":
            z = zipfile.ZipFile(io.BytesIO(data))
            if z.testzip():
                return False, "zip CRC 损坏"
            return True, "zip 可完整解包(%d 条目)" % len(z.namelist())
    except Exception as e:
        return False, "校验失败: %s" % str(e)[:90]
    return False, ""


def curl(url, timeout=75, follow=True):
    of = os.path.join(TMP, "f_%d.bin" % int(time.time() * 1000 % 1e9))
    hf = of + ".hdr"
    cmd = ["curl", "-s", "-D", hf, "-o", of, "-m", str(timeout), "-A", UA,
           "-H", "Accept-Encoding: identity",
           "-w", "%{http_code}|%{size_download}|%{time_total}|%{speed_download}|"
                 "%{url_effective}|%{content_type}|%{num_redirects}|%{time_starttransfer}"]
    if follow:
        cmd.insert(1, "-L")
    cmd.append(url)
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout + 20)
        raw, stderr, rc = p.stdout.strip(), p.stderr.strip()[:200], p.returncode
    except subprocess.TimeoutExpired:
        raw, stderr, rc = "", "PROCESS_TIMEOUT", -9
    meta = {"curl_exit": rc, "stderr": stderr}
    parts = raw.split("|")
    if len(parts) >= 7:
        meta.update({
            "status": int(parts[0] or 0), "size": int(parts[1] or 0),
            "time": float(parts[2] or 0), "speed_kb": round(float(parts[3] or 0) / 1024, 1),
            "final_url": parts[4], "content_type": parts[5],
            "num_redirects": int(parts[6] or 0),
            "time_starttransfer": float(parts[7] or 0) if len(parts) > 7 else 0,
        })
    else:
        meta.update({"status": 0, "size": 0, "time": 0, "final_url": "",
                     "content_type": "", "num_redirects": 0, "speed_kb": 0,
                     "time_starttransfer": 0})
    headers = ""
    if os.path.exists(hf):
        headers = open(hf, encoding="utf-8", errors="replace").read()
        os.remove(hf)
    return meta, of, headers


def analyze(meta, filepath, expect=None, label=""):
    rec = dict(meta)
    rec["label"] = label
    data = open(filepath, "rb").read() if os.path.exists(filepath) else b""
    try:
        os.remove(filepath)
    except Exception:
        pass
    rec["size"] = len(data)
    kind, detail = sniff(data[:600])
    rec["kind"], rec["kind_detail"] = kind, detail
    if expect:
        rec["expect_size"] = expect
        rec["size_match"] = (len(data) == expect)
    if kind in ("GZIP", "ZIP") and len(data) > 100:
        ok, d2 = verify(data, kind)
        rec["content_valid"], rec["content_detail"] = ok, d2
    else:
        rec["content_valid"] = False
        rec["content_detail"] = detail
    if 0 < len(data) <= 600:
        rec["body_preview"] = data.decode("utf-8", "replace")[:250]
    return rec


def main():
    out = []
    for name, prefix in PROXIES:
        url = prefix + SMALL
        # 先不跟随跳转，看 302 去向
        m, f, h = curl(url, timeout=45, follow=False)
        locs = [l.strip() for l in h.splitlines() if l.lower().startswith("location:")]
        head_rec = {"label": name + "-HEAD", "url": url, "status": m["status"],
                    "location": locs, "curl_exit": m["curl_exit"],
                    "stderr": m.get("stderr", "")[:120]}
        print(json.dumps(head_rec, ensure_ascii=False), flush=True)
        out.append(head_rec)
        # 实际下载（小文件优先，能最快判定死活）
        m, f, h = curl(url, timeout=75)
        r = analyze(m, f, SMALL_SIZE, name + "-SMALL")
        r["redirect_chain"] = [l.strip() for l in h.splitlines()
                               if l.lower().startswith("location:")]
        print(json.dumps(r, ensure_ascii=False), flush=True)
        out.append(r)
        with open(os.path.join(OUT, "proxy_partial.json"), "w", encoding="utf-8") as fh:
            json.dump(out, fh, ensure_ascii=False, indent=2)
    print("DONE", flush=True)


if __name__ == "__main__":
    main()
