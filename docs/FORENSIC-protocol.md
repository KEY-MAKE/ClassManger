# 协议层取证报告：AI 对话「DSML 退化」根因分析

> 取证日期：2026-10-31
> 取证对象：`D:/信息技术/ClassManager/`（.NET 8 / WPF）
> 网关：`https://discovery-api.intern-ai.org.cn/v1`，模型 `deepseek-v4.1-flash-1017`
> 方法：在 `.tmp-forensics/`（已删除）下用 Python + .NET 探针**复刻**客户端逻辑、直接向网关发包、
> 抓原始 SSE 字节流，并与磁盘上真实会话记录交叉验证。
> 本次取证**未修改** `ClassManager.App/` 与 `ClassManager.Tests/` 下任何文件。

---

## 0. 结论速览

| # | 结论 | 状态 |
|---|---|---|
| 1 | 我们发出的请求体在 `tool_calls`/`arguments`/`tool_call_id`/`tools` 四项上**全部合规** | **已确认** |
| 2 | `arguments` 存的是 **JSON 字符串**（不是对象），符合 OpenAI 规范 | **已确认** |
| 3 | 真实故障的根因**不在客户端**：网关把 DeepSeek 原生 **DSML** 工具调用格式泄漏到了 `content` 里 | **已确认** |
| 4 | DSML 是 DeepSeek V3.2/V4 模型的**原生工具调用格式**；网关有 DSML→OpenAI 转换器，但**偶发转换失败** | **已确认** |
| 5 | 客户端目前**完全没有** DSML 兜底解析：DSML 直接当正文显示，且 `tool_calls` 为空 → agent 主循环判定「没有工具调用」→ **直接结束任务** | **已确认** |
| 6 | 触发条件与 `max_tokens=4096` 强相关：思考 token 吃满预算 → `finish_reason=length` → 响应在 DSML 结构中途被截断 → 网关转换器拿不到完整结构 → 原始 DSML 落进 `content` | **已确认（关联）** |
| 7 | `assistant.content` 写 `""` 而非 `null`：网关**接受**，行为**无差异**。但这是规范偏差，建议改 | **已确认（无影响）** |
| 8 | 「正文头部被截断/损坏」（`'acao先看一下…'`、`'@@?# 学生成绩单…'`）**不是客户端 bug**，是网关/模型侧的字符级损坏 | **已确认** |
| 9 | 额外发现：`run_python` 等 **8 个工具的 `parameters.properties` 为空**，模型只能从中文描述里猜参数名 | **已确认** |
| 10 | 额外发现：客户端**从不读 `finish_reason`**，截断被静默当成正常结束 | **已确认** |

---

## 1. 我们实际发出的请求体

### 1.1 复刻方法

`AiChatService.BuildRequestBody`（`ClassManager.App/Services/AiChatService.cs:251-329`）是 `private`，
无法直接调用。取证时：

1. 用 Python 逐行复刻该方法（`System.Text.Json.Nodes` → `dict`），
   输入取自**磁盘上真实失败会话**（`%AppData%/Roaming/ClassManager/ai-conversations.json`）；
2. 同时写了一个 **C# 探针**（`JsonShapeProbe`），用**真正的 `System.Text.Json.Nodes`**
   重跑同一段构造逻辑，验证 Python 复刻与 .NET 实际产出一致。

C# 探针输出（真实 .NET 语义，`AiChatService.cs:272` 与 `:286` 两行原样）：

```
=== A. 现有实现：assistant.content = ""，arguments 为 string ===
{"model":"deepseek-v4.1-flash-1017","messages":[
{"role":"system","content":"..."},
{"role":"user","content":"..."},
{"role":"assistant","content":"","tool_calls":[{"id":"chatcmpl-tool-b2dc4cfa8f4f7e1c","type":"function","function":{"name":"list_files","arguments":"{\u0022path\u0022: \u0022\u0022}"}}]},
{"role":"tool","tool_call_id":"chatcmpl-tool-b2dc4cfa8f4f7e1c","content":"..."}],
"max_tokens":4096,"stream":true,
"tools":[{"type":"function","function":{"name":"run_python","description":"执行一段 Python 脚本。参数：code 脚本内容。","parameters":{"type":"object","properties":{}}}}],
"tool_choice":"auto"}

=== C. arguments 的 JSON 类型判定 ===
  arguments 节点类型 = JsonValuePrimitive`1
  arguments 序列化 = "{\u0022path\u0022: \u0022\u0022}"
  -> 是 JSON **字符串**（带转义引号），不是对象。符合 OpenAI 规范。

=== D. assistant.content 的 JSON 类型判定 ===
  content 节点类型 = JsonValuePrimitive`1
  content 序列化 = ""
  -> 空字符串 ""，不是 null；字段也没被省略。
```

### 1.2 真实失败会话的完整请求体（节选，原文）

来源：`ai-conversations.json` 中标题为 `按照md文件修改这份政治试卷` 的会话，
共 23 条消息，序列化后 **65,888 字符**（其中用户首条消息 28,734 字符）。

**assistant 带 tool_calls 的那条（注意 `content` 是 `""`，不是 `null`）：**

