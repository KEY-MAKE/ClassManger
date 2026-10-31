# -*- coding: utf-8 -*-
"""取证 18：真实失败历史上 assistant.content = "" vs null 的 A/B（大样本）。

审计问题①：assistant 带 tool_calls 时 content 写 "" 还是 null？网关接受吗？行为有差异吗？
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import (build_request_body, conv_to_history, load_real_conversation,
                    raw_sse, parse_sse, KEYS)

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"

MODE = sys.argv[1]        # empty | null
N = int(sys.argv[2])
HIST = int(sys.argv[3]) if len(sys.argv) > 3 else 22

conv = load_real_conversation("按照md文件修改这份政治试卷")
base = conv_to_history(conv)[:HIST]

VALID = {"list_files", "read_file", "write_file", "write_xlsx", "write_docx",
         "write_pptx", "search_tool", "install_tool", "run_python", "run_node",
         "web_fetch", "create_directory"}

agg = {"n": 0, "http_err": 0, "length": 0, "dsml": 0, "weird": 0, "ok_tool": 0}
for i in range(N):
    h = []
    for m in base:
        m2 = dict(m)
        if MODE == "null" and m2["role"] == "assistant" and m2.get("tool_calls"):
            m2["content"] = None
        h.append(m2)
    body = build_request_body(h)
    status, reason, hdrs, lines = raw_sse(body, key=KEYS[i % 2], timeout=300)
    if status != 200:
        agg["http_err"] += 1
        print("[%s] run %d: HTTP %s %s" % (MODE, i, status, reason)); time.sleep(4); continue
    with io.open(OUT + "/ab_%s_%02d.txt" % (MODE, i), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    res = parse_sse(lines)
    dsml = "DSML" in res["content"]
    names = [t["name"] for t in res["tool_calls"]]
    weird = [n for n in names if n not in VALID]
    agg["n"] += 1
    agg["length"] += int(res["finish_reason"] == "length")
    agg["dsml"] += int(dsml)
    agg["weird"] += int(bool(weird))
    agg["ok_tool"] += int(bool(names) and not weird)
    print("[%-5s] run %2d: finish=%-11s names=%-18s dsml=%-5s weird=%s"
          % (MODE, i, res["finish_reason"], names, dsml, weird))
    if dsml:
        print("      content=%r" % res["content"][:220])
    if weird:
        for l in lines:
            if '"tool_calls"' in l and '"function"' in l:
                print("      原始行: %s" % l[:280]); break
    time.sleep(2)

print()
print("汇总 %s: %s" % (MODE, agg))
