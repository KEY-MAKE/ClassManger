# -*- coding: utf-8 -*-
"""取证 12：DSML 泄漏的触发条件 —— 参数值里含双引号 / 花括号 / 特殊字符。

假说：网关的 DSML→OpenAI 转换器用朴素正则提取 parameter 值
      （例如 string="true">([^"]*)），当参数值本身含 " 时截断 → 转换失败 →
      原始 DSML 泄漏进 content。

对照组：
  q_none  参数值不含任何双引号
  q_quote 参数值含双引号
  q_brace 参数值含花括号/方括号
  q_raw   参数值含引号 + 换行 + 缩进（最接近真实会话 msg[22]）
"""
import json, io, os, sys, time

sys.path.insert(0, r"D:/信息技术/ClassManager/.tmp-forensics")
from common import build_request_body, raw_sse, parse_sse, KEYS

sys.stdout.reconfigure(encoding="utf-8")
OUT = r"D:/信息技术/ClassManager/.tmp-forensics/out"
os.makedirs(OUT, exist_ok=True)

MODE = sys.argv[1]
N = int(sys.argv[2]) if len(sys.argv) > 2 else 6

CODES = {
    "q_none": "import os\nprint(os.getcwd())\n",
    "q_quote": 'import os\nprint("hello world")\n',
    "q_brace": "d = {'a': [1, 2, 3]}\nprint(d)\n",
    "q_raw": 'import os, sys\nsys.stdout.reconfigure(encoding="utf-8")\np = "_conversations/x"\nfor i in range(3):\n    print(f"[{i}] " + os.path.basename(p))\n',
}

code = CODES[MODE]
prompt = ("请调用 run_python 工具执行下面这段脚本，务必用 run_python 工具，不要手写结果：\n```python\n"
          + code + "```")

VALID = {"list_files", "read_file", "write_file", "write_xlsx", "write_docx",
         "write_pptx", "search_tool", "install_tool", "run_python", "run_node",
         "web_fetch", "create_directory"}

stats = {"n": 0, "dsml": 0, "weird": 0, "ok_tool": 0, "length": 0}
for i in range(N):
    body = build_request_body([{"role": "user", "content": prompt}])
    status, reason, hdrs, lines = raw_sse(body, key=KEYS[i % 2], timeout=240)
    if status != 200:
        print("run %d: HTTP %s %s" % (i, status, reason)); time.sleep(4); continue
    with io.open(OUT + "/q_%s_%02d.txt" % (MODE, i), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))

    res = parse_sse(lines)
    dsml = "DSML" in res["content"]
    weird = [t["name"] for t in res["tool_calls"] if t["name"] not in VALID]
    names = [t["name"] for t in res["tool_calls"]]
    stats["n"] += 1
    stats["dsml"] += int(dsml)
    stats["weird"] += int(bool(weird))
    stats["length"] += int(res["finish_reason"] == "length")
    stats["ok_tool"] += int(bool(names) and not weird)
    print("run %2d: finish=%-11s names=%-22s dsml=%-5s weird=%s"
          % (i, res["finish_reason"], names, dsml, weird))
    if dsml:
        print("      content=%r" % res["content"][:200])
        for l in lines:
            if "DSML" in l:
                print("      原始行: %s" % l[:260]); break
    if weird:
        for l in lines:
            if '"tool_calls"' in l and '"function"' in l:
                print("      原始 tool_calls 行: %s" % l[:260]); break
    time.sleep(2)

print()
print("汇总 %s: %s" % (MODE, stats))
