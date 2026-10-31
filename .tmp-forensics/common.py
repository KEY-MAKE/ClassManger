# -*- coding: utf-8 -*-
"""取证公共库：网关客户端 + BuildRequestBody 的等价复刻。

只读调研用；不触碰 ClassManager.App / ClassManager.Tests。
"""
import json, io, os, sys, time, http.client, urllib.parse

sys.stdout.reconfigure(encoding="utf-8")

BASE_URL = "https://discovery-api.intern-ai.org.cn/v1"
MODEL = "deepseek-v4.1-flash-1017"
KEYS = [
    "sk-a09fe4f6da318f196cb8517ece1f5c80c9044f83d51c0471e4a01ff996bb23b3",
    "sk-e35000920936c32f2954d9c02231cf1007a5ab2be65b1b2c6a299de7e24015d6",
]
CONV_PATH = r"C:/Users/admin/AppData/Roaming/ClassManager/ai-conversations.json"
WORKSPACE = r"C:\Users\admin\Documents\ClassManager AI"

# ---------------------------------------------------------------- system prompt
# 逐字复制自 ClassManager.App/Services/AiChatService.cs:214-248 (C# 原始字符串字面量，
# 公共缩进 16 空格被编译器剥离，故每行无前导空格)。
SYSTEM_PROMPT = """你是一个运行在「班级综合管理系统」里的本地 AI 助手，可以真正动手做事，不只是聊天。

工作区：{ws}
（所有相对路径都相对于这个目录；需要时请先用 list_files 看看里面有什么）

你可以使用的工具：
- list_files：列出目录内容
- read_file：读取文件。支持文本/代码/CSV/JSON、Excel(xlsx/xls)、Word(docx)、PPT(pptx)；
  图片与其它二进制会被明确拒绝并说明原因（不要重复尝试，直接向用户说明）
- write_file：写任意文本文件（txt/md/json/xml/yaml/csv/代码文件等）
- write_xlsx：生成 Excel（参数 sheets 是「表名 → 行数组」的对象）
- write_docx：生成 Word（内容用 Markdown 写）
- write_pptx：生成 PPT（内容用 Markdown 写，每行 # 标题一页）
- create_directory：创建目录（可以一次建多级）
- web_fetch：抓取网页正文
- search_tool：联网检索可用的库/工具（查 PyPI + 网页搜索）
- install_tool：把检索到的包安装到工作区 _tools/（会先请用户确认）
- run_python：执行一段 Python 脚本（可以 import 刚装好的包）
- run_node：执行一段 Node.js 脚本（可以 require 刚装好的包）

当现有工具做不到用户的要求时（缺库、缺能力、缺格式支持），按这个顺序自我扩展：
1. search_tool 检索能用的包（别凭空编包名，先查）；
2. install_tool 安装（用户拒绝就换个方案或直接问用户，不要反复重试）；
3. run_python / run_node 用装好的工具把活干完；
4. 只从 PyPI / npm 官方源安装，不要去装来路不明的脚本。

工作方式：
1. 需要动手就调工具，不要只描述步骤。
2. 输出文件前先把目录建好（create_directory），再写文件。
3. 生成 Word/PPT 时用 Markdown：# 是标题，- 是列表条目。
4. 工具返回「用户拒绝」时不要重试同一个操作，也不要直接结束任务；
   要向用户说明你需要什么才能继续（换目录？让他点允许？），然后等用户回复。
5. 最后用几句话说清你做了什么、产物在哪。""".format(ws=WORKSPACE)

# ---------------------------------------------------------------- tool specs
# 逐字复制自 ClassManager.App/Services/AiFileTools.cs:39-119
# ParametersJson 未显式给出的，走 AiChatService.cs:35 的默认值
DEFAULT_PARAMS = '{"type":"object","properties":{}}'