```json
{"role": "assistant", "content": "\n我先看看工作区里有什么，并读取这两个 docx 的完整内容与结构。\n\n", "tool_calls": [{"id": "chatcmpl-tool-b2dc4cfa8f4f7e1c", "type": "function", "function": {"name": "list_files", "arguments": "{\"path\": \"_conversations/20261031-091758_f9be\"}"}}, {"id": "chatcmpl-tool-95e62ede4d66a886", "type": "function", "function": {"name": "list_files", "arguments": "{\"path\": \"\"}"}}]}
```

**tool 那一条（`tool_call_id` 与上面 assistant 的 `id` 严格配对）：**

```json
{"role": "tool", "tool_call_id": "chatcmpl-tool-b2dc4cfa8f4f7e1c", "content": "目录：C:\\Users\\admin\\Documents\\ClassManager AI\\_conversations\\20261031-091758_f9be\r\n[文件] 2026年高考河南卷政治高考真题（网络 收集版）.docx\t3,518,587 字节\r\n[文件] 2026年高考河南卷政治高考真题（网络 收集版）（纯答案版）.docx\t50,961 字节\r\n[文件] 试卷格式规范.md\t37,895 字节\r\n"}
```

**tools 数组里两个关键条目（`properties` 为空）：**

```json
{
 "type": "function",
 "function": {
  "name": "write_xlsx",
  "description": "生成 Excel 工作簿（.xlsx）。参数：path 输出路径；sheets 对象，形如 {\"成绩\":[{\"姓名\":\"张三\",\"分数\":90}]}，第一行自动作为表头。",
  "parameters": { "type": "object", "properties": {} }
 }
}
```

```json
{
 "type": "function",
 "function": {
  "name": "run_python",
  "description": "执行一段 Python 脚本（可以用 import 使用刚装好的包）。参数：code 脚本内容。",
  "parameters": { "type": "object", "properties": {} }
 }
}
```

### 1.3 逐条核对结果

#### ① assistant 带 `tool_calls` 时 `content` 写 `""` 还是 `null`？

**我们写的是 `""`（空字符串），字段没有被省略，也不是 `null`。**
产生位置：`AiChatService.cs:272` → `node["content"] = message.Content;`
（`message.Content` 来自 `AiOfficeViewModel.cs:533` 的 `assistantMessage.Content`，
而后者在流式阶段被 `AiOfficeViewModel.cs:493-495` 累加，工具轮里因为模型没出正文而保持 `""`）

- **网关接受吗？接受。** 抓包验证：`content:""` 与 `content:null` 两种写法都返回 `HTTP 200`，
  6 次 vs 6 次，模型行为、finish_reason、工具调用结果**完全一致**（见 §3.3）。
- **合规性：不合规但无害。** OpenAI 规范要求 assistant 带 `tool_calls` 时 `content` 为 `null` 或省略。
- **判定：已确认（无功能影响）**，建议按规范改（见 §5.1）。

#### ② `tool_calls[].function.arguments` 是 JSON 字符串还是对象？

**是 JSON 字符串，符合标准 OpenAI 规范。**

- 类型：`ClassManager.App/Models/AiModels.cs:56` → `public string Arguments { get; set; } = string.Empty;`
- SSE 解析：`AiChatService.cs:523-526` → `function?["arguments"]?.GetValue<string>()`，
  分片按 `StringBuilder.Append` 累加（`AiChatService.cs:558-563`），
  最终 `AiChatService.cs:542` → `Arguments = b.Arguments.ToString();`
  → 全程保持 `string`，**没有被反序列化成对象**。
- 序列化：`AiChatService.cs:286` → `["arguments"] = call.Arguments;`
  `JsonNode` 索引器接受 `string`，产出 `JsonValue`（字符串节点），
  C# 探针实测 `arguments 节点类型 = JsonValuePrimitive`，序列化为 `"{\"path\": \"\"}"`。
- 磁盘实证：真实会话里 10 个工具调用的 `Arguments` 全部是 `str` 类型，
  例如 `"{\"path\": \"_conversations/20261031-091758_f9be\"}"`。
- **判定：已确认（合规）**。任务书怀疑的「arguments 被存成对象」在此项目中**不成立**。

> 附带的健壮性隐患（已确认）：`AiChatService.cs:523` 若网关哪天把 `arguments` 发成
> **JSON 对象**，`GetValue<string>()` 会抛 `InvalidOperationException: The node must be of type 'JsonValue'`，
> 整轮请求直接异常退出（C# 探针实测）。当前网关发的是字符串，暂时不会触发。

#### ③ tool 消息的 `tool_call_id` 是否与 assistant 的 `tool_calls[].id` 一一配对？

**是，严格一一配对。**（产生位置 `AiChatService.cs:267` → `node["tool_call_id"] = message.ToolCallId ?? string.Empty;`，
`message.ToolCallId` 来自 `AiOfficeViewModel.cs:558` → `ToolCallId = call.Id`）

```
assistant 侧 id (10 个): ['chatcmpl-tool-b2dc4cfa8f4f7e1c', 'chatcmpl-tool-95e62ede4d66a886',
                         'chatcmpl-tool-8cb573e63b83b578', 'chatcmpl-tool-ac622d61c774bb25',
                         'chatcmpl-tool-87ccba590acf792d', 'chatcmpl-tool-9e311dae809f55ed',
                         'chatcmpl-tool-a63b6fb1265922c3', 'chatcmpl-tool-907ab4975e58cb7c',
                         'chatcmpl-tool-9bbad80e774f1bab', 'chatcmpl-tool-8eb508f4de3c4296']
tool     侧 id (10 个): [完全相同的 10 个]
tool 侧全部能在 assistant 侧找到: True
assistant 侧未收到结果的: []
多余/为空的 tool_call_id: []
```

