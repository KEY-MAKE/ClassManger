# QA 验收报告：协议层修复（`<result>` 循环 / DSML 泄漏）

- **版本**：v0.27
- **验收日期**：2026-10-31
- **验收范围**：`ClassManager.App/Services/AiInternalMarkup.cs`、`AiDsmlParser.cs`、
  `AiChatService.cs`、`ViewModels/AiOfficeViewModel.cs`
- **验收人**：独立验收工程师（新视角复核，不复述实现意图）
- **结论**：**主体有效，但有 1 个 P0 缺陷会让合法回答被误杀，另有 3 个 P1 缺陷。**
  建议修复 P0 后再发布。

---

## 0. 验证方法

不依赖实现者的描述，全部结论由可复现的实验得出：

| 手段 | 说明 |
|---|---|
| 独立探测工程 | `.tmp-forensics/probe/` 直接编译产品源文件（`AiInternalMarkup.cs` / `AiDsmlParser.cs`），黑盒调用其公开 API，观察真实输出 |
| 真实语料回放 | 用真实事故会话文件 `C:/Users/admin/AppData/Roaming/ClassManager/ai-conversations.json`（3 个会话 / 82 条消息，含真实 `<result>` 循环与 DSML 泄漏样本）跑一遍新代码，度量真实影响面 |
| ViewModel 端到端用例 | `ClassManager.Tests/AiAnomalyRoundTests.cs` 用假聊天服务驱动 `SendAsync`，复刻 `AiChatService` 末端的真实管线（DSML 解析 → 内部标记剥离 → 三个标志位），验证重试/打断/落盘行为 |
| 真实网关验证 | `.tmp-forensics/empty_assistant_probe.py` 对真实端点验证「空 assistant 消息（无 tool_calls）」是否被拒 |

---

## 1. 测试结果

```
dotnet test ClassManager.sln -c Release
已通过! - 失败: 0，通过: 189，已跳过: 0，总计: 189
```

- **基线 160 → 现在 189**，新增 29 条（基线 160 条全部仍然通过，无回归）。
- 新增文件：
  - `ClassManager.Tests/AiInternalMarkupEdgeCaseTests.cs`（13 条）—— 剥离器边界行为
  - `ClassManager.Tests/AiAnomalyRoundTests.cs`（16 条）—— 异常轮判定 / 重试 / 历史清洗
- 未修改 `ClassManager.App/` 下任何产品代码（仅验证）。

---

## 2. 结论清单

### 2.1 已确认（有效）

| # | 结论 | 证据 |
|---|---|---|
| C1 | **`<result>` 空转循环能被正确识别** | 真实样本（28213 字 / 3098 标记 / 0 闭合）→ `MarkerCount=3098`、`LooksLikeLoop=True`、`ReasoningOnly=True`、正文长度 0。`AiInternalMarkupTests.cs:63` 已覆盖 |
| C2 | **循环真的会被打断，不会死循环** | `Marker_loop_is_broken_after_bounded_retries`：模型每轮都吐 3098 个标记 → 客户端恰好发 **3 次**请求（1 轮原始 + 2 次纠错重试），第 3 轮后停手并给出「已停止」错误提示。`anomalyRetries` 计数与 `continue` 路径自洽 |
| C3 | **DSML 泄漏不再静默结束任务** | `Dsml_leak_is_retried_instead_of_ending_the_task`：真实残缺样本（第 14 号消息）→ 判为异常轮 → 重试 1 次后正常收敛；界面上不出现 `｜DSML｜` 乱码 |
| C4 | **畸形工具名不会被执行** | 真实消息 [6] 的工具名是 `description" string="true`（网关把 DSML `parameter` 属性当成了函数名）。新代码不产生这个畸形名；解析器还原不出 `invoke name` 时宁可丢弃也不执行（`AiDsmlParserTests` 已覆盖）。未知工具名走 `AiFileTools.cs:180` 的 `_ => $"未知工具：{name}"`，不会崩溃 |
| C5 | **正常短回答不会被判成 `ReasoningOnly`** | `Short_normal_answer_is_not_treated_as_abnormal`：「好。」→ 1 次请求、无错误。`ReasoningOnly` 定义为 `effectiveCount > 0 && cleaned.Length == 0`，**要求同时存在标记**，纯短正文（无标记）不满足 |
| C6 | **正文里不成标签的 `<result` 不会被误删** | `Non_tag_angle_bracket_text_is_preserved`：`<result` / `<resultx` / `<results` 三个都原样保留（`OpenMarkerRegex` 要求精确匹配 `<result>`） |
| C7 | **标记之前的正文完整保留** | `Body_before_the_marker_is_kept_verbatim`：「这是给用户的结论。」一字不差；`Body_between_two_markers_is_kept`：A/B/C 三段都在 |
| C8 | **历史清洗不改坏用户消息和 tool 消息** | `History_cleaning_keeps_user_and_tool_messages_intact`：用户消息里的 `<result>`、tool 结果里的 `</result>` 均原样保留（清洗只对 `role == "assistant"` 生效，`AiOfficeViewModel.cs:251`） |
| C9 | **思考过程默认折叠** | `_isProcessExpanded` 无显式初始化（`AiOfficeViewModel.cs:61`），默认 `false`；全仓库无任何位置把它设为 `true`。`AiOfficePage.xaml:177` 绑定 `IsExpanded` |
| C10 | **`finish_reason == "length"` 归入异常轮** | `AiOfficeViewModel.cs:580`；`AiChatService.cs:515-520` 正确读取 `choices[0].finish_reason`（且在 `delta == null` 的分支之前读取，不会漏） |

