# -*- coding: utf-8 -*-
"""取证 11：统计「assistant 正文头部是否被损坏」，并抓原始 SSE 定位发生在哪一侧。

对每种历史形态发 N 次请求，检查 content 的开头是否合理：
  - 期望中文/英文/换行开头
  - 出现乱码前缀（如 @@?#、ação、ige）即为「头部损坏」
同时落盘原始 SSE，便于逐字节核对。
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import build_request_body, conv_to_history, load_real_conversation, raw_sse, parse_sse, KEYS

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"
os.makedirs(OUT, exist_ok=True)

MODE = sys.argv[1]
N = int(sys.argv[2]) if len(sys.argv) > 2 else 8

conv = load_real_conversation("按照md文件修改这份政治试卷")
hist_all = conv_to_history(conv)

if MODE == "text":
    # 纯文本输出（明确不要调工具），看 content 头部完整性
    hist = [{"role": "user", "content":
             "请只输出文字、不要调用任何工具。用中文写一段 200 字左右的说明，"
             "第一句必须以「本文档」三个字开头。"}]
elif MODE == "tool":
    # 需要调工具的场景
    hist = [{"role": "user", "content": "请用 list_files 看一下工作区里有什么。"}]
elif MODE == "binary":
    # 上下文里塞入含 U+FFFD 的二进制乱码（复现真实会话 tool[5] 的污染）
    junk = "PK\u0003\u0004\u0014\u0000\u0008\u0008\u0008\u0000%{P]\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u000f\u0000\u0009\u0000xl/workbook.xmlUT\u0005\u0000\u0001\ufffdA\ufffdj\ufffd\ufffdOn\ufffd:\u0010\ufffd\ufffd\ufffd\u0014\ufffd\ufffdm\ufffdN\ufffdg\u001b\ufffd\ufffd\u0509\ufffd\u0000\ufffd\u0013$i\ufffd\ufffd\ufffd\ufffd\u0146"
    hist = [
        {"role": "user", "content": "读一下这个文件：" + junk},
        {"role": "assistant", "content": "我先读文件。",
         "tool_calls": [{"id": "chatcmpl-tool-x1", "name": "read_file",
                         "arguments": "{\"path\": \"a.xlsx\"}"}]},
        {"role": "tool", "tool_call_id": "chatcmpl-tool-x1", "content": junk},
    ]
else:
    raise SystemExit("unknown mode")

stats = {"n": 0, "bad_head": 0, "dsml": 0, "weird": 0, "empty": 0}
VALID = {"list_files", "read_file", "write_file", "write_xlsx", "write_docx",
         "write_pptx", "search_tool", "install_tool", "run_python", "run_node",
         "web_fetch", "create_directory"}

for i in range(N):
    body = build_request_body(hist)
    status, reason, hdrs, lines = raw_sse(body, key=KEYS[i % 2], timeout=240)
    if status != 200:
        print("run %d: HTTP %s %s" % (i, status, reason)); time.sleep(4); continue
    with io.open(OUT + "/head_%s_%02d.txt" % (MODE, i), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    res = parse_sse(lines)
    head = res["content"][:40]
    dsml = "DSML" in res["content"]
    weird = [t["name"] for t in res["tool_calls"] if t["name"] not in VALID]
    stats["n"] += 1
    stats["dsml"] += int(dsml)
    stats["weird"] += int(bool(weird))
    if not res["content"]:
        stats["empty"] += 1
    print("run %2d: finish=%-11s head=%r%s%s"
          % (i, res["finish_reason"], head,
             "  DSML!" if dsml else "", "  WEIRD:%s" % weird if weird else ""))
    if dsml:
        for l in lines:
            if "DSML" in l:
                print("      原始行: %s" % l[:300])
                break
    time.sleep(2)

print()
print("汇总 %s: %s" % (MODE, stats))