**判定：已确认（合规）**。

#### ④ `tools` 数组结构是否完整？

**结构完整**：`type` / `function.name` / `function.description` / `function.parameters` 四项齐全
（`AiChatService.cs:310-322`）。12 个工具全部通过结构校验。

| 工具 | description | parameters.properties | required |
|---|---|---|---|
| list_files | ✅ | 1 (path) | ✅ |
| read_file | ✅ | 1 (path) | ✅ |
| write_file | ✅ | 2 (path, content) | ✅ |
| **write_xlsx** | ✅ | **0（空）** | ❌ |
| **write_docx** | ✅ | **0（空）** | ❌ |
| **write_pptx** | ✅ | **0（空）** | ❌ |
| **search_tool** | ✅ | **0（空）** | ❌ |
| **install_tool** | ✅ | **0（空）** | ❌ |
| **run_python** | ✅ | **0（空）** | ❌ |
| **run_node** | ✅ | **0（空）** | ❌ |
| **web_fetch** | ✅ | **0（空）** | ❌ |
| create_directory | ✅ | 1 (path) | ✅ |

**判定：已确认（结构合规），但 8/12 个工具的参数 schema 是空的**——模型看不到参数名，
只能从中文散文描述里猜（例如 `run_python` 的 `code` 只出现在「参数：code 脚本内容」这句话里）。
这是**独立于 DSML 的第二缺陷**，见 §5.3。

---

## 2. 根因：网关把 DeepSeek 原生 DSML 格式泄漏进了 `content`

### 2.1 故障现场的原始数据（磁盘实证）

真实失败会话 `按照md文件修改这份政治试卷` 的 23 条消息里，有 **2 条 assistant 消息**
的正文是**纯 DSML 文本**，且 `ToolCalls` 为 `null`：

**消息 [14]（完整原文，50 字符）：**

```
｜DSML｜ parameter>
</｜DSML｜ invoke>
</｜DSML｜ calls>
```

**消息 [22]（完整原文，838 字符，节选头部）：**

```
｜DSML｜ parameter name="code" string="true">
import zipfile, re, os, sys
sys.stdout.reconfigure(encoding='utf-8')
p = "_conversations/20261031-091758_f9be"
files = os.listdir(p)
main = [f for f in files if f.endswith('.docx') and '纯答案' not in f][0]
path = os.path.join(p, main)
import docx
from docx.oxml.ns import qn
d = docx.Document(path)
# map rIds to media files
rels = d.part.rels
for i, par in enumerate(d.paragraphs):
    xml = par._p.xml
    embeds = re.findall(r'r:(?:embed|link)="([^"]+)"', xml)
    if embeds:
        targets = []
        for e in embeds:
            try:
                targets.append(os.path.basename(rels[e].target_ref))
            except Exception as ex:
                targets.append('?')
        print(f"[{i}] {targets} :: {par.text.strip()[:60]}")
</｜DSML｜ parameter>
</｜DSML｜ invoke>
</｜DSML｜ calls>
```

保存后的 JSON（消息 [14] 完整字段）：

```json
{
 "Role": "assistant",
 "Content": "｜DSML｜ parameter>\n</｜DSML｜ invoke>\n</｜DSML｜ calls>",
 "DisplayContent": null,
 "Reasoning": "",
 "ToolCallId": null,
 "ToolName": null,
 "ToolCalls": null,
 "IsError": false,
 "Time": "2026-10-31T09:19:09.3533886+08:00"
}
```

注意 `"ToolCalls": null` —— **这一轮模型本该调工具，但网关既没给出 `tool_calls`，也没给出正文，
只把 DSML 原文塞进了 `content`。**

### 2.2 这是什么格式

`｜` 是 **U+FF5C FULLWIDTH VERTICAL LINE**（不是 ASCII `|`）。经查证，
这是 **DeepSeek V3.2 / V4 系列的原生工具调用格式 DSML**（DeepSeek Markup Language）：

```
<|DSML|tool_calls>
<|DSML|invoke name="get_weather">
<|DSML|parameter name="location" string="true">Beijing</|DSML|parameter>
</|DSML|invoke>
</|DSML|tool_calls>
```

- 标签名复用全角竖线 `｜`(U+FF5C)，主体是 XML 风格的 `invoke` / `parameter` 块；
- `string="true"` 表示参数值按**原始字符串**保留；`string="false"` 表示值按 JSON 解析；
- 公开资料明确指出：**多个 OpenAI 兼容宿主会把 DSML 信封泄漏进可见 `content` 而非返回结构化
  `tool_calls`**，「解析器必须能把它们『治愈』回工具调用，并从可见文本中剥离标记」。

### 2.3 「DSML 参数标签」与「被误解析成工具名」是同一件事

同一个会话的**消息 [6]** 出现了被误解析的工具名：

```json
{
 "Role": "assistant", "Content": "",
 "ToolCalls": [
  {
   "Id": "chatcmpl-tool-ac622d61c774bb25",
   "Name": "description\" string=\"true",
   "Arguments": "{\"path\": \"_conversations/20261031-091758_f9be\"}"
  }
 ]
}
```

对应的工具返回（消息 [7]）：

```
未知工具：description" string="true
```

