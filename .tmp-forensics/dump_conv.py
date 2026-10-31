# -*- coding: utf-8 -*-
"""取证 1b：完整 dump 真实会话，输出协议视角的骨架（含 arguments 原文、tool_call_id 配对）。"""
import json, io, sys

sys.stdout.reconfigure(encoding='utf-8')

CONV = r"C:/Users/admin/AppData/Roaming/ClassManager/ai-conversations.json"
with io.open(CONV, encoding="utf-8") as f:
    convs = json.load(f)

for conv in convs:
    msgs = conv.get("Messages") or conv.get("messages") or []
    print("=" * 110)
    print("会话 title=%r 消息数=%d" % (conv.get("Title", conv.get("title")), len(msgs)))
    print("=" * 110)
    for i, m in enumerate(msgs):
        role = m.get("Role")
        c = m.get("Content") or ""
        tcs = m.get("ToolCalls")
        tcid = m.get("ToolCallId")
        tn = m.get("ToolName")
        dsml = "DSML" in c
        print("[%d] role=%-9s contentLen=%-6d tool_call_id=%-24s tool_name=%-14s%s"
              % (i, role, len(c), str(tcid), str(tn), "   <<<<< DSML" if dsml else ""))
        if role == "assistant" and c:
            head = c[:220].replace("\n", "\\n")
            print("      content=%r" % head)
        if tcs:
            for tc in tcs:
                args = tc.get("Arguments")
                print("      TOOLCALL id=%s" % tc.get("Id"))
                print("               name=%s" % tc.get("Name"))
                print("               arguments.pytype=%s len=%d" % (type(args).__name__, len(args or "")))
                print("               arguments.raw=%r" % (args or "")[:300])
    print()
