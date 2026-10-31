# -*- coding: utf-8 -*-
"""取证 2：向网关发请求，抓原始 SSE 字节。

用法: python replay.py <case>
case ∈ {baseline, e1, e2, e3, e4, e5}
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import (build_request_body, build_tools, conv_to_history,
                    load_real_conversation, raw_sse, parse_sse, report,
                    tool_call_name_first_wins, SYSTEM_PROMPT)

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"
os.makedirs(OUT, exist_ok=True)

CASE = sys.argv[1] if len(sys.argv) > 1 else "baseline"
KEY_IDX = 0

conv = load_real_conversation("按照md文件修改这份政治试卷")
hist_all = conv_to_history(conv)

body = None
label = CASE

if CASE == "baseline":
    # 干净基线：短对话 + 完整 tools
    body = build_request_body([
        {"role": "user", "content": "列一下工作区里有什么文件。"},
    ])
    label = "baseline_clean"

elif CASE == "e1":
    # 复刻失败点 1：历史 = 真实会话 [0..13]（含 28KB 用户消息、8403/4573 字符 tool 结果）
    body = build_request_body(hist_all[:14])
    label = "e1_replay_msg0_13"

elif CASE == "e2":
    # 复刻失败点 2：历史 = 真实会话 [0..21]（再加 8020/1213 字符 tool 结果）
    body = build_request_body(hist_all[:22])
    label = "e2_replay_msg0_21"

elif CASE == "e3":
    # 自我强化检验：与 e1 同历史，但把含 DSML 的 assistant 正文清空（这里 [0..13] 尚无 DSML，
    # 故额外构造：手工注入一条含 DSML 的 assistant 消息）
    h = list(hist_all[:14])
    h.insert(13, {
        "role": "assistant",
        "content": "｜DSML｜ parameter name=\"code\" string=\"true\">\nprint(1)\n</｜DSML｜ parameter>\n</｜DSML｜ invoke>\n</｜DSML｜ calls>",
    })
    body = build_request_body(h)
    label = "e3_with_dsml_in_history"

elif CASE == "e4":
    # 长上下文压力：28KB 用户消息 + 多次 8KB tool 结果，但不含任何 DSML
    big = ("以下是需要处理的材料片段。\n" + "这是一段用于测试长上下文的中文填充文本。" * 900)
    h = [
        {"role": "user", "content": big},
        {"role": "assistant", "content": "我先列目录。",
         "tool_calls": [{"id": "chatcmpl-tool-aaa", "name": "list_files",
                         "arguments": "{\"path\": \"\"}"}]},
        {"role": "tool", "tool_call_id": "chatcmpl-tool-aaa",
         "content": "文件很多。" + "目录内容行。" * 1200},
        {"role": "assistant", "content": "再读一个文件。",
         "tool_calls": [{"id": "chatcmpl-tool-bbb", "name": "read_file",
                         "arguments": "{\"path\": \"a.txt\"}"}]},
        {"role": "tool", "tool_call_id": "chatcmpl-tool-bbb",
         "content": "文件正文。" * 3000},
    ]
    body = build_request_body(h)
    label = "e4_long_context_no_dsml"

elif CASE == "e5":
    # content: null vs "" 对照：把 assistant(带 tool_calls) 的 content 由 "" 改成 null
    h = [dict(m) for m in hist_all[:6]]
    for m in h:
        if m["role"] == "assistant" and m.get("tool_calls"):
            m["content"] = None
    body = build_request_body(h)
    label = "e5_assistant_content_null"

elif CASE == "e5b":
    # 同 e1 历史，但 assistant(带 tool_calls) 的 content 置 null
    h = [dict(m) for m in hist_all[:14]]
    for m in h:
        if m["role"] == "assistant" and m.get("tool_calls"):
            m["content"] = None
    body = build_request_body(h)
    label = "e5b_assistant_content_null_replay"

else:
    raise SystemExit("unknown case")

print("### CASE %s" % label)
print("请求体字符数: %d  消息数: %d" % (
    len(json.dumps(body, ensure_ascii=False)), len(body["messages"])))

with io.open(OUT + "/body_%s.json" % label, "w", encoding="utf-8") as f:
    f.write(json.dumps(body, ensure_ascii=False, indent=1))

status, reason, hdrs, lines = raw_sse(body, key_idx=None) if False else raw_sse(body)

print("HTTP %s %s" % (status, reason))
print("content-type: %s" % hdrs.get("content-type"))
if status != 200:
    print("响应体:")
    for l in lines[:20]:
        print("   ", l)
    sys.exit(1)

with io.open(OUT + "/sse_%s.txt" % label, "w", encoding="utf-8") as f:
    f.write("\n".join(lines))

# 原始 SSE 前 30 行（含 tool_calls 分片原样）
print("\n--- 原始 SSE 行（前 30 条 data: 行）---")
n = 0
for l in lines:
    if l.startswith("data:"):
        print("  " + l[:400])
        n += 1
        if n >= 30:
            break

res = parse_sse(lines)
print("\n--- 按现有解析逻辑（AiChatService.cs:430-544）---")
report(res, label)

print("\n--- DSML 出现在哪些原始行 ---")
for i, l in enumerate(lines):
    if "DSML" in l:
        print("  line[%d]: %s" % (i, l[:500]))

print("\n--- name 首次出现 vs 后覆盖前 对比 ---")
first = tool_call_name_first_wins(lines)
print("  后覆盖前(现有实现): %s" % [(t["id"], t["name"]) for t in res["tool_calls"]])
print("  首次出现(假设修复)  : %s" % [(t["id"], t["name"]) for t in first])

with io.open(OUT + "/parsed_%s.json" % label, "w", encoding="utf-8") as f:
    json.dump(res, f, ensure_ascii=False, indent=1)
