# -*- coding: utf-8 -*-
"""取证 17：测试「工具描述里含裸 ASCII 双引号」是否会破坏网关的 DSML→tool_calls 转换。

依据：
  - 真实会话 msg[6] 的工具名被解析成 `description" string="true`，
    其中 `string="true` 正是 **DSML parameter 标签的属性**。
  - 我们的 write_xlsx 描述里含裸 ASCII 双引号：
      形如 {"成绩":[{"姓名":"张三","分数":90}]}
    如果网关把 tools 的 description 原样注入 DSML 格式的说明书，
    裸引号会破坏说明书结构 → 模型模仿出畸形 DSML → 解析出垃圾工具名 / 裸 DSML 泄漏。

对照组：
  plain_desc  描述不含裸引号
  quoted_desc 描述含裸引号（复刻 write_xlsx 的写法）
  empty_props 参数 schema 为空（复刻 run_python 的写法）
  full_props  参数 schema 完整
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import build_request_body, raw_sse, parse_sse, KEYS

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"

VARIANTS = {
    "plain_desc": {
        "description": "保存一条笔记。参数：note 笔记内容；tag 标签。",
        "parameters": {"type": "object", "properties": {
            "note": {"type": "string", "description": "笔记内容"},
            "tag": {"type": "string", "description": "标签"}}, "required": ["note"]},
    },
    "quoted_desc": {
        "description": "保存一条笔记。参数：note 笔记内容。形如 {\"成绩\":[{\"姓名\":\"张三\"}]}。",
        "parameters": {"type": "object", "properties": {
            "note": {"type": "string", "description": "笔记内容"}}, "required": ["note"]},
    },
    "empty_props": {
        "description": "执行一段 Python 脚本。参数：code 脚本内容。",
        "parameters": {"type": "object", "properties": {}},
    },
    "full_props": {
        "description": "执行一段 Python 脚本。参数：code 脚本内容。",
        "parameters": {"type": "object", "properties": {
            "code": {"type": "string", "description": "脚本内容"}}, "required": ["code"]},
    },
}

MODE = sys.argv[1]
N = int(sys.argv[2]) if len(sys.argv) > 2 else 8

TOOL_NAME = {"plain_desc": "save_note", "quoted_desc": "save_note",
             "empty_props": "run_script", "full_props": "run_script"}[MODE]
v = VARIANTS[MODE]

TOOLS = [{"type": "function", "function": {
    "name": TOOL_NAME, "description": v["description"], "parameters": v["parameters"]}}]

if TOOL_NAME == "save_note":
    ARG = "请调用 save_note 工具保存一条笔记，note 内容为 \"hello world\"，tag 为 \"test\"。"
    WANT = {"note", "tag"}
else:
    ARG = "请调用 run_script 工具执行脚本，code 内容为 print(1)。"
    WANT = {"code"}

body = {
    "model": "deepseek-v4.1-flash-1017",
    "messages": [
        {"role": "system", "content": "你是一个可以真正动手做事的本地 AI 助手。需要动手就调工具，不要只描述步骤。"},
        {"role": "user", "content": ARG},
    ],
    "max_tokens": 4096, "stream": True,
    "tools": TOOLS, "tool_choice": "auto",
}

agg = {"n": 0, "ok": 0, "dsml": 0, "weird": 0, "none": 0}
for i in range(N):
    status, reason, hdrs, lines = raw_sse(body, key=KEYS[i % 2], timeout=200)
    if status != 200:
        print("run %d: HTTP %s %s" % (i, status, reason)); time.sleep(4); continue
    with io.open(OUT + "/tool_%s_%02d.txt" % (MODE, i), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    res = parse_sse(lines)
    dsml = "DSML" in res["content"]
    names = [t["name"] for t in res["tool_calls"]]
    weird = [n for n in names if n != TOOL_NAME]
    agg["n"] += 1
    agg["ok"] += int(names == [TOOL_NAME])
    agg["dsml"] += int(dsml)
    agg["weird"] += int(bool(weird))
    agg["none"] += int(not names)
    print("%-12s run %2d: finish=%-11s names=%-30s dsml=%-5s args=%r"
          % (MODE, i, res["finish_reason"], names, dsml,
             (res["tool_calls"][0]["arguments"][:80] if res["tool_calls"] else "")))
    if dsml:
        print("      content=%r" % res["content"][:200])
    if weird:
        for l in lines:
            if '"tool_calls"' in l and '"function"' in l:
                print("      原始 tool_calls 行: %s" % l[:280]); break
    time.sleep(2)

print()
print("汇总 %s: %s" % (MODE, agg))