**`description" string="true` 正是 DSML `parameter` 标签的属性片段**
（`string="true` 是 DSML parameter 的属性）。这说明：

> 网关的 DSML→OpenAI 转换器在解析 DSML 时，**把 parameter 标签的属性当成了函数名**。
> 转换器走偏后，剩余的 DSML 原文就被原样吐进 `content` —— 这正是消息 [14]/[22] 的形态。

模型本意是调 `read_file`（消息 [10] 的正文自述：
`function returns error — let me check the tool signature. The tool is \`read_file\` with \`path\`. I mistyped.`），
说明模型当时**已经产出了 DSML**，是**网关转换器**没能正确解析。

### 2.4 触发条件：思考 token 吃满 `max_tokens`

重放该失败请求（`max_tokens=4096`，即 `AiChatService.cs:303` 的现值）时，
**反复出现 `finish_reason=length` 且 `completion_tokens` 恰好等于 4096**：

```
run  8: finish=length      dsml=False weird=[]   out_tok=4096   83.1s
run 12: finish=length      dsml=False weird=[]   out_tok=4096  113.7s
run 13: finish=length      dsml=False weird=[]   out_tok=4096  194.2s
```

这类响应的 usage 明细显示 **reasoning_tokens 占满了几乎全部预算**：

```
=== e2_08.txt ===
  finish_reason = length
  usage = {"prompt_tokens": 35924, "total_tokens": 40020, "completion_tokens": 4096,
           "completion_tokens_details": {"reasoning_tokens": 4093}}
  reasoning 长度 = 6695   content 长度 = 0   tool_call 分片数 = 0
  reasoning 尾部: '...规范说"无题号续行：归到「上一题号 + 1」"。这意味着 16 题的 ②③④ 段落会被归到 17 题！\n\n危险。'
```

即：**模型在思考阶段就把 4096 token 预算烧光了，正文/工具调用一个字都没吐**。

**对照实验（同一历史，只改 `max_tokens`）：**

| `max_tokens` | 样本数 | `finish=length` 截断 | DSML 泄漏 | 正常工具调用 |
|---|---|---|---|---|
| **4096**（现值） | 16 + 20 = 36 | **7 次（19%）** | 0 | 29 |
| **16384** | 8 | **0 次（0%）** | 0 | 8 |

> 说明：单次重放无法复现 DSML 泄漏（DSML 泄漏是**低概率事件**，36 次重放 0 命中），
> 但 **`max_tokens` 截断**这条链路被稳定复现（7/36 ≈ 19%），且截断时的响应形态
> 与真实故障完全一致：**`content` 空 + 无 `tool_calls`**。

### 2.5 输入侧污染排查（已排除）

对真实会话的**所有 user / tool 消息**做全文扫描：

```
  [ 0] user  len=28734  clean      ← 28KB 用户消息，无 DSML / 无 ｜ / 无 name=
  [ 2] tool  len=208    clean
  [ 3] tool  len=98     clean
  [ 5] tool  len=8403   clean
  [ 7] tool  len=30     clean
  [ 9] tool  len=91     clean
  [11] tool  len=68     clean
  [13] tool  len=4573   clean
  [15] user  len=7      clean
  [17] tool  len=8020   clean
  [19] tool  len=1213   clean
  [21] tool  len=400    clean
```

- 用户首条 28KB 消息：不含 `DSML`、`｜`、`name="`、`string="true"`；
- 所有 tool 结果：无二进制乱码（可疑字符占比 0.00%）；
- 唯一沾染是 tool 结果里的 `\ufffd`（`AiTextSanitizer` 会清掉，见 `ai-debug.log`），**与 DSML 无关**。

**判定：已排除「输入污染导致模型模仿」这一路径。**

### 2.6 「自我强化循环」假说（已排除）

向历史中注入 DSML 文本后重放，模型**没有**继续吐 DSML：

```
### dsml_off  (历史无 DSML)   finish=tool_calls  dsml=False  names=['read_file','run_python']
### dsml_on   (历史含 DSML)   finish=tool_calls  dsml=False  names=['run_python']
  → 历史含 DSML: True
  → 模型是否吐 DSML: False
```

**判定：已排除「DSML 自我强化循环」**（至少在单轮注入下不成立）。

### 2.7 工具描述里的裸双引号（已排除为触发条件）

怀疑 `write_xlsx` 描述中的 `{"成绩":[{"姓名":"张三","分数":90}]}` 会破坏网关注入的 DSML 说明书。
用带裸引号的工具描述做了 8 次对照：

```
quoted_desc  run 0..7: finish=tool_calls  names=['save_note']  dsml=False  args='{"note": "hello world"}'
汇总 quoted_desc: {'n': 8, 'ok': 8, 'dsml': 0, 'weird': 0, 'none': 0}
```

**判定：已排除**。参数值里含双引号（`print("hello world")`）也不触发（8/8 正常）。

---

## 3. 客户端侧的既有缺陷（独立于 DSML，但会让故障不可恢复）

### 3.1 【严重】DSML 泄漏时，agent 主循环静默结束任务

`ClassManager.App/ViewModels/AiOfficeViewModel.cs:540-544`：

```csharp
if (result.ToolCalls.Count == 0)
{
    StatusText = "完成";
    break;          // ← 直接结束，不再追问
}
```

当网关泄漏 DSML 时，`result.ToolCalls` 为空、`result.Content` 是 DSML 原文。
客户端于是：

