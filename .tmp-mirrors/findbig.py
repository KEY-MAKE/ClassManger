# -*- coding: utf-8 -*-
"""定位仓库中的大文件(>50MB), 用于实测 jsDelivr 的单文件体积限制"""
import json, urllib.request, os, sys

proxy = os.environ.get("https_proxy") or os.environ.get("HTTPS_PROXY")
op = urllib.request.build_opener(
    urllib.request.ProxyHandler({"http": proxy, "https": proxy}))


def get(u):
    return json.load(op.open(urllib.request.Request(
        u, headers={"User-Agent": "curl/8.13"}), timeout=50))


REPOS = ["git-for-windows/git", "PowerShell/PowerShell",
         "microsoft/vscode", "dotnet/runtime", "flutter/flutter"]
for repo in REPOS:
    try:
        t = get("https://api.github.com/repos/" + repo + "/git/trees/HEAD?recursive=1")
        big = sorted([(x.get("size", 0), x["path"]) for x in t.get("tree", [])
                      if x.get("type") == "blob" and x.get("size", 0) > 50_000_000],
                     reverse=True)
        print("%-26s trunc=%-6s %s" % (repo, t.get("truncated"), big[:2]), flush=True)
    except Exception as e:
        print("%-26s ERR %s" % (repo, str(e)[:100]), flush=True)
