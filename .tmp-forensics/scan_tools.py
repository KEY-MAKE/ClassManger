# -*- coding: utf-8 -*-
"""取证 4：检查真实会话里 tool 结果的内容污染（是否含二进制乱码）。"""
import json, io, sys, re

sys.stdout.reconfigure(encoding="utf-8")
CONV = r"C:/Users/admin/AppData/Roaming/ClassManager/ai-conversations.json"

with io.open(CONV, encoding="utf-8") as f:
    convs = json.load(f)

BIN_HINT = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f�]")

for conv in convs:
    msgs = conv.get("Messages") or conv.get("messages") or []
    print("=" * 100)
    print("会话 %r" % conv.get("Title"))
    print("=" * 100)
    for i, m in enumerate(msgs):
        role = m.get("Role")
        c = m.get("Content") or ""
        if role == "tool":
            hits = BIN_HINT.findall(c)
            pct = 100.0 * len(hits) / max(1, len(c))
            print("  [%2d] tool  name=%-16s len=%-6d 可疑字符=%-5d (%.2f%%)  %s"
                  % (i, str(m.get("ToolName")), len(c), len(hits), pct,
                     "  <<< 含二进制乱码" if pct > 1 else ""))
            if pct > 1:
                print("        head=%r" % c[:160])