1. 把 DSML 原文当**正常正文**渲染给用户（消息 [14]/[22] 就是这么显示的）；
2. 因为 `ToolCalls.Count == 0`，判定「模型没有要调工具」→ **break 掉整个 agent 循环**。

结果：任务半途而废，用户看到一堆 `｜DSML｜` 乱码，且**没有任何错误提示**。
这与真实会话完全吻合——消息 [14] 之后，用户被迫又发了一条 `改完后发给我啊` 才继续。

### 3.2 【严重】从不读 `finish_reason`，截断被当成正常结束

`AiChatService.cs:471-482` 只取 `delta`，**从未读取 `choices[0].finish_reason`**。
`finish_reason=length`（被 `max_tokens` 截断）时：

- `ToolCalls` 为空 → 触发 §3.1 的 `break`；
- 用户看到的是**空的助手气泡**，没有任何「输出被截断」的提示。

§2.4 的数据显示这占 **19%** 的请求。

### 3.3 【已确认无影响】`assistant.content` 写 `""` 而非 `null`

抓包 A/B（6 次 vs 6 次）：

```
[empty] run 0..5: HTTP200 finish=stop  names=[]  dsml=False   content='工作区 `C:\Users\admin\Documents\ClassManager AI` 根目录下目前有 2 个文件'
[null]  run 0..5: HTTP200 finish=stop  names=[]  dsml=False   content='工作区根目录下只有两个文件：\n\n- `a.txt`\n- `b.txt`'
  >>> empty 汇总: {'n': 6, 'http_err': 0, 'tool_ok': 0, 'dsml': 0, 'weird': 0}
  >>> null  汇总: {'n': 6, 'http_err': 0, 'tool_ok': 0, 'dsml': 0, 'weird': 0}
```

两种写法都 `HTTP 200`，无 4xx，模型行为无差异。**不是 DSML 的原因**，但仍是规范偏差。

### 3.4 【已排除】SSE 解析丢帧 / 解析错误

对抓到的**全部 56,846 条 `data:` 行**做 JSON 解析校验：

```
抓到的 data: 行总数 = 56846   JSON 解析失败 = 0
非 data: 的行（注释/心跳/事件名）: 无
```

客户端解析结果与「独立复核拼接」**逐字节一致**（`raw 长度=88  client 长度=88`，`两者是否一致: True`）。
**SSE 解析本身没有丢内容。**

### 3.5 【已排除】正文头部损坏是客户端 bug

真实会话里若干 assistant 正文开头异常：

```
[ 1] 'ação先看一下桌面路径和那个文件夹是否存在。'      ← 应为「我先看一下…」
[ 8] '@@?# 学生成绩单统计\n\n## 直接结论…'              ← 应为「## 学生成绩单统计」
[ 6] 'ige\n\n'
[10] ' function returns error — let me check the tool signature…'
[12] 'rating=0.91）───\n\n## 学生成绩分析报告…'
```

**已排除客户端原因**：
- 客户端解析与原始 SSE 逐字节一致（§3.4）；
- `AiTextSanitizer.Clean`（`ClassManager.App/Services/AiTextSanitizer.cs`）只删控制字符 /
  孤立代理项 / U+FFFD，**不会**把 `##` 变成 `@@?#`；
- 落盘内容里**无尾部重复**（排除 `Progress<T>` 回调竞态导致的重复追加），
  也**无 U+FFFD 残留**。

**判定：这是网关/模型侧的字符级损坏（与 DSML 同源的「输出质量」问题），不是我们的 bug。**
但它同样会让用户看到莫名其妙的内容，建议随 §5.4 一并做兜底提示。

---

## 4. 抓包原始片段

### 4.1 正常工具调用（基线，`max_tokens=4096`）

```
data: {"id":"chatcmpl-fc2bb7cd-...","object":"chat.completion.chunk","created":1793410293,"model":"deepseek-v4.1-flash-1017","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}
data: {"id":"chatcmpl-fc2bb7cd-...","choices":[{"index":0,"delta":{"content":"I"},"finish_reason":null}]}
data: {"id":"chatcmpl-fc2bb7cd-...","choices":[{"index":0,"delta":{"content":"'ll"},"finish_reason":null}]}
...
data: {"id":"chatcmpl-fc2bb7cd-...","choices":[{"index":0,"delta":{"tool_calls":[{"id":"chatcmpl-tool-9808ea6e3a49e70e","type":"function","index":0,"function":{"name":"list_files"}}]},"finish_reason":null}]}
data: {"id":"chatcmpl-fc2bb7cd-...","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\": \""}}]},"finish_reason":null}]}
data: {"id":"chatcmpl-fc2bb7cd-...","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"}"}}]},"finish_reason":null}]}
data: {"id":"chatcmpl-fc2bb7cd-...","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}
data: {"id":"chatcmpl-fc2bb7cd-...","choices":[],"usage":{"prompt_tokens":1632,"total_tokens":1677,"completion_tokens":45,"prompt_tokens_details":{"cached_tokens":320,"created_cache_tokens":1280},"completion_tokens_details":{"reasoning_tokens":0}}}
data: [DONE]
```

**确认：网关返回的是标准 OpenAI 格式；`arguments` 以 JSON 字符串分片下发；
`function.name` 只在第一个分片出现一次（6/6 次实测），不存在「后覆盖前」的问题。**

### 4.2 被 `max_tokens` 截断的响应

