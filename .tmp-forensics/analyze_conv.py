# -*- coding: utf-8 -*-
"""取证 1：分析真实会话历史，定位 DSML 出现的位置与上下文形态。"""
import json, io, sys, re

sys.stdout.reconfigure(encoding='utf-8')

CONV = r"C:/Users/admin/AppData/Roaming/ClassManager/ai-conversations.json"

with io.open(CONV, encoding="utf-8") as f:
    convs = json.load(f)

print("会话总数:", len(convs))

DSML_MARK = "DSML"
TOOLCALL_MARK = "tool_calls"

hits = []
for conv in convs:
    msgs = conv.get("Messages") or conv.get("messages") or []
    for i, m in enumerate(msgs):
        content = m.get("Content") or ""
        if DSML_MARK in content:
            hits.append((conv, i, m, content))

print("含 DSML 的 assistant 消息数:", len(hits))
print()

for conv, i, m, content in hits:
    msgs = conv.get("Messages") or conv.get("messages") or []
    print("=" * 100)
    print("会话 id=%s title=%r updated=%s" % (conv.get("Id", conv.get("id")),
                                              conv.get("Title", conv.get("title")),
                                              conv.get("UpdatedAt", conv.get("updatedAt"))))
    print("消息下标 %d / 共 %d 条" % (i, len(msgs)))
    print("-" * 100)
    # 打印前后各 3 条消息的骨架
    lo = max(0, i - 3)
    hi = min(len(msgs), i + 4)
    for j in range(lo, hi):
        mm = msgs[j]
        role = mm.get("Role")
        c = mm.get("Content") or ""
        tcs = mm.get("ToolCalls")
        tcid = mm.get("ToolCallId")
        tn = mm.get("ToolName")
        mark = "  <<< DSML" if DSML_MARK in c else ""
        print("  [%d] role=%-9s len=%-7d tool_call_id=%-22s tool_name=%-16s%s"
              % (j, role, len(c), str(tcid), str(tn), mark))
        if tcs:
            for tc in tcs:
                print("        tool_call id=%s name=%s arguments(type=%s)=%r"
                      % (tc.get("Id"), tc.get("Name"), type(tc.get("Arguments")).__name__,
                         (tc.get("Arguments") or "")[:160]))
    print("-" * 100)
    print("DSML 消息正文全文（%d 字）:" % len(content))
    print(content)
    print()
