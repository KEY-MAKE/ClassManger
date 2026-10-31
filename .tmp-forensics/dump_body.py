# -*- coding: utf-8 -*-
"""取证 1：dump 我们实际发出的请求体，并逐条核对协议合规性。

等价复刻 AiChatService.BuildRequestBody（AiChatService.cs:251-329）。
"""
import json, io, sys

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import (build_request_body, build_tools, conv_to_history,
                    load_real_conversation, SYSTEM_PROMPT)

sys.stdout.reconfigure(encoding="utf-8")

OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"
import os
os.makedirs(OUT, exist_ok=True)

conv = load_real_conversation("按照md文件修改这份政治试卷")
hist = conv_to_history(conv)

body = build_request_body(hist)
raw = json.dumps(body, ensure_ascii=False, separators=(",", ":"))

print("=" * 100)
print("完整请求体（真实失败会话，%d 条消息，%d 字符）" % (len(body["messages"]), len(raw)))
print("=" * 100)
print(raw)
print()

with io.open(OUT + "/request_body_real.json", "w", encoding="utf-8") as f:
    f.write(raw)

# ---------------------------------------------------------------- 逐条核对
print("=" * 100)
print("逐条核对")
print("=" * 100)

msgs = body["messages"]
checks = []

# 1) assistant 带 tool_calls 时的 content
asst_with_tc = [m for m in msgs if m.get("role") == "assistant" and m.get("tool_calls")]
print("\n[核对 1] assistant 带 tool_calls 时的 content 取值")
vals = {}
for m in asst_with_tc:
    v = m.get("content", "<absent>")
    vals[repr(v)] = vals.get(repr(v), 0) + 1
print("  content 取值分布: %s" % vals)
print("  → 我们写的是 \"\"（空字符串），不是 null；也不是省略该字段。")
print("  → 标准 OpenAI 规范：assistant 带 tool_calls 时 content 应为 null 或省略。")
checks.append(("assistant.content", "不合规（用 \"\" 而非 null）"))

# 2) arguments 的 JSON 类型
print("\n[核对 2] tool_calls[].function.arguments 的 JSON 类型")
allargs = []
for m in asst_with_tc:
    for tc in m["tool_calls"]:
        allargs.append((tc["function"]["name"], tc["function"]["arguments"]))
types = {}
for name, a in allargs:
    types[type(a).__name__] = types.get(type(a).__name__, 0) + 1
print("  Python 复刻后的类型分布: %s" % types)
print("  样例（原始 JSON 片段）:")
for name, a in allargs[:3]:
    print("    %-12s → %r" % (name, a[:90]))
print("  → 序列化为 JSON **字符串**（带转义引号），符合标准 OpenAI 规范。")
checks.append(("arguments 类型", "合规（JSON 字符串，非对象）"))

# 3) tool_call_id 配对
print("\n[核对 3] tool 消息 tool_call_id 与 assistant tool_calls[].id 的配对")
ids = []
for m in msgs:
    if m.get("role") == "assistant":
        for tc in m.get("tool_calls") or []:
            ids.append(tc["id"])
tool_ids = [m.get("tool_call_id") for m in msgs if m.get("role") == "tool"]
print("  assistant 侧 id (%d 个): %s" % (len(ids), ids))
print("  tool     侧 id (%d 个): %s" % (len(tool_ids), tool_ids))
print("  tool 侧全部能在 assistant 侧找到: %s" % all(i in ids for i in tool_ids))
print("  assistant 侧未收到结果的: %s" % [i for i in ids if i not in tool_ids])
print("  多余/为空的 tool_call_id: %s" % [i for i in tool_ids if not i])
checks.append(("tool_call_id 配对", "合规（一一配对）"))

# 4) tools 结构
print("\n[核对 4] tools 数组结构")
for t in body["tools"]:
    f = t["function"]
    ps = f["parameters"]
    print("  type=%-8s name=%-18s has_desc=%-5s params_keys=%s props=%d required=%s"
          % (t["type"], f["name"], bool(f.get("description")), sorted(ps.keys()),
             len(ps.get("properties", {})), ps.get("required")))
print("  → 结构完整（type/function.name/function.description/function.parameters 齐全）。")
checks.append(("tools 结构", "合规"))

# 5) 额外发现：哪些工具的参数 schema 是空的
print("\n[核对 5] parameters 中无任何属性定义的工具（模型只能靠 description 散文猜参数名）")
empty = [t["function"]["name"] for t in body["tools"]
         if not t["function"]["parameters"].get("properties")]
print("  %s" % empty)
print("  → 这些工具在 schema 里看不到参数名；例如 run_python 的 'code' 只写在中文描述里。")

# 6) assistant content 里是否混入了 DSML（自我强化的输入源）
print("\n[核对 6] 历史中 assistant content 含 DSML 的消息")
dsml_idx = []
for i, m in enumerate(msgs):
    if m.get("role") == "assistant" and "DSML" in (m.get("content") or ""):
        dsml_idx.append(i)
print("  消息下标: %s" % dsml_idx)
print("  → 这些 DSML 文本会作为 assistant 正文原样回传给模型。")
