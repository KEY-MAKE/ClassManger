# -*- coding: utf-8 -*-
"""取证 7：批量重放真实失败回合，抓 DSML 出现的原始字节。

失败是随机的（stochastic），所以要重复多次。
每次都把原始 SSE 落盘，便于事后逐字节核对。
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import (build_request_body, conv_to_history, load_real_conversation,
                    raw_sse, parse_sse, KEYS)

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"
os.makedirs(OUT, exist_ok=True)

N = int(sys.argv[1]) if len(sys.argv) > 1 else 10
UPTO = int(sys.argv[2]) if len(sys.argv) > 2 else 14

conv = load_real_conversation("按照md文件修改这份政治试卷")
hist_all = conv_to_history(conv)

summary = []
for i in range(N):
    body = build_request_body(hist_all[:UPTO])
    key = KEYS[i % 2]
    t0 = time.time()
    status, reason, hdrs, lines = raw_sse(body, key=key)
    dt = time.time() - t0
    if status != 200:
        print("run %2d: HTTP %s %s" % (i, status, reason))
        summary.append({"run": i, "status": status})
        time.sleep(3)
        continue

    with io.open(OUT + "/loop_%02d.txt" % i, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    res = parse_sse(lines)
    dsml_in_content = "DSML" in res["content"]
    names = [t["name"] for t in res["tool_calls"]]
    weird = [n for n in names if n not in (
        "list_files", "read_file", "write_file", "write_xlsx", "write_docx",
        "write_pptx", "search_tool", "install_tool", "run_python", "run_node",
        "web_fetch", "create_directory")]
    print("run %2d: finish=%-12s dsml=%-5s weird_names=%-40s usage_out=%-5s %.1fs"
          % (i, res["finish_reason"], dsml_in_content, weird,
             (res["usage"] or {}).get("completion_tokens"), dt))
    if dsml_in_content:
        print("      DSML 正文前 300 字: %r" % res["content"][:300])
        print("      DSML 出现在原始行的下标:")
        for j, l in enumerate(lines):
            if "DSML" in l:
                print("        line[%d] %s" % (j, l[:300]))
    if weird:
        print("      !!! 异常工具名，原始 SSE 中的 tool_calls 行:")
        for j, l in enumerate(lines):
            if '"tool_calls"' in l and "function" in l:
                print("        line[%d] %s" % (j, l[:400]))
    summary.append({
        "run": i, "finish": res["finish_reason"], "dsml": dsml_in_content,
        "names": names, "weird": weird,
        "content_head": res["content"][:120],
        "usage": res["usage"],
    })
    time.sleep(2)

with io.open(OUT + "/loop_summary.json", "w", encoding="utf-8") as f:
    json.dump(summary, f, ensure_ascii=False, indent=1)

n_dsml = sum(1 for s in summary if s.get("dsml"))
n_weird = sum(1 for s in summary if s.get("weird"))
print()
print("汇总: 共 %d 次，DSML 出现 %d 次，异常工具名 %d 次" % (N, n_dsml, n_weird))