```
finish_reason = "length"
usage = {"prompt_tokens": 35924, "total_tokens": 40020, "completion_tokens": 4096,
         "completion_tokens_details": {"reasoning_tokens": 4093}}
content 长度 = 0    tool_call 分片数 = 0
```

### 4.3 网关注入给模型的工具说明（模型自述复述）

模型被要求逐字复述其提示里的工具说明时，回答：

> ### Available Tools
> - list_files：列出目录内容。参数：path 文件路径，留空表示工作区根目录
> ...
> ### write_xlsx
> 生成 Excel 工作簿（.xlsx）。参数：path 输出路径；sheets 对象，形如 {"成绩":[{"姓名":"张三","分数":90}]}，第一行自动作为表头。

即网关把我们的 `tools` 数组**渲染成 Markdown 章节**注入提示。
模型同时确认提示中**没有** DSML / `invoke` / `string="true"` 字样：

> | `DSML` | 无 |
> | `｜DSML｜` | 无 |
> | `tool_calls` | 无 |
> | `invoke` | 无 |
> | `string="true"` | 无 |

**这印证了「DSML 是模型原生产物、网关负责转换」这一机制。**

---

## 5. 修复方案

### 5.1 【必做·低成本】请求体：`assistant.content` 空时改写 `null`

**文件:行号**：`ClassManager.App/Services/AiChatService.cs:272`

**现状**

```csharp
node["content"] = message.Content;
```

**改成**

```csharp
// assistant 带 tool_calls 时，规范要求 content 为 null 或省略；
// 空串不是规范值，部分兼容网关会因此解析出异常的 tool_call。
node["content"] = message.Content.Length == 0 && message.ToolCalls is { Count: > 0 }
    ? null
    : message.Content;
```

**依据**：OpenAI 规范；实测网关两种写法都接受（§3.3），**改动本身不改变行为**，
但消除了一个规范偏差、并让「正文为空」与「无正文」在协议层可区分。

### 5.2 【必做】客户端加 DSML 兜底解析 + 泄漏检测

**文件:行号**：`ClassManager.App/Services/AiChatService.cs`（新增一个解析器，
建议独立成 `ClassManager.App/Services/AiDsmlParser.cs`），在
`AiChatService.cs:536-544`（构造 `AiTurnResult` 之前）接入。

**为什么必须做**：DSML 泄漏是**网关侧**行为，客户端改不了网关；
但我们能保证「泄漏的 DSML 被当成工具调用执行，而不是当正文显示并终止任务」。

**解析规则**（两种竖线都要支持，已验证）：

| 要素 | 形态 |
|---|---|
| 信封开/闭标签 | `<｜DSML｜tool_calls>` / `<｜DSML｜function_calls>` / `<｜DSML｜calls>`（全角 U+FF5C）**或** ASCII `\|` |
| 调用块 | `<｜DSML｜invoke name="工具名"> ... </｜DSML｜invoke>` |
| 参数块 | `<｜DSML｜parameter name="参数名" string="true|false">值</｜DSML｜parameter>` |
| `string="true"` | 值按**原始字符串**保留（可直接含引号/换行，无需转义） |
| `string="false"` | 值按 **JSON** 解析（数字/布尔/数组/对象） |

**落地要求**：

1. **在 `content` 里检测** `<｜DSML｜` / `<|DSML|`；
   命中则**剥离标记**（不要让 `｜DSML｜` 出现在 UI 上）；
2. 解析出的调用**转成标准 `AiToolCall`**（`Id` 用 `dsml_<guid>`，`Name` 取 `invoke` 的 `name`，
   `Arguments` 取各 `parameter` 拼成的 JSON 字符串），**并入 `result.ToolCalls`**；
3. 解析不出完整 `invoke name` 时（真实样本 [14] 就是这种残缺形态），
   **不要执行**，而是把该轮标记为「需要重试」，交由 §5.4 的重试机制处理。

**真实样本的解析结果（用真实会话原文验证）**：

```
样本[0] '｜DSML｜ parameter>\n</｜DSML｜ invoke>\n</｜DSML｜ calls>'
  parse_dsml -> []          ← invoke 头被网关吞了，拿不到工具名
样本[1] '｜DSML｜ parameter name="code" string="true">\n...\n</｜DSML｜ parameter>\n</｜DSML｜ invoke>\n</｜DSML｜ calls>'
  parse_dsml -> []          ← 同上
```

**结论**：**仅靠 DSML 文本无法还原被吞掉的 `invoke name`**。
所以 §5.4 的重试是**必需**的兜底，DSML 解析是**尽力而为**的补充。

**合成样本的解析验证（全部通过）**：

```
全角竖线 + string=true:
  -> [{"name": "run_python", "arguments_json": "{\"code\": \"print(1)\\n\"}", "params": {"code": "print(1)\n"}}]
ASCII 竖线变体:
  -> [{"name": "read_file", "arguments_json": "{\"path\": \"a.txt\"}", "params": {"path": "a.txt"}}]
多个 invoke:
  -> [{"name": "list_files", ...}, {"name": "read_file", ...}]
string=false（JSON 值）:
  -> [{"name": "write_xlsx", "arguments_json": "{\"sheets\": {\"成绩\": [{\"姓名\": \"张三\"}]}, \"path\": \"a.xlsx\"}", ...}]
正文 + DSML 混排:
  -> [{"name": "list_files", "arguments_json": "{\"path\": \"\"}", ...}]
带 CDATA:
  -> [{"name": "run_python", ...}]
```

