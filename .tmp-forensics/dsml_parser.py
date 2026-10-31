# -*- coding: utf-8 -*-
"""取证 10：DSML 兜底解析器的可行性验证。

依据（公开资料）：
  DeepSeek V4/V3.2 起，模型原生地用 DSML(DeepSeek Markup Language) 表示工具调用：
      <|DSML|tool_calls>
      <|DSML|invoke name="get_weather">
      <|DSML|parameter name="location" string="true">Beijing</|DSML|parameter>
      </|DSML|invoke>
      </|DSML|tool_calls>
  标签用全角竖线 ｜(U+FF5C)，也有 ASCII 竖线 | 的变体。
  多个 OpenAI 兼容宿主会把 DSML 泄漏进可见 content 而不是返回结构化 tool_calls。

本脚本：
  1) 用真实会话里抓到的两段 DSML 原文做解析，验证能否还原成 (工具名, 参数JSON)
  2) 用合成样本覆盖各种变体（全角/ASCII、string=true/false、多个 invoke、CDATA、缺标签）
"""
import json, io, re, sys

sys.stdout.reconfigure(encoding="utf-8")

# ---------------------------------------------------------------- 解析器
# 标签名可能是 tool_calls / function_calls / calls（真实样本里就出现过 calls）
_BLOCK_RE = re.compile(
    r"<[|｜]DSML[|｜](?P<block>tool_calls|function_calls|calls)\s*>(?P<body>.*?)</[|｜]DSML[|｜](?P=block)\s*>",
    re.DOTALL | re.IGNORECASE)
_INVOKE_RE = re.compile(
    r"<[|｜]DSML[|｜]invoke\s+(?P<attrs>[^>]*?)\s*>(?P<body>.*?)</[|｜]DSML[|｜]invoke\s*>",
    re.DOTALL | re.IGNORECASE)
_PARAM_RE = re.compile(
    r"<[|｜]DSML[|｜]parameter\s+(?P<attrs>[^>]*?)\s*>(?P<val>.*?)</[|｜]DSML[|｜]parameter\s*>",
    re.DOTALL | re.IGNORECASE)
_ATTR_RE = re.compile(r'([A-Za-z_][\w-]*)\s*=\s*"([^"]*)"')

# 关闭标签单独出现（真实样本：只有 </｜DSML｜ invoke> </｜DSML｜ calls>）
_CLOSE_ONLY_RE = re.compile(
    r"</[|｜]DSML[|｜](?P<tag>tool_calls|function_calls|calls|invoke|parameter)\s*>")


def _attrs(s):
    return dict(_ATTR_RE.findall(s or ""))


def parse_dsml(text):
    """从正文里解析出 DSML 工具调用。返回 [{name, arguments_json, raw}]。"""
    if not text:
        return []
    out = []
    blocks = list(_BLOCK_RE.finditer(text))
    if blocks:
        for b in blocks:
            for inv in _INVOKE_RE.finditer(b.group("body")):
                out.append(_invoke_to_call(inv.group("attrs"), inv.group("body")))
    else:
        # 没有完整外层信封：直接找 invoke 块（真实样本 msg[14] 就是这种残缺形态）
        for inv in _INVOKE_RE.finditer(text):
            out.append(_invoke_to_call(inv.group("attrs"), inv.group("body")))
    return [c for c in out if c]


def _invoke_to_call(attrs_s, body):
    a = _attrs(attrs_s)
    name = a.get("name", "").strip()
    params = {}
    for pm in _PARAM_RE.finditer(body):
        pa = _attrs(pm.group("attrs"))
        pname = pa.get("name", "").strip()
        if not pname:
            continue
        val = pm.group("val")
        if pa.get("string", "true").lower() == "false":
            # 非字符串：值按 JSON 解析
            try:
                params[pname] = json.loads(val)
            except Exception:
                params[pname] = val
        else:
            params[pname] = val
    args = json.dumps(params, ensure_ascii=False) if params else "{}"
    return {"name": name, "arguments_json": args, "params": params}


def strip_dsml(text):
    """把 DSML 标记从可见正文里剥掉（避免污染 UI）。"""
    if not text:
        return text
    out = _BLOCK_RE.sub("", text)
    out = re.sub(r"<[|｜]DSML[|｜]invoke\s+[^>]*?>", "", out)
    out = re.sub(r"<[|｜]DSML[|｜]parameter\s+[^>]*?>", "", out)
    out = _CLOSE_ONLY_RE.sub("", out)
    # 清理残留的孤立竖线标记
    out = re.sub(r"[｜|]DSML[｜|]", "", out)
    return out.strip()


