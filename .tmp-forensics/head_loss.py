# -*- coding: utf-8 -*-
"""取证 8：判定 assistant 正文「头部缺失/损坏」发生在哪一侧。

方法：发一个**纯文本**请求（不让模型调工具），抓原始 SSE 字节，
     然后用「现有客户端逻辑」等价复刻一遍，看是否丢头。
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import build_request_body, raw_sse, parse_sse, KEYS

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"
os.makedirs(OUT, exist_ok=True)

# 强制纯文本：明确要求不要调用任何工具
PROMPT = ("请直接用中文回答，不要调用任何工具："
          "用一句话说明什么是「有理数」，然后换行写 5 个有理数的例子。"
          "回答的第一句话必须以「有理数是」四个字开头。")

body = build_request_body([{"role": "user", "content": PROMPT}])
status, reason, hdrs, lines = raw_sse(body, key=KEYS[0])
print("HTTP %s %s" % (status, reason))
if status != 200:
    print("".join(lines)[:600]); sys.exit(1)

with io.open(OUT + "/head_loss.txt", "w", encoding="utf-8") as f:
    f.write("\n".join(lines))

# 1) 原始 SSE 前若干条 content delta（含 reasoning 分离显示）
print("\n--- 原始 SSE：前 8 条 delta（repr）---")
n = 0
for l in lines:
    if not l.startswith("data:"):
        continue
    p = l[5:].strip()
    if not p or p == "[DONE]":
        continue
    try:
        node = json.loads(p)
    except Exception as e:
        print("  [解析失败] %r  (%s)" % (l[:160], e)); continue
    ch = (node.get("choices") or [{}])[0]
    d = ch.get("delta") or {}
    print("  keys(delta)=%s  %r" % (sorted(d.keys()), l[-180:]))
    n += 1
    if n >= 8:
        break

# 2) 客户端等价逻辑拼接
res = parse_sse(lines)
print("\n--- 客户端逻辑拼接结果 ---")
print("  content 前 120 字 = %r" % res["content"][:120])
print("  reasoning 前 120 字 = %r" % res["reasoning"][:120])

# 3) 独立复核：直接把所有 delta.content 拼起来
raw = ""
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
    for ch in node.get("choices") or []:
        d = ch.get("delta") or {}
        if isinstance(d.get("content"), str):
            raw += d["content"]
print("\n--- 独立复核拼接 ---")
print("  raw 前 120 字 = %r" % raw[:120])
print("  与客户端结果一致: %s" % (raw == res["content"]))
print("  raw 长度=%d  client 长度=%d" % (len(raw), len(res["content"])))

# 4) 客户端逻辑丢弃了什么？
if raw != res["content"]:
    print("  !!! 有差异。逐 delta 比对：")
    ri = ci = 0
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
        for ch in node.get("choices") or []:
            d = ch.get("delta") or {}
            cd = d.get("content")
            if isinstance(cd, str) and cd:
                if cd not in raw[ri:ri+len(cd)+2]:
                    print("     该 delta 未被原样保留: %r" % cd[:80])
                ri += len(cd)