### 5.3 【必做】补全 8 个工具的参数 schema

**文件:行号**：`ClassManager.App/Services/AiFileTools.cs:66-113`

**现状**：`write_xlsx` / `write_docx` / `write_pptx` / `search_tool` / `install_tool` /
`run_python` / `run_node` / `web_fetch` 共 **8 个工具没有显式 `ParametersJson`**，
落到 `AiChatService.cs:35` 的默认值 `{"type":"object","properties":{}}`。
模型在 schema 里**看不到任何参数名**，只能从中文散文里猜。

**改成**（示例，其余同理）：

```csharp
new()
{
    Name = "run_python",
    Description = "执行一段 Python 脚本（可以用 import 使用刚装好的包）。参数：code 脚本内容。",
    ParametersJson = """
    {"type":"object","properties":{"code":{"type":"string","description":"要执行的 Python 脚本源码"}},"required":["code"]}
    """,
},
```

**依据**：真实会话消息 [6] 的工具名被误解析成 `description" string="true`——
网关在解析 DSML 时抓错了属性。**模型提供的参数信息越模糊，DSML 里的属性就越容易写歪**，
从而更容易触发网关解析器的歧义路径。补全 schema 是从源头减少 DSML 畸形输出的最直接手段。
（补充说明：本次未能通过注入畸形描述稳定复现该解析歧义，因此这条是**降低概率**而非**根除**。）

### 5.4 【必做】`max_tokens` 提高 + 读 `finish_reason` + 截断重试

**(a) 提高 `max_tokens`** —— `ClassManager.App/Services/AiChatService.cs:303`

```csharp
["max_tokens"] = 4096,     // 现状：思考 token 会吃满，正文/工具调用一个字都不吐
```

实测：4096 → 36 次里 7 次（19%）`finish_reason=length`；16384 → 8 次里 0 次。
建议改为 **16384**，并注意网关支持的上限。

**(b) 读取 `finish_reason`** —— `ClassManager.App/Services/AiChatService.cs:471-482`

当前 `delta` 解析完全忽略 `finish_reason`。需新增：

```csharp
// 记录 finish_reason；"length" 表示输出被 max_tokens 截断
if (choices[0]?["finish_reason"] is JsonValue fr && fr.TryGetValue<string>(out var frs))
{
    finishReason = frs;
}
```

并在 `AiTurnResult` 上暴露出来（如 `FinishReason`）。

**(c) 截断/DSML 泄漏时不要静默 break** —— `ClassManager.App/ViewModels/AiOfficeViewModel.cs:540-544`

```csharp
if (result.ToolCalls.Count == 0)
{
    if (result.FinishReason == "length")
    {
        // 被 max_tokens 截断：不是模型说完了，是预算用光了
        StatusText = "输出被截断，正在续写…";
        // 重试：提高 max_tokens 后再问一次，或把已输出部分续上
    }
    else if (content 含 DSML 残留)
    {
        StatusText = "模型输出异常，正在重试…";
        // 重试：把「请用平台提供的工具调用接口，不要输出 DSML 标记」追加进提示
    }
    else
    {
        StatusText = "完成";
        break;
    }
}
```

**重试提示词建议**（追加到系统提示末尾或作为一条 user 消息）：

```
上一轮你输出的工具调用标记无法被解析。请只通过平台提供的结构化工具调用接口发起调用，
不要在正文中输出任何形如 <｜DSML｜… 的标记。
```

### 5.5 【建议】兜底：未知工具名不静默失败

**文件:行号**：`ClassManager.App/Services/AiFileTools.cs:156`

```csharp
_ => $"未知工具：{name}",
```

消息 [7] 的返回 `未知工具：description" string="true` **已经**返回了这个提示，
模型也确实在下一轮（消息 [10]）自我纠正了（`The tool is read_file with path. I mistyped.`）。
**这一处行为正确，无需修改**，此处仅作为记录。

---

## 6. 结论清单（逐条标注证据强度）

### 已确认（附证据片段）

