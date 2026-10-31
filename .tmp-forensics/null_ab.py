# -*- coding: utf-8 -*-
"""取证 13：assistant.content = "" vs null 的 A/B（网关接受性 + 行为差异）。

同时验证：网关是否接受 content=null（是否 400），以及两种写法下模型行为是否一致。
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import build_request_body, raw_sse, parse_sse, KEYS

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"
os.makedirs(OUT, exist_ok=True)

VALID = {"list_files", "read_file", "write_file", "write_xlsx", "write_docx",
         "write_pptx", "search_tool", "install_tool", "run_python", "run_node",
         "web_fetch", "create_directory"}

BASE = [
    {"role": "user", "content": "列一下工作区里有什么文件。"},
    {"role": "assistant", "content": None,   # 这里改
     "tool_calls": [{"id": "chatcmpl-tool-a1", "name": "list_files",
                     "arguments": "{\"path\": \"\"}"}]},
    {"role": "tool", "tool_call_id": "chatcmpl-tool-a1", "content": "a.txt\nb.txt"},
]

N = int(sys.argv[1]) if len(sys.argv) > 1 else 6

for mode in ("empty", "null"):
    agg = {"n": 0, "http_err": 0, "tool_ok": 0, "dsml": 0, "weird": 0}
    for i in range(N):
        hist = [dict(m) for m in BASE]
        hist[1]["content"] = "" if mode == "empty" else None
        body = build_request_body(hist)
        status, reason, hdrs, lines = raw_sse(body, key=KEYS[i % 2], timeout=120)
        if status != 200:
            agg["http_err"] += 1
            print("[%s] run %d: HTTP %s %s -> %s" % (mode, i, status, reason,
                                                      "".join(lines)[:300]))
            time.sleep(3)
            continue
        res = parse_sse(lines)
        names = [t["name"] for t in res["tool_calls"]]
        weird = [n for n in names if n not in VALID]
        dsml = "DSML" in res["content"]
        agg["n"] += 1
        agg["tool_ok"] += int(bool(names) and not weird)
        agg["dsml"] += int(dsml)
        agg["weird"] += int(bool(weird))
        print("[%s] run %d: HTTP200 finish=%-11s names=%-16s dsml=%-5s content=%r"
              % (mode, i, res["finish_reason"], names, dsml, res["content"][:60]))
        time.sleep(2)
    print("  >>> %s 汇总: %s" % (mode, agg))
    print()