### 2.2 有问题

#### **P0-A｜`markerCount` 数的是「`<` 的个数」，不是「内部标记的个数」→ 合法回答被误判为空转循环**

- **位置**：`ClassManager.App/Services/AiInternalMarkup.cs:76-77`
  ```csharp
  var markerCount = content.Count(c => c == '<') - CountClosingMarkers(content);
  ```
- **错在哪**：这行把正文里**任意**一个 `<` 都算成内部标记。真正准确的 `CountMarkers(content)`（`:114`，只数 `<result>`/`<thinking>`/`<reasoning>`/`<thought>`）被放在 `:102` 的 fallback 分支里：
  ```csharp
  var effectiveCount = markerCount > 0 ? markerCount : CountMarkers(content);
  ```
  只要正文有**一个** `<`，`markerCount` 就 > 0，**准确的计数永远用不上**。
- **实测**（探测工程）：
  - 20 个 `<` 的正常代码 → `MarkerCount=20` → `LooksLikeLoop=True`
  - 12 个 XML 元素（24 个 `<`）→ `LooksLikeLoop=True`
  - 阈值实测边界：19 个 `<` → False，**20 个 `<` → True**
- **实际危害**（`AiOfficeViewModel.cs:583-586`）：`abnormal` 判定包含 `result.MarkerLoop` →
  1. 一段正常的 XML/HTML/代码回答被当成「模型陷入循环」，触发**最多 2 次无意义的重试**（每次都要真的发一次请求、消耗一次 token）；
  2. 若两次重试后仍是同一段代码 → 用户看到「模型连续多次陷入重复输出内部标记的循环，已停止」这种**完全错误的报错**，而正文其实一直是对的；
  3. 纠错提示会让模型以为自己循环了，可能真的开始胡说。
- **代码证据**：`Current_behaviour_code_with_many_less_than_signs_is_misjudged_as_loop` —
  20 行 `if (aN < bN)` 的正常代码 → 客户端发了 **3 次**请求并最终报错。
- **本项目的高风险场景**：这是一个「AI 办公」工具，模型经常被要求生成 XML/HTML/代码片段；
  真实语料里 conv#1 msg#7（一条 tool 消息，二进制 xlsx 内容）就有 32 个 `<`。
  该 tool 消息不走剥离路径所以没事，但同样形态出现在 assistant 正文里就会中招。
- **建议修法**：`markerCount` 直接用 `CountMarkers(content)`（数真正的内部标记），
  闭合/成对与否交给 `LooksLikeLoop` 之外的条件判断；或至少把「`<` 总数」与
  「内部标记数」拆成两个字段，只有后者参与循环判定。

