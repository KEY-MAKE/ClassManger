# -*- coding: utf-8 -*-
"""取证 16：max_tokens 与 DSML 泄漏的关系。

关键观察：真实失败会话的 assistant 消息 14/22 是**纯 DSML 正文**（没有 tool_calls），
            而 e1/e2 重放时反复出现 finish_reason=length（正好 4096）。

假说：max_tokens=4096 把「思考 token」+「DSML 正文」一起卡住，
      导致模型在 DSML 块中途被截断 → 网关的 DSML→tool_calls 转换失败 →
      原始 DSML 泄漏到 content。

对照：同一历史，只改 max_tokens（4096 vs 16384），比较
      (a) finish_reason=length 比例  (b) DSML 泄漏比例  (c) 正常 tool_calls 比例
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import (build_request_body, conv_to_history, load_real_conversation,
                    raw_sse, parse_sse, KEYS)

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"

MAXTOK = int(sys.argv[1])
N = int(sys.argv[2]) if len(sys.argv) > 2 else 6
HIST = int(sys.argv[3]) if len(sys.argv) > 3 else 14

conv = load_real_conversation("按照md文件修改这份政治试卷")
hist = conv_to_history(conv)[:HIST]

VALID = {"list_files", "read_file", "write_file", "write_xlsx", "write_docx",
         "write_pptx", "search_tool", "install_tool", "run_python", "run_node",
         "web_fetch", "create_directory"}

agg = {"n": 0, "length": 0, "dsml": 0, "weird": 0, "ok_tool": 0, "text_only": 0}
for i in range(N):
    body = build_request_body(hist, max_tokens=MAXTOK)
    status, reason, hdrs, lines = raw_sse(body, key=KEYS[i % 2], timeout=300)
    if status != 200:
        print("run %d: HTTP %s %s" % (i, status, reason)); time.sleep(4); continue
    with io.open(OUT + "/mt%d_h%d_%02d.txt" % (MAXTOK, HIST, i), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    res = parse_sse(lines)
    dsml = "DSML" in res["content"]
    weird = [t["name"] for t in res["tool_calls"] if t["name"] not in VALID]
    names = [t["name"] for t in res["tool_calls"]]
    length = res["finish_reason"] == "length"
    agg["n"] += 1
    agg["length"] += int(length)
    agg["dsml"] += int(dsml)
    agg["weird"] += int(bool(weird))
    agg["ok_tool"] += int(bool(names) and not weird)
    agg["text_only"] += int(bool(res["content"]) and not names)
    print("mt=%-6d run %2d: finish=%-11s names=%-20s dsml=%-5s weird=%-20s out_tok=%s"
          % (MAXTOK, i, res["finish_reason"], names, dsml, weird,
             (res["usage"] or {}).get("completion_tokens")))
    if dsml:
        print("      content=%r" % res["content"][:250])
        for l in lines:
            if "DSML" in l:
                print("      原始行: %s" % l[:300]); break
    if weird:
        for l in lines:
            if '"tool_calls"' in l and '"function"' in l:
                print("      原始 tool_calls 行: %s" % l[:300]); break
    time.sleep(2)

print()
print("汇总 max_tokens=%d hist=%d: %s" % (MAXTOK, HIST, agg))
