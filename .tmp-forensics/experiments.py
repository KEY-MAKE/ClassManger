# -*- coding: utf-8 -*-
"""取证 5：多项实验。

x_name : 审计网关是否会把 function.name 拆成多片 / 多次下发（验证「后覆盖前」是否是真 bug）
x_null : assistant.content = ""  vs  null 的 A/B（网关接受性 + 行为差异）
x_dsml : 历史里注入 DSML 文本 → 自我强化循环验证
x_long : 长上下文（无 DSML）→ 单纯上下文长度是否触发退化
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import (build_request_body, conv_to_history, load_real_conversation,
                    raw_sse, parse_sse, report, KEYS)

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"
os.makedirs(OUT, exist_ok=True)

MODE = sys.argv[1]
KEY = KEYS[1] if MODE in ("x_name", "x_dsml") else KEYS[0]

conv = load_real_conversation("按照md文件修改这份政治试卷")
hist_all = conv_to_history(conv)


def run(body, tag):
    status, reason, hdrs, lines = raw_sse(body, key=KEY)
    print("### %s  HTTP %s %s" % (tag, status, reason))
    if status != 200:
        print("  响应: %s" % ("".join(lines)[:600]))
        return None
    with io.open(OUT + "/sse_%s.txt" % tag, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    res = parse_sse(lines)
    report(res, tag)
    return lines, res


if MODE == "x_name":
    # 连打若干次「需要调工具」的请求，审计每个 index 的 function.name 出现次数与取值
    for n in range(6):
        body = build_request_body([
            {"role": "user", "content": "请用 run_python 工具执行：print(1)"},
        ])
        status, reason, hdrs, lines = raw_sse(body, key=KEY)
        if status != 200:
            print("run %d: HTTP %s" % (n, status))
            time.sleep(2)
            continue
        name_chunks = []
        for line in lines:
            if not line.startswith("data:"):
                continue
            p = line[5:].strip()
            if not p or p == "[DONE]":
                continue
            try:
                node = json.loads(p)
            except Exception:
                continue
            for ch in node.get("choices") or []:
                for tc in (ch.get("delta") or {}).get("tool_calls") or []:
                    fn = tc.get("function") or {}
                    if "name" in fn:
                        name_chunks.append((tc.get("index"), fn["name"]))
        print("run %d: name 分片序列 = %s" % (n, name_chunks))
        if len(name_chunks) > 1:
            print("   !!! 同一请求里 name 出现 %d 次" % len(name_chunks))
        time.sleep(1)

elif MODE == "x_null":
    # A/B：相同历史，assistant.content = ""  vs  null
    for tag, nullify in (("ab_empty", False), ("ab_null", True)):
        h = []
        for m in hist_all[:6]:
            m2 = dict(m)
            if nullify and m2["role"] == "assistant" and m2.get("tool_calls"):
                m2["content"] = None
            h.append(m2)
        body = build_request_body(h)
        r = run(body, tag)
        if r:
            lines, res = r
            print("  → content 用 %s" % ("null" if nullify else '""'))

elif MODE == "x_dsml":
    # 历史里注入一条含 DSML 的 assistant 消息，看模型是否模仿
    dsml = ("｜DSML｜ calls>\n｜DSML｜ invoke name=\"run_python\" description=\"执行python\" "
            "parameter name=\"code\" string=\"true\">\nprint('hi')\n"
            "｜DSML｜ parameter>\n｜DSML｜ invoke>\n｜DSML｜ calls>")
    for tag, inject in (("dsml_off", False), ("dsml_on", True)):
        h = list(hist_all[:8])
        if inject:
            h.insert(7, {"role": "assistant", "content": dsml})
        body = build_request_body(h)
        r = run(body, tag)
        if r:
            lines, res = r
            print("  → 历史含 DSML: %s" % inject)
            print("  → 模型是否吐 DSML: %s" % ("DSML" in res["content"]))

elif MODE == "x_long":
    # 纯长度压力，无 DSML
    filler = "这是用于测试长上下文的中文填充文本，重复很多次。" * 700
    for tag, nt in (("long_40k", 40000), ("long_90k", 90000)):
        h = [{"role": "user", "content": filler * (nt // 1000)}]
        body = build_request_body(h)
        r = run(body, tag)
        if r:
            lines, res = r
            print("  → 长度 %d 字符，是否吐 DSML: %s" % (len(filler) * (nt // 1000),
                                                 "DSML" in res["content"]))

else:
    raise SystemExit("unknown mode")