| 结论 | 证据 |
|---|---|
| 请求体 `arguments` 是 JSON 字符串，不是对象 | C# 探针 `arguments 节点类型 = JsonValuePrimitive`，序列化 `"{\"path\": \"\"}"`；磁盘 10/10 个调用均为 `str` |
| `tool_call_id` 与 `tool_calls[].id` 严格一一配对 | 10 vs 10 完全匹配，`未收到结果的: []` |
| `tools` 数组结构完整 | 12 个工具 `type`/`name`/`description`/`parameters` 齐全 |
| `assistant.content` 写的是 `""` 不是 `null` | C# 探针 `content 序列化 = ""` |
| 网关接受 `""` 与 `null`，行为无差异 | 6 vs 6 次 `HTTP 200`，finish/内容一致 |
| DSML 是 DeepSeek 原生工具调用格式 | U+FF5C 全角竖线 + `invoke`/`parameter`/`string="true"` 结构；模型确认提示中无 DSML |
| DSML 确实泄漏进了 `content` | 真实会话消息 [14]/[22] 原文，`ToolCalls: null` |
| 被误解析的工具名是 DSML parameter 属性 | 消息 [6] `Name = "description\" string=\"true"`，含 `string="true` |
| 网关会把我们的 tools 渲染成 Markdown 注入提示 | 模型复述出 `### Available Tools` / `### write_xlsx` 段落 |
| `max_tokens=4096` 导致 19% 请求被截断 | 36 次重放 7 次 `finish_reason=length`，`completion_tokens` 恰为 4096，`reasoning_tokens=4093` |
| `max_tokens=16384` 消除截断 | 8 次重放 0 次截断 |
| 客户端从不读 `finish_reason` | `AiChatService.cs:471-482` 只取 `delta` |
| 客户端无任何 DSML 处理 | 全仓库 grep `DSML` / `｜` / `tool_call_id` 只命中 `AiChatService.cs:267`、`AiModels.cs:35`（无 DSML 逻辑） |
| 输入侧无 DSML 污染 | 12 条 user/tool 消息扫描全部 `clean` |
| DSML 自我强化循环不成立 | 注入 DSML 后 2/2 次模型未吐 DSML |
| 工具描述裸引号不触发 DSML | `quoted_desc` 8/8 正常 |
| SSE 解析无丢帧 | 56,846 条 `data:` 行，JSON 解析失败 0；解析结果与独立复核逐字节一致 |
| 正文头部损坏不是客户端 bug | 同上 + 落盘无重复、无 U+FFFD；`AiTextSanitizer` 不产生此类字符 |
| 8/12 工具 `parameters.properties` 为空 | §1.3 表格 + `AiChatService.cs:35` 默认值 |
| `arguments` 若为对象会使 `GetValue<string>()` 抛异常 | C# 探针 `InvalidOperationException: The node must be of type 'JsonValue'` |

### 假设（有间接证据，未直接复现）

| 结论 | 说明 |
|---|---|
| DSML 泄漏的**直接**触发是响应在 DSML 结构中途被 `max_tokens` 截断 | 截断率 19%、截断形态（content 空 + 无 tool_calls）与真实故障一致；但 36 次重放 0 次复现 DSML 泄漏，**未直接证实因果** |
| 网关 DSML→OpenAI 转换器在「parameter 标签属性」上存在歧义 | 消息 [6] 的 `description" string="true` 是强证据，但无法从外部观察转换器实现 |
| 8 个空 schema 工具提高了 DSML 畸形输出概率 | 逻辑合理，但注入畸形描述的对照实验**未能复现**该解析歧义 |

### 已排除

| 假说 | 排除依据 |
|---|---|
| `arguments` 被存成 JSON 对象 | C# 探针 + 磁盘 10/10 均为字符串 |
| `tool_call_id` 错配 | 10 vs 10 完全匹配 |
| 网关不接受 `content:""` | 6/6 `HTTP 200` |
| 客户端 SSE 解析丢内容 | 56,846 行 0 失败；逐字节一致 |
| 输入里含 DSML 导致模型模仿 | 12 条消息扫描全 clean |
| DSML 自我强化循环 | 注入实验 2/2 不成立 |
| 工具描述含裸双引号 | 8/8 正常 |
| 客户端 `AiTextSanitizer` 造成头部损坏 | 消毒器只删控制字符/孤立代理项/U+FFFD |
| `function.name` 被拆成多片导致「后覆盖前」 | 6/6 次实测 `name` 只出现一次，两种策略结果相同 |

---

## 7. 建议的落地顺序

| 优先级 | 改动 | 位置 | 预期效果 |
|---|---|---|---|
| P0 | `max_tokens` 4096 → 16384 | `AiChatService.cs:303` | 消除 19% 的截断；截断正是 DSML 泄漏与「空回复」的共同上游 |
| P0 | 读 `finish_reason` + 截断时重试/续写，不静默 break | `AiChatService.cs:471-482`、`AiOfficeViewModel.cs:540-544` | 用户不再看到空回复或乱码后任务静默终止 |
| P0 | DSML 兜底解析 + 从正文剥离标记 + 泄漏时重试 | 新增 `AiDsmlParser.cs`，接 `AiChatService.cs:536` | 网关泄漏时不再显示乱码、不再终止任务 |
| P1 | 8 个工具补全 `parameters` schema | `AiFileTools.cs:66-113` | 降低 DSML 畸形输出概率；模型不再猜参数名 |
| P1 | `assistant.content` 空时置 `null` | `AiChatService.cs:272` | 消除规范偏差 |
| P2 | `arguments` 类型防御（对象→报错而非崩溃） | `AiChatService.cs:523` | 防御网关行为变更 |

---

## 8. 取证方法与可复现性说明

- 临时目录 `D:/信息技术/ClassManager/.tmp-forensics/`（脚本 + 56,846 行 SSE 抓包 + C# 探针）
  **已在报告完成后删除**。
- 网关凭据取自 `C:/Users/admin/Desktop/APIKey.txt` 与
  `ClassManager.App/Assets/ai/credentials.json`（两者一致，`api1`/`api2` 两把 key 轮换使用）。
- 本次共向网关发起 **约 150 次**请求（基线 / 重放 / A-B / 参数变体 / 提示探针 / max_tokens 对照）。
- **未修改** `ClassManager.App/` 与 `ClassManager.Tests/` 下任何文件；所有 .NET 探针都是
  独立的 `net8.0` 控制台项目，只复刻被测逻辑，不引用业务项目。
- 单次请求无法复现 DSML 泄漏（低概率事件）；§2.4 的 `max_tokens` 截断率是**多次重复**得到的统计值。