#### **P0-B｜历史清洗产生空 assistant 消息，与同文件 237 行的既有约定自相矛盾**

- **位置**：`ClassManager.App/ViewModels/AiOfficeViewModel.cs:238-262`
- **错在哪**：过滤空 assistant 消息的检查在 **238-245 行**（清洗**之前**），
  而清洗在 **251-262 行**（会把纯内部标记的消息洗成空正文）。
  于是「原本有内容、清洗后变空」的 assistant 消息**绕过了过滤**，带着空正文进入 `_history`。
- **实测（真实语料）**：4 条真实 assistant 消息（msg#47/50/52/58）在恢复历史后
  变成 `content=""` 的 assistant 消息留在 `_history` 里。
- **危害**：这些空消息会**回传给模型**（`AiChatService.cs:304` `node["content"] = message.Content`）。
  实测（`.tmp-forensics/empty_assistant_probe.py`，真实网关 3/3 HTTP 200）：
  **本项目当前网关其实接受这种形态**，所以不会 400 —— 这点与代码注释的说法不符，
  但代码本身的不一致是真的（238 行按「会拒绝」过滤，重试路径却制造这种消息）。
- **建议**：把空消息过滤移到清洗**之后**；或者清洗后若正文为空则整条丢弃。

#### **P1-A｜用户真想看到 `<result>` / `</result>` 字样时，回答被腰斩且不报错**

- **位置**：`AiInternalMarkup.cs:82-94`（成对块 + 未闭合标记的贪婪处理）
- **错在哪**：用户问「`<result>` 是什么」，模型照实解释：
  ```
  输入：<result> 是 DeepSeek 用来包裹内部思考的标签，</result> 是它的闭合形式。
  正文：是它的闭合形式。          ← 前半句没了
  思考区：是 DeepSeek 用来包裹内部思考的标签，  ← 被静默挪进「思考」区
  ```
  `ReasoningOnly=false`、`LooksLikeLoop=false` → **不触发重试、不报错**，
  用户直接看到一个残缺答案，且前半句被塞进默认折叠的「思考」区里看不见。
- **同类问题**：
  - 嵌套标记 `<result>外层<result>内层</result>尾巴</result>结尾` →
    正文变成 `尾巴</result>结尾`，**残留的 `</result>` 直接暴露在用户可见正文里**；
  - 单独的闭合标签 `</result>` → `HasInternal=false`，**原样留在正文**（与开标签处理不对称）。
- **建议**：只有当标记出现在「疑似内部推理」的位置（行首、独占一行）时才剥离；
  行文中出现的 `<result>` 应当保留或转义。

#### **P1-B｜正文里正常提到 `｜DSML｜` 会被当成泄漏 → 回答被截断 + 触发重试**

- **位置**：`ClassManager.App/Services/AiDsmlParser.cs:99-101`（检测）、`:185-197`（整段截断）
- **错在哪**：检测条件只要正文同时含 `DSML` 和 `｜DSML｜` 就判定为泄漏；
  还原不出调用时，`:196` 把**第一个标记之后的全部内容**截掉。
- **实测**：
  ```
  输入：DSML 是 DeepSeek 的标记语言，形如 ｜DSML｜invoke name="read_file"> 这样调用。
  清洗后：DSML 是 DeepSeek 的标记语言，形如        ← 后面全没了
  ```
  同时 `Unparsable=true` → `DsmlLeak=true` → 触发重试。
  用户问了个正常问题（这个项目自己的取证报告就在讨论 DSML），却被当成故障重试。
- **建议**：截断前先确认该处**确实像** DSML 结构（后面紧跟 `invoke`/`parameter`/`calls`），
  否则只剥标记不截断。

#### **P1-C｜纠错提示以 `role=user` 写进 `_history` 并落盘 → 冒充用户发言 + 永久污染上下文**

