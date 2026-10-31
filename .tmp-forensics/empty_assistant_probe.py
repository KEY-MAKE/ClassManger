# -*- coding: utf-8 -*-
"""取证 14：异常轮留下的「空 assistant 消息（无 tool_calls）」会不会被网关拒绝？

背景：AiOfficeViewModel 的纠错重试路径会在 _history 里留下一条 content="" 的
assistant 消息（无 tool_calls）。而 AiOfficeViewModel.cs:237 的注释明确说
「空 assistant 消息会让下一轮请求被服务端拒绝」。

但 FORENSIC-protocol.md §3.3 的 A/B 实验只覆盖了「带 tool_calls 时 content 为
"" / null」，没有覆盖「不带 tool_calls 的空 assistant 消息」。

这里直接对真实网关验证该形态。
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import build_request_body, raw_sse, parse_sse, KEYS

sys.stdout.reconfigure(encoding="utf-8")

N = int(sys.argv[1]) if len(sys.argv) > 1 else 3

BASE = [
    {"role": "user", "content": "请用一句话回答：中国的首都是哪里？"},
    # 异常轮留下的空 assistant 消息：无 tool_calls
    {"role": "assistant", "content": ""},
    # 纠错提示（产品实际是以 role=user 追加的）
    {"role": "user", "content": "上一轮你只输出了内部思考内容，没有给用户可读的回答。"
                                "请直接给出面向用户的回答（思考过程不需要写出来）。"},
]

ok = 0
for i in range(N):
    body = build_request_body(BASE)
    status, reason, hdrs, lines = raw_sse(body, key=KEYS[i % 2], timeout=120)
    print("[empty-assistant] run %d: HTTP %s %s" % (i, status, reason))
    if status != 200:
        print("   body: %s" % "".join(lines)[:400])
    else:
        res = parse_sse(lines)
        print("   finish=%s content=%r" % (res["finish_reason"], res["content"][:120]))
        ok += 1
    time.sleep(2)

print()
print(">>> 空 assistant（无 tool_calls）被网关接受: %d/%d" % (ok, N))