TOOL_SPECS = [
    ("list_files",
     "列出某个目录下的文件与子目录。path 可为相对工作区的路径或绝对路径。",
     '{"type":"object","properties":{"path":{"type":"string","description":"目录路径，留空表示工作区根目录"}},"required":["path"]}'),
    ("read_file",
     "读取文件内容。支持：文本/代码/CSV/JSON（纯文本）、Excel（xlsx/xls，渲染成文本）、Word（docx）、PPT（pptx)。"
     "图片与其它二进制文件会明确拒绝并说明原因，绝不会返回乱码——遇到这种提示时请向用户索取文本版本。",
     '{"type":"object","properties":{"path":{"type":"string","description":"文件路径"}},"required":["path"]}'),
    ("write_file",
     "写入/覆盖一个文本文件（父目录不存在会自动创建）。修改用户文件前需用户确认。",
     '{"type":"object","properties":{"path":{"type":"string","description":"文件路径"},"content":{"type":"string","description":"完整的新内容"}},"required":["path","content"]}'),
    ("write_xlsx",
     "生成 Excel 工作簿（.xlsx）。参数：path 输出路径；sheets 对象，形如 {\"成绩\":[{\"姓名\":\"张三\",\"分数\":90}]}，第一行自动作为表头。",
     None),
    ("write_docx",
     "生成 Word 文档（.docx）。参数：path 输出路径；content 用 Markdown 写正文，# 标题、- 列表会自动转成标题与项目符号。",
     None),
    ("write_pptx",
     "生成 PPT（.pptx）。参数：path 输出路径；content 用 Markdown 写正文，每行 # 标题算一页、- 列表是该页的要点。",
     None),
    ("search_tool",
     "联网检索可用的工具/库。当现有工具做不了用户的要求时，先用它找出合适的包。参数：query 关键词或 PyPI 包名。返回 PyPI 包信息 + 网页结果摘要。",
     None),
    ("install_tool",
     "安装检索到的工具包（装到工作区 _tools/，不污染系统）。参数：package 包名；kind 可选 \"python\"（默认）或 \"node\"。安装前会请用户确认。",
     None),
    ("run_python",
     "执行一段 Python 脚本（可以用 import 使用刚装好的包）。参数：code 脚本内容。",
     None),
    ("run_node",
     "执行一段 Node.js 脚本（可以用 require 使用刚装好的包）。参数：code 脚本内容。",
     None),
    ("web_fetch",
     "联网抓取网址并返回正文文本（HTML 自动转纯文本）。参数：url 完整地址（http/https）。",
     None),
    ("create_directory",
     "新建一个目录（已存在则忽略）。",
     '{"type":"object","properties":{"path":{"type":"string","description":"目录路径"}},"required":["path"]}'),
]


def build_tools():
    """等价于 AiChatService.cs:307-326。"""
    out = []
    for name, desc, params in TOOL_SPECS:
        out.append({
            "type": "function",
            "function": {
                "name": name,
                "description": desc,
                "parameters": json.loads(params if params is not None else DEFAULT_PARAMS),
            },
        })
    return out


# ---------------------------------------------------------------- request body
def build_request_body(history, tools=None, max_tokens=4096, stream=True):
    """等价于 AiChatService.BuildRequestBody（AiChatService.cs:251-329）。

    history: [{role, content, tool_calls?, tool_call_id?}]
      - content 为 str 时序列化为 JSON 字符串（JsonObject 索引器接受 string -> JsonNode 隐式转换）
      - tool_calls[].function.arguments 为 str 时同样序列化为 JSON 字符串
    """
    if tools is None:
        tools = build_tools()

    messages = [{"role": "system", "content": SYSTEM_PROMPT}]
    for m in history:
        node = {"role": m["role"]}
        if m["role"] == "tool":
            node["tool_call_id"] = m.get("tool_call_id") or ""
            node["content"] = m.get("content", "")
        else:
            node["content"] = m.get("content", "")
            tcs = m.get("tool_calls")
            if tcs:
                node["tool_calls"] = [
                    {
                        "id": tc["id"],
                        "type": "function",
                        "function": {"name": tc["name"], "arguments": tc["arguments"]},
                    }
                    for tc in tcs
                ]
        messages.append(node)

    root = {
        "model": MODEL,
        "messages": messages,
        "max_tokens": max_tokens,
        "stream": stream,
    }
    if tools:
        root["tools"] = tools
        root["tool_choice"] = "auto"
    return root


def load_real_conversation(title_contains=None):
    with io.open(CONV_PATH, encoding="utf-8") as f:
        convs = json.load(f)
    if title_contains:
        for c in convs:
            if title_contains in (c.get("Title") or c.get("title") or ""):
                return c
        raise KeyError(title_contains)
    return convs[0]


def conv_to_history(conv, upto=None):
    """AiMessage 列表 -> BuildRequestBody 的 history 形状（保持 arguments 为 str）。"""
    msgs = conv.get("Messages") or conv.get("messages") or []
    if upto is not None:
        msgs = msgs[:upto]
    hist = []
    for m in msgs:
        role = m.get("Role")
        if role == "tool":
            hist.append({
                "role": "tool",
                "tool_call_id": m.get("ToolCallId") or "",
                "content": m.get("Content") or "",
            })
        else:
            item = {"role": role, "content": m.get("Content") or ""}
            tcs = m.get("ToolCalls")
            if tcs:
                item["tool_calls"] = [
                    {"id": tc.get("Id"), "name": tc.get("Name"), "arguments": tc.get("Arguments")}
                    for tc in tcs
                ]
            hist.append(item)
    return hist


