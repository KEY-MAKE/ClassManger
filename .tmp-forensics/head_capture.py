# -*- coding: utf-8 -*-
"""取证 6：抓原始 SSE 字节，判定「assistant 正文头部缺失」是网关侧还是客户端侧。

做法：发若干请求，让模型输出以中文/## 开头的正文，
然后逐条打印**原始 SSE 行**（repr，看码位），并与客户端解析结果对照。
"""
import json, io, os, sys, time, re

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import build_request_body, raw_sse, parse_sse, KEYS

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"
os.makedirs(OUT, exist_ok=True)

PROMPTS = [
    "用 Markdown 写一份《期末考试分析》报告，第一行必须是二级标题 ## 期末考试分析，正文至少 300 字。",
    "请先说明你会怎么做，然后输出结果。第一句必须以「我先」两个字开头。",
]

for pi, prompt in enumerate(PROMPTS):
    body = build_request_body([{"role": "user", "content": prompt}])
    status, reason, hdrs, lines = raw_sse(body, key=KEYS[pi % 2])
    print("=" * 100)
    print("PROMPT %d: %s" % (pi, prompt[:50]))
    print("HTTP %s %s" % (status, reason))
    if status != 200:
        print("".join(lines)[:500])
        continue

    with io.open(OUT + "/head_p%d.txt" % pi, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    # 逐条打印前 12 个含 content 的 delta（原始 repr）
    print("--- 前 12 个 content delta（原始 SSE 行 repr）---")
    n = 0
    for l in lines:
        if not l.startswith("data:"):
            continue
        p = l[5:].strip()
        if not p or p == "[DONE]":
            continue
        try:
            node = json.loads(p)
        except Exception:
            print("  [非JSON行] %r" % l[:200])
            continue
        ch = (node.get("choices") or [{}])[0]
        d = ch.get("delta") or {}
        if d.get("content") is not None or d.get("reasoning_content") is not None:
            print("  %r" % l[:260])
            n += 1
            if n >= 12:
                break

    res = parse_sse(lines)
    print("--- 客户端解析后的正文前 120 字 ---")
    print("  %r" % res["content"][:120])
    print("--- 原始 SSE 里 content 拼接后的前 120 字（独立复核）---")
    raw_content = ""
    for l in lines:
        if not l.startswith("data:"):
            continue
        p = l[5:].strip()
        if not p or p == "[DONE]":
            continue
        try:
            node = json.loads(p)
        except Exception:
            continue
        ch = (node.get("choices") or [{}])[0]
        d = ch.get("delta") or {}
        if isinstance(d.get("content"), str):
            raw_content += d["content"]
    print("  %r" % raw_content[:120])
    print("  两��是否一致: %s" % (raw_content == res["content"]))
    # 检查是否有 U+FFFD / 孤立代理项
    print("  正文含 U+FFFD: %s" % ("�" in res["content"]))
    print()
    time.sleep(1)
