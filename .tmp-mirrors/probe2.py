# -*- coding: utf-8 -*-
"""
实测 GitHub Release 附件下载在各「前缀式代理/加速」服务下的可用性。
主测用 curl（跟随跳转、捕获真实字节数与耗时），Python 负责内容真实性校验。

所有请求都走系统环境里的本地中转代理（= 中国大陆 Chinanet 出口）。
"""
import os, sys, json, time, gzip, io, zipfile, subprocess

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


def sniff(head: bytes):
    if not head:
        return "EMPTY", "0 字节"
    low = head[:600].lower()
    if head[:4] == b"PK\x03\x04":
        return "ZIP", "zip 魔数 PK\\x03\\x04 正确"
    if head[:2] == b"\x1f\x8b":
        return "GZIP", "gzip 魔数 \\x1f\\x8b 正确"
    if b"<!doctype html" in low:
        return "HTML", "HTML 错误页伪装成文件"
    if b"<html" in low:
        return "HTML", "HTML 页面"
    if low[:1] in (b"{", b"["):
        return "JSON", "JSON 响应(错误信息)"
    if b"application/xhtml" in low[:80]:
        return "HTML", "HTML"
    return "UNKNOWN", "未知类型 %r" % head[:20]


def verify_tgz(data):
    try:
        with gzip.GzipFile(fileobj=io.BytesIO(data)) as g:
            g.read(8192)
        return True, "gzip 可正常解压"
    except Exception as e:
        return False, "gzip 解压失败: %s" % e


def verify_zip(data):
    try:
        z = zipfile.ZipFile(io.BytesIO(data))
        names = z.namelist()
        bad = z.testzip()
        if bad:
            return False, "zip 内 %s CRC 损坏" % bad
        return True, "zip 可完整解包(%d 条目)" % len(names)
    except Exception as e:
        return False, "zip 解包失败: %s" % e


def curl(url, timeout=120, outfile=None, follow=True, dump_headers=False):
    """执行 curl，返回 (meta_dict, filepath, headers_text)"""
    of = outfile or os.path.join(TMP, "f_%d.bin" % int(time.time() * 1000 % 1e9))
    cmd = ["curl", "-s", "-L" if follow else "--max-redirs", "-o", of,
           "-w", ("%{http_code}|%{size_download}|%{time_total}|%{speed_download}|"
                  "%{url_effective}|%{content_type}|%{num_redirects}|%{time_connect}|"
                  "%{time_starttransfer}"),
           "-m", str(timeout), "-A", UA, "-H", "Accept-Encoding: identity"]
    if not follow:
        cmd.remove("--max-redirs")
    cmd.append(url)
    if dump_headers:
        hf = of + ".hdr"
        cmd += ["-D", hf]
    else:
        hf = None
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout + 30)
    meta = {"raw_w": p.stdout.strip(), "stderr": p.stderr.strip()[:200], "curl_exit": p.returncode}
    parts = meta["raw_w"].split("|")
    if len(parts) >= 8:
        meta.update({
            "status": int(parts[0] or 0), "size": int(parts[1] or 0),
            "time": float(parts[2] or 0), "speed_kb": round(float(parts[3] or 0) / 1024, 1),
            "final_url": parts[4], "content_type": parts[5],
            "num_redirects": int(parts[6] or 0),
            "time_connect": float(parts[7] or 0),
        })
    else:
        meta.update({"status": 0, "size": 0, "time": 0, "final_url": "", "num_redirects": 0})
    headers = ""
    if hf and os.path.exists(hf):
        headers = open(hf, "r", encoding="utf-8", errors="replace").read()
    return meta, of, headers


def analyze(meta, filepath, expect_size=None):
    rec = dict(meta)
    rec.pop("raw_w", None)
    if os.path.exists(filepath):
        data = open(filepath, "rb").read()
    else:
        data = b""
    rec["file_on_disk"] = os.path.exists(filepath)
    head = data[:600]
    kind, detail = sniff(head)
    rec["kind"] = kind
    rec["kind_detail"] = detail
    rec["size"] = len(data)
    if expect_size:
        rec["expect_size"] = expect_size
        rec["size_match"] = (len(data) == expect_size)
    if kind == "GZIP" and len(data) > 100:
        ok, d2 = verify_tgz(data)
        rec["content_valid"], rec["content_detail"] = ok, d2
    elif kind == "ZIP" and len(data) > 100:
        ok, d2 = verify_zip(data)
        rec["content_valid"], rec["content_detail"] = ok, d2
    else:
        rec["content_valid"] = False
        rec["content_detail"] = detail
    if len(data) > 0 and len(data) < 600:
        rec["body_preview"] = data.decode("utf-8", "replace")[:300]
    try:
        os.remove(filepath)
    except Exception:
        pass
    return rec


def show(rec):
    print(json.dumps(rec, ensure_ascii=False), flush=True)


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


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "proxy"
    allrec = []

    if mode == "baseline":
        print("### BASELINE 直连 github.com ###", flush=True)
        for label, url, exp in [("direct-MID-5.8MB", MID, MID_SIZE),
                                ("direct-SMALL-763KB", SMALL, SMALL_SIZE)]:
            for attempt in (1, 2):
                m, f, h = curl(url, timeout=120)
                r = analyze(m, f, exp)
                r["label"] = label
                r["attempt"] = attempt
                r["redirect_chain"] = [l.strip() for l in h.splitlines()
                                       if l.lower().startswith("location:")]
                show(r)
                allrec.append(r)
                if r.get("status") == 200 and r.get("size_match"):
                    break

    elif mode == "proxy":
        for name, prefix in PROXIES:
            print("### PROXY %s ###" % name, flush=True)
            # 先 HEAD 看 302 去向
            m, f, h = curl(prefix + MID, timeout=120, follow=False, dump_headers=True)
            head_rec = {"label": name + "-HEAD", "url": prefix + MID,
                        "status": m.get("status"), "size": m.get("size"),
                        "time": m.get("time"),
                        "location": [l.strip() for l in h.splitlines()
                                     if l.lower().startswith("location:")],
                        "headers_tail": h.splitlines()[-6:] if h else []}
            show(head_rec)
            allrec.append(head_rec)
            # 实际下载（先小后中，节省时间）
            for label, url, exp in [(name + "-SMALL", prefix + SMALL, SMALL_SIZE),
                                    (name + "-MID", prefix + MID, MID_SIZE)]:
                m, f, h = curl(url, timeout=120, dump_headers=True)
                r = analyze(m, f, exp)
                r["label"] = label
                r["redirect_chain"] = [l.strip() for l in h.splitlines()
                                       if l.lower().startswith("location:")]
                show(r)
                allrec.append(r)

    with open(os.path.join(OUT, "%s.json" % mode), "w", encoding="utf-8") as f:
        json.dump(allrec, f, ensure_ascii=False, indent=2)
    print("saved -> results/%s.json" % mode, flush=True)


if __name__ == "__main__":
    main()