# ---------------------------------------------------------------- raw SSE client
def raw_sse(body, key=None, timeout=180, endpoint="/v1/chat/completions"):
    """发请求并逐行返回原始 SSE 文本行（不做任何解析）。

    返回 (status, reason, headers_dict, [raw_lines])
    """
    key = key or KEYS[0]
    payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
    conn = http.client.HTTPSConnection("discovery-api.intern-ai.org.cn", timeout=timeout)
    headers = {
        "Authorization": "Bearer " + key,
        "Content-Type": "application/json",
        "Accept": "text/event-stream",
        "User-Agent": "ClassManager-Forensics/1.0",
    }
    conn.request("POST", endpoint, body=payload, headers=headers)
    resp = conn.getresponse()
    status, reason = resp.status, resp.reason
    hdrs = {k.lower(): v for k, v in resp.getheaders()}
    lines = []
    if resp.status == 200:
        buf = b""
        while True:
            chunk = resp.read(1)
            if not chunk:
                break
            buf += chunk
            while b"\n" in buf:
                raw, buf = buf.split(b"\n", 1)
                lines.append(raw.decode("utf-8", "replace"))
        if buf:
            lines.append(buf.decode("utf-8", "replace"))
    else:
        lines.append(resp.read().decode("utf-8", "replace"))
    conn.close()
    return status, reason, hdrs, lines


def parse_sse(lines):
    """等价于 AiChatService.TryOnceAsync 的 SSE 解析（AiChatService.cs:430-544）。"""
    content, reasoning = [], []
    tool_calls = {}   # index -> {"id","name","arguments"}
    usage = None
    finish_reason = None
    for line in lines:
        if not line.startswith("data:"):
            continue
        payload = line[5:].strip()
        if not payload or payload == "[DONE]":
            continue
        try:
            node = json.loads(payload)
        except Exception:
            continue
        if node.get("usage"):
            usage = node["usage"]
        choices = node.get("choices")
        if not choices:
            continue
        ch = choices[0]
        if ch.get("finish_reason"):
            finish_reason = ch["finish_reason"]
        delta = ch.get("delta")
        if delta is None:
            continue
        cd = delta.get("content")
        if cd:
            content.append(cd)
        rd = delta.get("reasoning_content")
        if rd:
            reasoning.append(rd)
        for item in delta.get("tool_calls") or []:
            idx = item.get("index", len(tool_calls))
            b = tool_calls.setdefault(idx, {"id": "", "name": "", "arguments": ""})
            if item.get("id"):
                b["id"] = item["id"]
            fn = item.get("function") or {}
            if fn.get("name"):
                b["name"] = fn["name"]          # <-- 注意：现有实现是「后覆盖前」
            if fn.get("arguments") is not None:
                b["arguments"] += fn["arguments"]
    return {
        "content": "".join(content),
        "reasoning": "".join(reasoning),
        "tool_calls": [tool_calls[k] for k in sorted(tool_calls)],
        "finish_reason": finish_reason,
        "usage": usage,
    }


def tool_call_name_first_wins(lines):
    """假设性修复版解析：name 只取第一个非空值（不后覆盖前）。"""
    tool_calls = {}
    for line in lines:
        if not line.startswith("data:"):
            continue
        payload = line[5:].strip()
        if not payload or payload == "[DONE]":
            continue
        try:
            node = json.loads(payload)
        except Exception:
            continue
        choices = node.get("choices")
        if not choices:
            continue
        delta = choices[0].get("delta")
        if not delta:
            continue
        for item in delta.get("tool_calls") or []:
            idx = item.get("index", len(tool_calls))
            b = tool_calls.setdefault(idx, {"id": "", "name": "", "arguments": ""})
            if item.get("id") and not b["id"]:
                b["id"] = item["id"]
            fn = item.get("function") or {}
            if fn.get("name") and not b["name"]:
                b["name"] = fn["name"]
            if fn.get("arguments") is not None:
                b["arguments"] += fn["arguments"]
    return [tool_calls[k] for k in sorted(tool_calls)]


def report(res, label=""):
    if label:
        print("---- %s ----" % label)
    print("finish_reason = %r" % res["finish_reason"])
    print("usage = %s" % json.dumps(res["usage"], ensure_ascii=False))
    print("tool_calls = %d" % len(res["tool_calls"]))
    for tc in res["tool_calls"]:
        print("   id=%s name=%r args_len=%d args_head=%r"
              % (tc["id"], tc["name"], len(tc["arguments"]), tc["arguments"][:120]))
    print("content(%d) 前 400 字 = %r" % (len(res["content"]), res["content"][:400]))
    print("has DSML = %s" % ("DSML" in res["content"]))
