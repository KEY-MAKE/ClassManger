# -*- coding: utf-8 -*-
"""取证 3：在真实会话的**所有**字段里找 DSML/类似标记的污染源，
   并检查 assistant 正文「头部缺失」现象。"""
import json, io, sys, re, unicodedata

sys.stdout.reconfigure(encoding="utf-8")
CONV = r"C:/Users/admin/AppData/Roaming/ClassManager/ai-conversations.json"

with io.open(CONV, encoding="utf-8") as f:
    convs = json.load(f)

DSML_PAT = re.compile(r"DSML")
# 宽松模式：任何 "｜...｜" 形式的标记、以及 invoke/calls/parameter 这类 DSML 关键词
LOOSE = re.compile(r"｜[^｜]*｜|</?\s*(invoke|calls|parameter)\b|<｜.*?｜>")

print("=" * 100)
print("A. 逐条消息扫描：DSML / 宽松标记 / 关键子串")
print("=" * 100)
for conv in convs:
    msgs = conv.get("Messages") or conv.get("messages") or []
    print("\n会话 %r (%d 条)" % (conv.get("Title"), len(msgs)))
    for i, m in enumerate(msgs):
        role = m.get("Role")
        c = m.get("Content") or ""
        hits = set()
        if DSML_PAT.search(c):
            hits.add("DSML")
        loose = LOOSE.findall(c)
        if loose:
            hits.add("loose=%s" % (loose[:4],))
        for kw in ("invoke", "calls>", "parameter ", 'string="true"', "function "):
            if kw in c:
                hits.add("kw:%s" % kw)
        if hits:
            print("  [%d] %-9s len=%-6d %s" % (i, role, len(c), sorted(hits)))
            # 打印命中位置附近的上下文
            for m_ in re.finditer(r"DSML|｜|</\s*(invoke|calls|parameter)|string=\"true\"", c):
                s = max(0, m_.start() - 60)
                print("        @%d: %r" % (m_.start(), c[s:m_.end() + 60]))

print()
print("=" * 100)
print("B. assistant 正文「头部缺失」现象：正文前 40 字符")
print("=" * 100)
for conv in convs:
    msgs = conv.get("Messages") or conv.get("messages") or []
    for i, m in enumerate(msgs):
        if m.get("Role") != "assistant":
            continue
        c = m.get("Content") or ""
        if not c:
            continue
        print("  [%2d] %r" % (i, c[:40]))
        # 首个字符的码位
        if c:
            print("        首字符 U+%04X %s | 第2字符 U+%04X"
                  % (ord(c[0]), unicodedata.name(c[0], "?"),
                     ord(c[1]) if len(c) > 1 else 0))

print()
print("=" * 100)
print("C. DSML 标记的精确码位")
print("=" * 100)
for conv in convs:
    msgs = conv.get("Messages") or conv.get("messages") or []
    for i, m in enumerate(msgs):
        c = m.get("Content") or ""
        if "DSML" not in c:
            continue
        print("消息[%d] 前 80 字符码位:" % i)
        for ch in c[:60]:
            print("   U+%04X %-28s %r" % (ord(ch), unicodedata.name(ch, "?"), ch))
        break
    break

print()
print("=" * 100)
print("D. 用户消息（28KB）里是否含任何标记/属性样式文本")
print("=" * 100)
conv = load = convs[0]
for conv in convs:
    msgs = conv.get("Messages") or conv.get("messages") or []
    if msgs and (msgs[0].get("Content") or "")[:20].find("按照") >= 0:
        pass
    for i, m in enumerate(msgs):
        if m.get("Role") != "user":
            continue
        c = m.get("Content") or ""
        print("  user[%d] len=%d  DSML=%s  含'｜'=%s  含'\\uff5c'=%s  含'name=\"'=%s"
              % (i, len(c), "DSML" in c, "｜" in c, "�" in c, 'name="' in c))
        if i == 0 and len(c) > 500:
            print("      前 300 字: %r" % c[:300])
            print("      后 300 字: %r" % c[-300:])