# ---------------------------------------------------------------- 真实样本验证
REAL = [
    # 会话 msg[14]：只有残缺的收尾标签，没有 invoke 头 → 无法还原工具名
    "｜DSML｜ parameter>\n</｜DSML｜ invoke>\n</｜DSML｜ calls>",
    # 会话 msg[22]：完整 parameter 块 + 收尾标签，**缺 invoke 头**（工具名丢失）
    '｜DSML｜ parameter name="code" string="true">\n'
    'import zipfile, re, os, sys\n'
    'sys.stdout.reconfigure(encoding=\'utf-8\')\n'
    'p = "_conversations/20261031-091758_f9be"\n'
    'print("hi")\n'
    '</｜DSML｜ parameter>\n'
    '</｜DSML｜ invoke>\n'
    '</｜DSML｜ calls>',
]

print("=" * 100)
print("A. 真实 DSML 样本的解析结果")
print("=" * 100)
for i, s in enumerate(REAL):
    calls = parse_dsml(s)
    print("\n样本[%d]: %r" % (i, s[:70].replace("\n", "\\n")))
    print("  parse_dsml -> %s" % json.dumps(calls, ensure_ascii=False))
    print("  strip_dsml -> %r" % strip_dsml(s))

print()
print("=" * 100)
print("B. 完整 DSML（规范形态）解析验证")
print("=" * 100)
SYNTH = [
    ("全角竖线 + string=true",
     '<｜DSML｜tool_calls>\n'
     '<｜DSML｜invoke name="run_python">\n'
     '<｜DSML｜parameter name="code" string="true">print(1)\n</｜DSML｜parameter>\n'
     '</｜DSML｜invoke>\n'
     '</｜DSML｜tool_calls>'),
    ("ASCII 竖线变体",
     '<|DSML|tool_calls>\n'
     '<|DSML|invoke name="read_file">\n'
     '<|DSML|parameter name="path" string="true">a.txt</|DSML|parameter>\n'
     '</|DSML|invoke>\n'
     '</|DSML|tool_calls>'),
    ("多个 invoke",
     '<｜DSML｜calls>\n'
     '<｜DSML｜invoke name="list_files"><｜DSML｜parameter name="path" string="true"></｜DSML｜parameter></｜DSML｜invoke>\n'
     '<｜DSML｜invoke name="read_file"><｜DSML｜parameter name="path" string="true">x.docx</｜DSML｜parameter></｜DSML｜invoke>\n'
     '</｜DSML｜calls>'),
    ("string=false（JSON 值）",
     '<｜DSML｜tool_calls><｜DSML｜invoke name="write_xlsx">'
     '<｜DSML｜parameter name="sheets" string="false">{"成绩":[{"姓名":"张三"}]}</｜DSML｜parameter>'
     '<｜DSML｜parameter name="path" string="true">a.xlsx</｜DSML｜parameter>'
     '</｜DSML｜invoke></｜DSML｜tool_calls>'),
    ("正文 + DSML 混排",
     '我先看看工作区。\n<｜DSML｜tool_calls><｜DSML｜invoke name="list_files">'
     '<｜DSML｜parameter name="path" string="true"></｜DSML｜parameter>'
     '</｜DSML｜invoke></｜DSML｜tool_calls>'),
    ("带 CDATA",
     '<｜DSML｜tool_calls><｜DSML｜invoke name="run_python">'
     '<｜DSML｜parameter name="code" string="true"><![CDATA[print("x")]]></｜DSML｜parameter>'
     '</｜DSML｜invoke></｜DSML｜tool_calls>'),
]
for label, s in SYNTH:
    calls = parse_dsml(s)
    print("\n%s:" % label)
    print("  -> %s" % json.dumps(calls, ensure_ascii=False))
    print("  strip -> %r" % strip_dsml(s))

print()
print("=" * 100)
print("C. 关键判定：真实残缺样本能否还原出工具名？")
print("=" * 100)
for i, s in enumerate(REAL):
    calls = parse_dsml(s)
    has_name = bool(calls and calls[0].get("name"))
    print("  样本[%d]: 解析出调用=%d, 能拿到工具名=%s" % (i, len(calls), has_name))
print()
print("  → 结论：真实泄漏样本里 **invoke 头（name=...）被网关吞掉了**，")
print("    客户端只能还原 parameter，无法确定是哪个工具。")