- **位置**：`ClassManager.App/ViewModels/AiOfficeViewModel.cs:625-629`
- **错在哪**：纠错提示被 `Role = "user"` 加入 `_history`，随后经 `Persist()` 落盘。
  - 界面上会显示成一条**「你」的气泡**（`OnSelectedConversationChanged` 按 role 渲染）；
  - 之后每一轮请求都把它当**用户说的话**回传给模型（实测确认）；
  - 它永久留在会话文件里 —— 即使用户早就换了话题。
- **实测**：`Current_behaviour_correction_prompt_is_persisted_to_disk` 确认落盘 +
  下一轮历史里仍以 `role=user` 出现。
- **建议**：用 `role=system` 或独立的、不进 `_history` 的旁路通道传纠错提示。

#### **P1-D｜代码围栏被无条件删除，正常回答里的代码块被破坏**

- **位置**：`AiInternalMarkup.cs:64-66`（`FenceRegex`）、`:97`
- **错在哪**：`Strip()` 无条件执行 `FenceRegex.Replace(working, "")`，
  与「是否含内部标记」无关。正常回答里的 ``` 围栏（含语言标注）被整段删掉：
  ```
  输入：步骤：\n```bash\necho hello\n```\n完成。
  清洗后：步骤：\necho hello\n完成。
  ```
  真实语料里有 6 条 assistant 消息含围栏（conv#0 msg#24/30/36/56、conv#1 msg#8/12）。
  其中 conv#0 msg#30/36 本身就是 ```` ```text ```` 包裹的思考段，
  围栏被删后**这些内部思考段反而看不出是内部内容**了。
- **建议**：只在剥离出内部内容时，才顺带去掉其围栏。

### 2.3 已排除

| # | 怀疑 | 结论与证据 |
|---|---|---|
| E1 | 短正文被误判成 `ReasoningOnly` | **排除**。`ReasoningOnly` 要求 `effectiveCount > 0`（必须有标记）**且**正文为空。实测「好。」/「OK」→ `ReasoningOnly=False`，1 次请求无错误 |
| E2 | 空回复（无标记）被判成异常轮 | **排除**。`ReasoningOnly`/`MarkerLoop` 都要求 `effectiveCount > 0`，纯空正文两个标志都是 False → 按「正常完成」处理。注意这与 FORENSIC 报告 §3.2 描述的旧行为不同（旧行为是 `finish_reason` 不读导致静默 break，现在 `finish_reason=="length"` 已归入异常轮） |
| E3 | 纠错重试不生效、继续死循环 | **排除**。实测 3 次请求后停手。`anomalyRetries` 在 `:614` 自增、`:595` 比较 `MaxAnomalyRetries=2`，`continue` 路径 `:635` 确实回到 `while` 顶部。另有 `:639` 在拿到工具调用时重置计数 —— 这个重置是必要的，否则「工具轮 + 异常轮」交替会提前耗尽预算 |
| E4 | 历史清洗写坏 tool 消息 / 用户消息 | **排除**。清洗条件 `message.Role == "assistant"`（`:251`）严格限定；tool 消息走 `:268` 的分支归并到「过程」区，内容不改 |
| E5 | 历史清洗后不写回磁盘 | **排除**。`OnSelectedConversationChanged` 直接改 `message.Content`（内存对象），
  而 `_history` 与 `SelectedConversation.Messages` 共享同一批 `AiMessage` 实例
  （`:264` `_history.Add(message)`），下次 `Persist()` 会把清洗后的内容写盘。
  实测 `History_cleaning_is_persisted_on_next_save` 确认落盘文件里不再有 `<result>`。
  **但注意**：切换会话本身不触发落盘（实测 `History_cleaning_does_not_touch_disk_immediately`），
  磁盘上的旧垃圾要等下一次 `Persist()` 才被清掉 |
| E6 | 畸形工具名 `description" string="true` 会导致崩溃 | **排除**。`AiFileTools.cs:180` 有 `_ =>` 兜底；真实语料 msg#7 显示当时的实际结果是
  `未知工具：description" string="true` 这条普通文本，程序没有崩溃 |
