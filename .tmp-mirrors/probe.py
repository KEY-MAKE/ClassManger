# -*- coding: utf-8 -*-
"""
实测 GitHub Release 附件下载在各「前缀式代理/加速」服务下的可用性。
只用标准库。走系统环境里的本地中转代理（模拟中国大陆出口）。
"""
import os, sys, time, json, gzip, io, zipfile, ssl, socket
import urllib.request, urllib.error
from urllib.parse import urlparse

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "results")
os.makedirs(OUT, exist_ok=True)

# 官方 API 确认的真实 release 附件（octocat/Hello-World 根本没有 release，会造成假 404）
SMALL = "https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-v3.8.1.tar.gz"
SMALL_SIZE = 763252
MID = "https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-windows-amd64-v3.8.1.zip"
MID_SIZE = 5870805

UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36"

SSL_CTX = ssl.create_default_context()
SSL_CTX.check_hostname = False
SSL_CTX.verify_mode = ssl.CERT_NONE

# 强制走环境里的中转代理（这就是"中国大陆出口"路径）
PROXY = os.environ.get("https_proxy") or os.environ.get("HTTPS_PROXY")
_opener = urllib.request.build_opener(
    urllib.request.ProxyHandler({"http": PROXY, "https": PROXY}),
    urllib.request.HTTPSHandler(context=SSL_CTX),
)


def sniff(head: bytes):
    """判断内容真实性：返回 (kind, detail)"""
    if not head:
        return "EMPTY", "0 字节，无内容"
    low = head[:400].lower()
    if head[:4] == b"PK\x03\x04":
        return "ZIP", "zip 魔数正确"
    if head[:2] == b"\x1f\x8b":
        return "GZIP", "gzip 魔数正确"
    if head[:4] == b"\x89PNG":
        return "PNG", "PNG 魔数正确"
    if b"<!doctype html" in low or b"<html" in low:
        return "HTML", "HTML 页面(错误页伪装)"
    if low[:1] == b"{" or low[:1] == b"[":
        return "JSON", "JSON 响应"
    if head[:5] == b"<!doc" or head[:4] == b"<!--":
        return "HTML", "HTML/文本"
    return "UNKNOWN", "未知类型: %r" % head[:16]


def verify_tar_gz(data: bytes):
    try:
        with gzip.GzipFile(fileobj=io.BytesIO(data)) as g:
            g.read(4096)
        return True, "gzip 可解压"
    except Exception as e:
        return False, "gzip 解压失败: %s" % e


def verify_zip(data: bytes):
    try:
        z = zipfile.ZipFile(io.BytesIO(data))
        names = z.namelist()
        bad = z.testzip()
        if bad:
            return False, "zip 内 %s CRC 损坏" % bad
        return True, "zip 可解包, %d 个条目" % len(names)
    except Exception as e:
        return False, "zip 解包失败: %s" % e


def fetch(url, timeout=90, max_bytes=None, label=""):
    """下载并返回实测数据字典"""
    rec = {"url": url, "label": label}
    t0 = time.time()
    try:
        req = urllib.request.Request(url, headers={
            "User-Agent": UA,
            "Accept": "*/*",
            "Accept-Encoding": "identity",
        })
        r = _opener.open(req, timeout=timeout)
        rec["status"] = r.getcode()
        rec["final_url"] = r.geturl()
        rec["redirected"] = (r.geturl() != url)
        rec["content_type"] = r.headers.get("Content-Type", "")
        rec["content_length_hdr"] = r.headers.get("Content-Length", "")
        chunks = []
        total = 0
        while True:
            b = r.read(65536)
            if not b:
                break
            chunks.append(b)
            total += len(b)
            if max_bytes and total >= max_bytes:
                break
        data = b"".join(chunks)
        rec["size"] = total
        rec["time"] = round(time.time() - t0, 2)
        rec["speed_kb"] = round(total / 1024.0 / rec["time"], 1) if rec["time"] > 0 else 0
        head = data[:400]
        kind, detail = sniff(head)
        rec["kind"] = kind
        rec["kind_detail"] = detail
        if kind == "GZIP" and len(data) > 100:
            ok, d2 = verify_tar_gz(data)
            rec["content_valid"] = ok
            rec["content_detail"] = d2
        elif kind == "ZIP" and len(data) > 100:
            ok, d2 = verify_zip(data)
            rec["content_valid"] = ok
            rec["content_detail"] = d2
        else:
            rec["content_valid"] = False
            rec["content_detail"] = detail
        return rec
    except urllib.error.HTTPError as e:
        rec["status"] = e.code
        rec["time"] = round(time.time() - t0, 2)
        rec["size"] = 0
        try:
            body = e.read(2048)
            rec["kind"], rec["kind_detail"] = sniff(body[:400])
            rec["content_detail"] = "HTTP错误体: %r" % body[:160]
        except Exception:
            rec["kind"] = "NONE"
            rec["kind_detail"] = "无错误体"
        rec["content_valid"] = False
        rec["final_url"] = url
        return rec
    except Exception as e:
        rec["status"] = 0
        rec["time"] = round(time.time() - t0, 2)
        rec["size"] = 0
        rec["error"] = "%s: %s" % (type(e).__name__, str(e)[:200])
        rec["kind"] = "NONE"
        rec["kind_detail"] = rec["error"]
        rec["content_valid"] = False
        return rec


def head_request(url, timeout=30):
    """只取响应头，用于看 302 跳转目标"""
    try:
        req = urllib.request.Request(url, headers={"User-Agent": UA}, method="HEAD")
        r = _opener.open(req, timeout=timeout)
        return {"status": r.getcode(), "final_url": r.geturl(),
                "location": r.headers.get("Location", ""),
                "content_length": r.headers.get("Content-Length", "")}
    except Exception as e:
        return {"status": 0, "error": "%s: %s" % (type(e).__name__, str(e)[:160])}


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
    mode = sys.argv[1] if len(sys.argv) > 1 else "all"
    results = []

    def run(label, url, expect_size=None, timeout=90, max_bytes=None):
        rec = fetch(url, timeout=timeout, max_bytes=max_bytes, label=label)
        rec["expect_size"] = expect_size
        if expect_size and rec.get("size") == expect_size:
            rec["size_match"] = True
        else:
            rec["size_match"] = False
        results.append(rec)
        print(json.dumps(rec, ensure_ascii=False), flush=True)
        return rec

    if mode in ("all", "baseline"):
        print("### BASELINE 直连 ###", flush=True)
        run("baseline-direct", MID, MID_SIZE, timeout=120)
        run("baseline-direct-small", SMALL, SMALL_SIZE, timeout=120)

    if mode in ("all", "proxy"):
        for name, prefix in PROXIES:
            print("### PROXY %s ###" % name, flush=True)
            h = head_request(prefix + MID, timeout=30)
            print(json.dumps({"head": name, **h}, ensure_ascii=False), flush=True)
            run(name, prefix + MID, MID_SIZE, timeout=120)
            run(name + "-small", prefix + SMALL, SMALL_SIZE, timeout=120)

    with open(os.path.join(OUT, "%s.json" % mode), "w", encoding="utf-8") as f:
        json.dump(results, f, ensure_ascii=False, indent=2)
    print("saved -> %s/%s.json" % (OUT, mode), flush=True)


if __name__ == "__main__":
    main()
