# -*- coding: utf-8 -*-
"""取证 9：批量重放 + 变体 A/B。

用法: python batch.py <case> <n>
case:
  e1   重放失败点1 的请求（history = 真实会话 [0..13]，即产出 DSML 的那一次）
  e2   重放失败点2 的请求（history = [0..21]）
  null assistant.content 改成 null 的对照
  long 长上下文压力
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import (build_request_body, conv_to_history, load_real_conversation,
                    raw_sse, parse_sse, KEYS)

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"
os.makedirs(OUT, exist_ok=True)

CASE = sys.argv[1]
N = int(sys.argv[2]) if len(sys.argv) > 2 else 10

conv = load_real_conversation("按照md文件修改这份政治试卷")
hist_all = conv_to_history(conv)

VALID = {"list_files", "read_file", "write_file", "write_xlsx", "write_docx",
         "write_pptx", "search_tool", "install_tool", "run_python", "run_node",
         "web_fetch", "create_directory"}

if CASE == "e1":
    base = hist_all[:14]
elif CASE == "e2":
    base = hist_all[:22]
elif CASE == "null":
    base = []
    for m in hist_all[:14]:
        m2 = dict(m)
        if m2["role"] == "assistant" and m2.get("tool_calls"):
            m2["content"] = None
        base.append(m2)
elif CASE == "long":
    filler = "这是用于测试长上下文的中文填充文本，重复很多次以拉长上下文。" * 900
    base = [{"role": "user", "content": filler}]
else:
    raise SystemExit("unknown case")

rows = []
for i in range(N):
    body = build_request_body(base)
    key = KEYS[i % 2]
    t0 = time.time()
    try:
        status, reason, hdrs, lines = raw_sse(body, key=key, timeout=240)
    except Exception as ex:
        print("run %2d: 网络异常 %s" % (i, ex)); time.sleep(3); continue
    dt = time.time() - t0
    if status != 200:
        print("run %2d: HTTP %s %s  %s" % (i, status, reason, "".join(lines)[:200]))
        time.sleep(4); continue

    with io.open(OUT + "/%s_%02d.txt" % (CASE, i), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    res = parse_sse(lines)
    dsml = "DSML" in res["content"]
    weird = [t["name"] for t in res["tool_calls"] if t["name"] not in VALID]
    # DSML 泄漏的原始行
    dsml_lines = [l[:240] for l in lines if "DSML" in l]

    print("run %2d: finish=%-11s dsml=%-5s weird=%-30s out_tok=%-5s %.0fs"
          % (i, res["finish_reason"], dsml, weird,
             (res["usage"] or {}).get("completion_tokens"), dt))
    if dsml:
        print("      泄漏正文前 200: %r" % res["content"][:200])
        for l in dsml_lines[:3]:
            print("      原始行: %s" % l)
    if weird:
        for l in lines:
            if '"tool_calls"' in l and "function" in l:
                print("      原始 tool_calls 行: %s" % l[:300])

    rows.append({"run": i, "finish": res["finish_reason"], "dsml": dsml,
                 "weird": weird, "content_head": res["content"][:200],
                 "usage": res["usage"],
                 "dsml_lines": dsml_lines})
    time.sleep(2)

with io.open(OUT + "/%s_summary.json" % CASE, "w", encoding="utf-8") as f:
    json.dump(rows, f, ensure_ascii=False, indent=1)

print()
print("汇总 %s: 共 %d 次, DSML %d 次, 异常工具名 %d 次, length截断 %d 次"
      % (CASE, len(rows),
         sum(1 for r in rows if r["dsml"]),
         sum(1 for r in rows if r["weird"]),
         sum(1 for r in rows if r["finish"] == "length")))