| E7 | DSML 还原出的工具调用会被错误执行 | **排除**。`AiDsmlParser.cs:112-116` 要求 `invoke` 必须带 `name` 属性，
  拿不到就 `continue`；`ParameterRegex` 要求 `string="true|false"` 齐全。
  真实残缺样本（invoke 头被吞）解析结果为空 → 不执行 → 走重试 |
| E8 | 空 assistant 消息会被网关拒绝（代码注释的说法） | **排除**。实测真实网关 3/3 HTTP 200。`FORENSIC-protocol.md` §3.3 的 A/B
  只覆盖了「带 tool_calls」的情况，未覆盖本场景；补测后确认网关接受。
  这条**不是** P0-B 的危害来源，P0-B 的问题是代码内部自相矛盾 + 留垃圾 |

---

## 3. 对「两类真实故障」的最终判定

| 故障 | 修复是否有效 | 说明 |
|---|---|---|
| `<result>` 循环（28213 字 / 3098 标记 / 0 闭合） | **有效** | 识别准确、正文归零、循环被打断、给出明确提示 |
| DSML 泄漏（`｜DSML｜` 漏进 content） | **有效（但有副作用）** | 不再显示乱码、不再静默结束任务；副作用是正文提到 `｜DSML｜` 会被误判（P1-B） |
| 工具名被误解析成 `description" string="true` | **未直接处理** | 新代码不会产生这个畸形名，也不会执行它；但**网关侧的转换器走偏**这个根因仍在。
  客户端只能靠 `DsmlLeak` → 重试来缓解。若期望「客户端彻底消除这个症状」，还缺一个「工具名合法性校验」 |

---

## 4. 建议处理顺序

1. **P0-A**（`markerCount` 口径）—— 影响面最大，会误杀合法回答。
   改 `AiInternalMarkup.cs:76-77` 的计数口径即可，改动最小、收益最大。
2. **P0-B**（空 assistant 消息）—— 把过滤挪到清洗之后。
3. **P1-A / P1-B**（标记/DSML 的误伤）—— 收紧触发条件：行首/独占一行才当内部标记；
   DSML 需结构完整才截断。
4. **P1-C**（纠错提示冒充用户）—— 换 `role=system` 或旁路。
5. **P1-D**（围栏）—— 只在剥离内部内容时才动围栏。

P0-A 与 P0-B 都是**局部改动**，不需要重构剥离器结构。

---

## 5. 附：新增测试清单

`ClassManager.Tests/AiInternalMarkupEdgeCaseTests.cs`（13 条）
- 正常正文不被误伤：不成标签的 `<result`、含 `<` 的普通文本、20 个 `<` 的代码、12 个元素的 XML
- 标记归属：标记前正文保留、两标记之间的正文保留
- 畸形输入：单独闭合标签、嵌套标记残留、未闭合标记吃后续
- 用户真想看到 `<result>` 字样（现状断言）
- 空回复不被标记（现状断言）
- 计数口径（现状断言）、真实 3098 标记样本

`ClassManager.Tests/AiAnomalyRoundTests.cs`（16 条）
- 循环被打断（恰好 3 次请求 + 明确报错）、纠错提示回传
- 正常短回答 / 含 `<` 的正常回答不被判异常
- 20 个 `<` 的正常代码被误判成循环（现状断言）
- 历史清洗：不动 user/tool 消息、切换时不落盘、下次保存时落盘
- 历史清洗：忽略 DSML 垃圾（现状断言）、制造空 assistant 消息（现状断言）
- 纠错提示以 `role=user` 落盘并在后续历史里冒充用户（现状断言）
- 空 assistant 消息留在历史里（现状断言）
- DSML 泄漏触发重试而非结束任务、正文提到 ｜DSML｜ 被截断（现状断言）、不可解析的 DSML 有界停止

---

## 6. 备注

- 探测工程 `.tmp-forensics/probe/` 与取证脚本 `.tmp-forensics/empty_assistant_probe.py`
  是本次验收的辅助产物，**不属于产品代码**；如需清理可直接删除 `.tmp-forensics/probe/`。
- 本次验收**未修改** `ClassManager.App/` 下任何文件；所有产品代码改动建议在 P0 修复后由开发者实施。
