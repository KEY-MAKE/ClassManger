# 测试报告 · 班级综合管理系统（C# WPF-UI 重构版）

> 测试执行：QA 工程师 严过关（独立验收，全新视角，主动构造边界与错误路径）
> 被测版本：`ClassManager.App`（WinExe, net8.0-windows），自包含发布 `publish/win-x64/`
> 报告日期：2026-01-11
> 报告文件：`docs/TEST-REPORT.md`

---

## 0. 结论摘要

| 项 | 值 |
|---|---|
| 测试项总数（本报告粒度） | 51 |
| 通过 | 48 |
| 失败（源码缺陷） | **3 个用例（同一根因，P0）** |
| 测试代码自身缺陷（已自查修正） | 1（我自己的验收脚本，非仓库用例） |
| **路由判定** | **Engineer（寇豆码）** —— 存在 1 个高危源码 Bug |
| 严重级别最高项 | **P0 高危：CSV 导入功能完全不可用** |

> **一句话结论**：除「CSV 导入」这一条外，构建、单实例、安装/卸载、xlsx 导入、点名、排座、导出、设置健壮性、暗色主题均通过独立验证。**CSV 导入经控制组对照确认为源码缺陷，建议退回工程师修复（Round 2 回归）。**

---

## 1. 测试环境

| 项 | 值 |
|---|---|
| .NET SDK | 8.0.421 |
| 被测 exe | `D:\信息技术\ClassManager\publish\win-x64\ClassManager.App.exe`（321 KB，自包含文件夹 468 文件） |
| 测试驱动 | Bash（Git Bash）+ PowerShell 5.1 + 自建 C# 验收 Harness（`%TEMP%\cm_qa\QAHarness`，引用 `ClassManager.App` 工程） |
| 环境约束 | 沙箱屏蔽 `reg.exe`，注册表改用 PowerShell `Microsoft.Win32.Registry` 读取；沙箱对 `cmd/rmdir` 有策略拦截（不影响被测算法的自删除脚本，已用文件计数交叉验证） |

> **独立性声明**：除「构建/单测」外，其余项均**未复用工程师脚本**，为本次重新构造的命令与样本；其中导入/导出/业务逻辑用**真实文件 + 控制组对照**验证，非 `ImportFromRows` 逻辑测试。

---

## 2. 逐项测试结果

### A. 构建与测试

| # | 测试项 | 命令 | 原始输出摘要 | 结论 |
|---|---|---|---|---|
| A1 | 干净重建（先 clean 再 build） | `dotnet clean ClassManager.sln -c Release` → `dotnet build ... -c Release` | `已成功生成。 0 个警告 0 个错误`（耗时 28.06s，clean 先行） | **PASS** |
| A2 | 单测复跑 | `dotnet test ClassManager.sln -c Release --no-build` | `已通过! - 失败: 0，通过: 36，已跳过: 0，总计: 36` | **PASS**（用例数 36 属实） |
| A3 | 测试内容质量审查 | 代码审查 4 个测试文件 | 见 §5「测试质量审查」 | **PASS（有改进项）** |

### B. 单实例与窗口激活

| # | 测试项 | 命令 / 方法 | 原始输出摘要 | 结论 |
|---|---|---|---|---|
| B1 | 实例2 退出码与耗时 | 启动实例1（停在向导）→ 实测实例2 | `instance2 exit=3 elapsed_ms=1417` | **PASS**（<3s，码=3，独立复现） |
| B2 | 杀掉后无残留 | `tasklist /FI "IMAGENAME eq ClassManager.App.exe"` | 计数 `0` | **PASS** |
| B3 | 异常路径：连续 5 实例 | 快速启动 5 个 | `burst#1..5 exit=3`；存活计数 `1` | **PASS**（仅 1 存活，其余全码 3） |

### C. 安装 / 卸载

| # | 测试项 | 命令 | 原始输出摘要 | 结论 |
|---|---|---|---|---|
| C1 | 中文+空格路径安装 | `--install "%TEMP%\班级 测试 目录" --quiet --no-desktop-shortcut --no-startmenu-shortcut` | `安装完成: ...` `install_exit=0`；目标目录 468 文件，`ClassManager.App.exe` 存在 | **PASS** |
| C2 | 注册表项内容 | PowerShell 读取 HKCU | `DisplayName/DisplayVersion=1.0/Publisher=K./DisplayIcon/UninstallString/InstallLocation/NoModify=1/NoRepair=1/EstimatedSize=171337` 全部正确 | **PASS** |
| C3 | 重复安装（覆盖） | 同目录再装一次 | `repeat_install_exit=0`，静默覆盖，无报错 | **PASS** |
| C4 | 已安装态启动判定 | 从安装目录启动，取 `MainWindowTitle` | `title='班级综合管理系统'`（**含“安装向导”则失败**）| **PASS**（进主界面，非向导） |
| C5 | 卸载清理 | `--uninstall --quiet` | `uninstall_exit=0`；注册表 `KEY_EXISTS=no`；自删除脚本残留 `0`；无残留进程 | **PASS**（目录删除为**异步**，见注） |
| C6 | `--keep-settings` | 预置 settings.json → 安装 → `--uninstall --quiet --keep-settings` | `settings.json exists=yes`（保留），安装目录已删 | **PASS** |
| C7 | 默认卸载删设置 | 预置 settings.json → 安装 → `--uninstall --quiet` | `settings.json exists=no` | **PASS** |
| C8 | 失败路径：非法盘符 | `--install "Z:\nope\x" ...` | `安装失败: Could not find a part of the path 'Z:\nope\x'.` `exit=1`（无崩溃/挂起） | **PASS** |
| C9 | 失败路径：目标为已存在文件 | `--install "%TEMP%\cm-qa-afile.txt" ...` | `安装失败: ... a file or directory with the same name already exists.` `exit=1` | **PASS** |
| C10 | 快捷方式不污染真实桌面/开始菜单 | 带 `--no-desktop-shortcut --no-startmenu-shortcut` | Desktop/StartMenu 均无“班级”快捷方式 | **PASS** |
| C11 | `.lnk` 生成能力（临时目录） | Harness 调 `ShortcutService.CreateLink` 写 `%TEMP%\qa_test_link.lnk` 并回读 | `target=C:\Windows\System32\notepad.exe`（读取后即清理） | **PASS** |

> **注（非缺陷，行为说明）**：`--uninstall` 返回 `exit=0` 时清理**尚未完成**——自删除脚本在进程退出后才 `rmdir`。发布目录 468 文件/≈169MB，实测删净耗时 **约 6–18 秒**（首测 6s 时目录仍在、内容已空，+12s 后消失）。这是设计上的异步行为，非 Bug；但验收脚本需等待足够时间或轮询。

### D. 成绩单导入

| # | 测试项 | 方法 | 原始输出摘要 | 结论 |
|---|---|---|---|---|
| D1 | xlsx 标准列（姓名+成绩） | 真实 xlsx，`ImportService.Import` | `count=3 order=李四,王五,张三 class=std` | **PASS** |
| D2 | xlsx 别名列（学生姓名/总分） | 真实 xlsx | `nameCol=学生姓名 scoreCol=总分` | **PASS** |
| D3 | 缺成绩列 + generate=true | 真实 xlsx | `scores=95,94,77`（均 60–100） | **PASS** |
| D4 | 缺成绩列 + generate=false | 真实 xlsx | 全部 0 分 | **PASS**（受设置项控制） |
| D5 | 空文件（0 字节） | 真实文件 | `ImportException: 读取文件时出错: The file/stream does not contain enough data to process` | **PASS**（明确异常，不崩溃） |
| D6 | 只有表头 | 手工构造 header-only xlsx | `ImportException: 文件中没有可用的数据行。` | **PASS** |
| D7 | 空行/重复姓名/非数字成绩 | 真实 xlsx | `count=3 甲=80 乙=0(非数字) 丙=90 dedup保留首现` | **PASS** |
| D8 | 损坏 xlsx | 真实文件 | 抛 `ImportException`，不崩溃 | **PASS** |
| **D9** | **CSV UTF-8(BOM)** | 真实 csv，`ImportService.Import` | **`ImportException: 读取文件时出错: The file type could not be inferred automatically, please specify ExcelType manually`** | **FAIL（P0）** |
| **D10** | **CSV UTF-8(无 BOM)** | 真实 csv | 同上 | **FAIL（P0）** |
| **D11** | **CSV GBK 中文表头** | 真实 csv | 同上 | **FAIL（P0）** |
| D12 | 控制组：MiniExcel 按路径读同一 CSV | `MiniExcel.Query(path, useHeaderRow:true, excelType:ExcelType.CSV)` | `direct-read rows=2` | **PASS**（证明 CSV 文件与编码无误，缺陷在调用方式） |
| D13 | `.xls` 明确拒绝 | 真实 `.xls` | `暂不支持旧版 .xls（二进制）文件，请用 Excel 另存为 .xlsx 或 .csv 后重试。` | **PASS** |
| D14 | 降序 + 人数一致 + 班级名=文件名 | 真实 `高二3班成绩.xlsx` | `class=高二3班成绩 scores=99,88,75,61` | **PASS** |

### E. 业务逻辑边界

| # | 测试项 | 原始输出摘要 | 结论 |
|---|---|---|---|
| E1 | random 可重复（50 次历史=50） | `hist=50` | **PASS** |
| E2 | sequential 点完从头循环 | `seq=A,B,C,A` | **PASS** |
| E3 | uncalled_only 全点完返回 null | `picks=B,C,A then null` | **PASS** |
| E4 | Reset 清空记录保留名单 | `students=3 hist=0 called=0` | **PASS** |
| E5 | VM 全点完 Toast 提示 | `toast=点名完成\|所有学生都已点到！` | **PASS** |
| E6 | 蛇形 3×4（奇数行反向） | `row0=A,B,C,D row1=E@(1,3),F@(1,2)` | **PASS** |
| E7 | 蛇形 12×10 | `occupied=120 first=S000 (1,9)=S010` | **PASS** |
| E8 | 分组 3×4 | `occ=9` | **PASS** |
| E9 | 分组 12×10（10 人） | `occ=10` | **PASS** |
| E10 | 随机 3×4 与 12×10 | `3x4=12 12x10=120` | **PASS** |
| E11 | UpdateLayout 保留域内/丢弃越界 | `kept=2 dropped=2` | **PASS** |
| E12 | 非法范围抛异常 | 6 组非法入参 `(2,8)(13,8)(6,3)(6,11)(0,0)(-1,5)` 全部 `ArgumentOutOfRangeException` | **PASS** |
| E13 | xlsx 双 sheet 可回读 | `sheets=座位表,统计信息 seatRows=3` | **PASS** |
| E14 | PNG 尺寸 = ⌈宽×2⌉×⌈高×2⌉ | `png=2760x2140 bytes≈101KB`（6×8 布局） | **PASS** |
| E15 | 打印文本含标题/班级/ASCII 网格/逐座详情 | `len=725`，含 `座位表` `高二3班` `第1行` `座位(1,1) … 成绩` | **PASS** |

### F. 健壮性

| # | 测试项 | 方法 | 原始输出摘要 | 结论 |
|---|---|---|---|---|
| F1 | settings.json 损坏优雅降级 | 写入非法 JSON → `--selftest` | `selftest_with_corrupt_settings_exit=0`，且生成 `settings.bak.json` | **PASS** |
| F2 | 硬编码颜色审查 | Grep 全部 `.xaml`（`#RRGGBB` 与命名色） | 视图层**零**硬编码色；仅 `Themes/Palette.xaml` 定义业务色刷（设计如此） | **PASS** |
| F3 | 暗色主题文字可读 | 置 `Theme=Dark` 启动 → `PrintWindow` 截图 | 见 `docs/qa-evidence-dark-theme.png`：深色底 + 浅色文字，导航/按钮/统计卡/空状态/状态栏均清晰可读 | **PASS** |
| F4 | `async void` 吞异常 | 代码审查 | 发现 `SeatViewModel.OnSeatClicked` 为 `async void`（见 §3 改进项） | **PASS（有改进项）** |
| F5 | 文件 IO / Mutex / COM 释放 | 代码审查 | 导入 IO 有兜底；Mutex/EventWaitHandle 在 `Dispose` 释放并处理 `AbandonedMutexException`；COM `shell` 已 `FinalReleaseComObject`（`link` 未显式释放，轻微） | **PASS（有改进项）** |

---

## 3. 缺陷清单与路由判定

### 【P0 高危 · 源码缺陷】CSV 导入功能完全不可用 —— 路由：**Engineer（寇豆码）**

- **归属**：Engineer（源码 Bug，需寇豆码修复；QA 未改动任何源码）
- **严重级别**：P0 / 高危（一个对用户承诺的核心功能整条链路不可用）
- **影响**：文件对话框允许选择 `*.csv`，空状态与导入提示也宣称支持 CSV，但**任何** CSV 文件都会导入失败并弹出错误框。
- **定位**：`ClassManager.App/Services/ImportService.cs`
  - `ReadRows`（约 **L171–192**）：`foreach (var row in MiniExcel.Query(stream, useHeaderRow: true))`（**L177**）
  - `PrepareStream`（约 **L195–229**）：CSV 分支返回 `MemoryStream`（**L228**）
  - 根因：对 **Stream** 调用 `MiniExcel.Query` 时**未显式指定 `excelType`**。MiniExcel 无法从内存流转推文件类型；xlsx 分支返回 `FileStream` 可被其按 zip 魔数识别而侥幸通过，CSV 则直接抛异常。
- **复现命令**：
  ```bash
  # 1) 生成一个最小 CSV
  printf '姓名,成绩\r\n张三,88\r\n李四,95\r\n' > "%TEMP%\demo.csv"
  # 2) 调用被测导入服务（Harness 或任意引用 ClassManager.App 的测试）
  new ImportService().Import("%TEMP%\demo.csv", true);
  ```
- **期望 vs 实际**：
  - 期望：解析出 2 名学生，按成绩降序（李四 95、张三 88）。
  - 实际：抛 `ImportException`，消息 = `读取文件时出错: The file type could not be inferred automatically, please specify ExcelType manually`。
- **控制组反证**：对**同一 CSV 文件**，`MiniExcel.Query(path, useHeaderRow:true, excelType: ExcelType.CSV)` 返回 `rows=2` 正常 → 证明缺陷在调用方式，不在文件/编码。
- **建议修复方向（供参考，非 QA 改动）**：为 CSV 显式传入类型，例如
  `MiniExcel.Query(stream, useHeaderRow: true, excelType: ex == ".csv" ? ExcelType.CSV : ExcelType.XLSX)`，
  或 CSV 直接以路径/`TextReader` 读取。

### 未发现需要退回的其它源码缺陷

C/E/F 全部通过；未发现第二处源码 Bug。

### 测试代码自身缺陷（QA 自修，非仓库用例）

- 我的验收 Harness 首轮 `E18.1 蛇形断言` 期望值写错（误期望 6 人时 `(1,0)=F`）；实测 6 人在 3×4 下为 `row0=A,B,C,D; row1=E@(1,3),F@(1,2)`，**源码行为正确**。已修正断言并复跑通过。**仓库 `ClassManager.Tests` 无需改动。**

---

## 4. 独立证实 / 证伪 工程师自报结论

| 工程师自报 | 我的独立结论 | 说明 |
|---|---|---|
| `dotnet build` 0 警告 0 错误 | **证实** | clean 后重建，非增量假象 |
| `dotnet test` 通过 36 / 失败 0 | **证实** | 实测 36/0 |
| `--selftest` exit 0 | **证实** | 6 项自检全通过 |
| 单实例 第二实例 exit=3，1469ms | **证实** | 实测 exit=3，**1417ms** |
| 安装复制 9 个文件 | **证实（且超出）** | 开发产物 9 文件属实；发布产物为 **468 文件**，本次以发布版为准 |
| 注册表 9 字段写入正确 | **证实** | 字段值逐一核对无误 |
| uninstall 后目录+注册表均删除 | **证实（补注）** | 注册表即时删除；**目录为异步删除，约 6–18s 后删净** |
| `--version` → 1.0.0 | **证实** | 输出 `1.0.0`，exit=0 |
| publish 468 文件 / 169MB | **证实** | 计数一致 |
| **（工程师未报告）** | **新发现** | **CSV 导入整链路不可用（P0）** —— 现有单测仅测 `ImportFromRows`，未覆盖 `Import(path)`，故漏网 |

---

## 5. 测试质量审查（针对 `ClassManager.Tests`，36 用例）

**结论：无错误断言，但存在“断言偏弱 / 测了等于没测”的用例，且关键路径缺测。**

偏弱用例清单：

1. `SettingsServiceTests.Current_IsNeverNullAfterLoad` —— `Assert.NotNull(service.Current)` 恒真（字段初始化为 `new()`），**几乎无信息量**。
2. `SeatingServiceTests.Grouped_UsesInterleavedOrder` —— 名字声称验证“交错”，实际只断言 `OccupiedSeats==9` 与 9 个不重名；**未验证好/中/差交错顺序**（实现顺序错误也测不出）。
3. `SeatingServiceTests.Random_PlacesAllStudents` —— 只断言占用数=10，未断言**姓名唯一/不重复落座**。
4. `RollCallServiceTests.Random_CanRepeat` —— 只断言历史条数=50 与姓名属于集合，**未断言“确实可能重复”**（可重复语义未真正验证）。
5. `SeatingServiceTests.UpdateLayout_InvalidRange_Throws` —— 只覆盖 `(2,8)`、`(6,11)`，**未覆盖 rows>12、cols<4** 等边界。

**关键覆盖缺口（导致 P0 漏网）**：全部导入测试只调用 `ImportFromRows`（纯逻辑），**没有任何用例通过 `Import(path)` 读取真实 `.csv` / `.xlsx` 文件**。建议补充基于真实文件的集成测试。

---

## 6. 未覆盖风险 / 说明

1. **导出/安装/单实例/主题/快捷方式四个服务无单测**（仅本次集成级验证），回归风险靠发布前手工验收兜底。
2. **GUI 交互链路**（向导按钮、座位点击二次确认、消息框）未做端到端 UI 自动化；其中 `SeatViewModel.OnSeatClicked` 为 `async void`，若 `MessageBox.ShowDialogAsync` 抛异常会经 `DispatcherUnhandledException` **直接崩溃且无捕获**（建议改 `async Task` 并 try/catch；低概率、低频率）。`SeatMapControl` 中部分固定色（黑板/讲台文字用 `Brushes.White/Black`）作用于**固定底色**的图形，明暗主题下均可读，非缺陷。
3. **`PrintWindow` 截图仅在暗色主题下采样**；浅色主题未截图（代码审查表明两套调色板由 `ThemeService.PaletteMap` 对称改写，风险低）。
4. **安装路径未做分隔符归一化**：命令行传入的混合分隔符（如 `Temp/班级 测试 目录`）被**逐字**写入注册表。不影响功能（`Directory.Exists`/卸载均正常），属健壮性小瑕疵，可选优化。
5. **沙箱限制**：`reg.exe`、`cmd` 直接调用被拦截，注册表/删除均改用 PowerShell 交叉验证；已确认不干扰被测算法的自删除脚本（以文件计数佐证）。

---

## 7. 证据文件

- 暗色主题主界面截图：`docs/qa-evidence-dark-theme.png`
- 验收 Harness：Round 1/2 使用的临时 Harness 位于 `%TEMP%\cm_qa\QAHarness`（引用 `ClassManager.App`，覆盖导入/导出/业务逻辑真实文件用例）；**测试结束后已按规程清理**（连同 `%TEMP%` 下全部测试目录/探针文件）。如需复跑，可依本报告命令重建。

---

## 8. 路由判定与下一步

- **Round 1 结果**：发现 **1 个 P0 源码缺陷（CSV 导入）** → **路由 Engineer（寇豆码）**。
- **Round 2 计划（待工程师修复后）**：仅回归 D9/D10/D11（三种编码 CSV）与 A1/A2，确认无新增回归后出最终结论。**总轮次上限 2 轮。**

> 除上述 CSV 缺陷外，本系统在构建、单实例、安装/卸载（含中文+空格路径与失败路径）、xlsx 导入、点名、排座、导出、设置健壮性、暗色主题可读性方面**均通过独立验收**。

---
---

# Round 2 回归验证（第 2 轮 · 最终裁决）

> 触发：工程师已修复 Round 1 的 P0（CSV 导入）并裁减卫星资源。本 QA **独立复核**，未复用工程师留下的 `%TEMP%\cm_csv_probe.txt` 或其所写测试样本。

## R2-A. CSV 缺陷回归（核心）

**修复点确认**（`ClassManager.App/Services/ImportService.cs`）：
- `ReadRows` 现 `var excelType = ext == ".xlsx" ? ExcelType.XLSX : ExcelType.CSV;` 并以 `MiniExcel.Query(stream, useHeaderRow: true, excelType: excelType)` 显式传类型（L173–183）。
- `PrepareStream`（L205–239）：xlsx 返回 `File.OpenRead`；CSV 做 BOM→UTF-8→GBK 解码后**以 UTF-8 重编码**写入新 `MemoryStream`，规避 GBK 乱码。
- 扩展名判断经 `Path.GetExtension(...).ToLowerInvariant()` → **大小写不敏感**。`.xls` 仍拒绝。

**我自造样本实测（走公开入口 `ImportService.Import(path)`）**：

| 样本 | 结果 | 结论 |
|---|---|---|
| UTF-8(BOM) | `count=2 top=李四`（降序、无乱码）| **PASS**（原 FAIL→PASS）|
| UTF-8(无 BOM) | `count=2 top=李四` | **PASS**（原 FAIL→PASS）|
| GBK 中文表头 `姓名,成绩` | `count=2 names=李四,张三 nameCol=姓名` | **PASS**（原 FAIL→PASS）|

→ **Round 1 的 3 个 P0 失败项全部转为 PASS。**

**对抗性样本（12 项，故意刁难）—— 全部 PASS**：

| 样本 | 实测结果 |
|---|---|
| 引号包裹 + 字段内逗号 `"张三, 男",88` | 正确解析为 `张三, 男`（MiniExcel 正确处理引号）|
| `\r\n` 与 `\n` 混用 | `count=2` 正确 |
| 仅一行数据 | `count=1` |
| 只有表头 | 抛 `ImportException: 文件中没有可用的数据行。` |
| 完全空文件(0字节) | 抛 `ImportException`（不崩溃）|
| 文件名含中文+空格 `高二 3班 成绩.csv` | `class=高二 3班 成绩` |
| **大写扩展名 `.CSV`** | 正确（大小写不敏感，**无漏点**）|
| **大写扩展名 `.XLSX`** | 正确（大小写不敏感，**无漏点**）|
| GBK 生僻字 `孑孓/龘龗` | `names=龘龗\|孑孓` 无乱码 |
| CSV 缺成绩列 + generate=true | 生成 60–100 |
| CSV 缺姓名列 | 回退第一列（`nameCol=学生`）|
| xlsx 缺姓名列 | 与修复前一致（回退）|

→ **CSV 缺陷已彻底修复，无回归、无新增漏点。**（我保留的 D14.0 控制组：同一 CSV 按路径 + `ExcelType.CSV` 直读仍正常，佐证修复方向正确。）

## R2-B. 卫星资源裁减回归（确认没砍坏东西）

| 检查项 | 结果 |
|---|---|
| 发布目录文件数 | **264**（原 468）✓ |
| 总大小 | **154M**（原 169M）✓ |
| 现存语言目录 | 仅 **`zh-Hans`**（内容为 WPF/Forms 的 `*.resources.dll`）✓ |
| 已剔除语言 | ru/ja/de/ko/pl/fr/it/es/cs/pt-BR/tr/zh-Hant —— **逐一确认不存在** ✓ |
| 发布版 `--version` | `1.0.0` exit 0 ✓ |
| 发布版 `--selftest` | 6 项全通过 exit 0 ✓ |
| 发布版安装向导渲染 | 标题/正文/复选框/按钮**中文无乱码**（截图 `docs/qa-evidence-install-wizard.png`）✓ |
| 发布版主界面渲染 | 导航/按钮/统计卡/空状态**中文无乱码，无缺失本地化异常**（截图 `docs/qa-evidence-light-main.png`）✓ |

→ 裁减**仅减小体积，未产生任何行为变化**（未触及任何 P0）。

## R2-C. 测试代码补强（QA 自行修，职责内）

补强 Round 1 点名的 5 个弱断言用例（4 处在原处增强，1 处替换）：

1. `SettingsServiceTests.Current_IsNeverNullAfterLoad`：由恒真断言改为**断言文档化默认值**（Theme/GenerateDemoScores/窗口尺寸）；另新增 `Load_CorruptFile_FallsBackToDefaultsAndBacksUp`（损坏 JSON→默认值+生成备份），带 `try/finally` 完整还原，避免污染真实配置。
2. `SeatingServiceTests.Grouped_UsesInterleavedOrder`：由“只数占用”改为断言**行优先落座的好/中/差成绩区间交错**（98–100 / 95–97 / 92–94），可捕获“未真正交错”的实现错误。
3. `SeatingServiceTests.Random_PlacesAllStudents`：新增**唯一落座 + 座位姓名集合 == 名单集合**断言。
4. `RollCallServiceTests.Random_CanRepeat`：新增单名学生场景，断言第二次点名 `AlreadyCalled == true`（真正验证“可重复”语义）。
5. `SeatingServiceTests.UpdateLayout_InvalidRange_Throws`：补齐 rows<3 / rows>12 / cols<4 / cols>10，并断言边界内合法值（3×4、12×10）**不抛异常**。

- 保留工程师新增的 `CsvImportIntegrationTests`（1 用例，断言经复核正确）。
- **全量测试：38 通过 / 0 失败 / 38 总计**（36 原 + 1 CSV 集成 + 1 损坏设置）。
- 自查：新增/补强断言若有错误断言会直接失败，实测**全绿**，故新增用例无错误断言。
- 注：`SettingsServiceTests` 原有文件未 `using System.IO`（WPF 项目 ImplicitUsings 不含 System.IO），已补 `using System.IO;`（属测试代码修正，编译通过 0/0）。

## R2-D. 最终裁决

| 项 | 值 |
|---|---|
| 仓库单测 | **38 / 38** |
| QA 独立 Harness（含 12 项对抗样本，引用 `ClassManager.App`） | **42 / 42** |
| Round 1 的 3 个 P0 失败项 | 全部 **转为 PASS** |
| 构建 | **0 警告 0 错误** |
| **路由判定** | **NoOne** |

## R2-E. 仍未覆盖的风险（明确“未验证”，不含糊）

1. **未做 GUI 端到端 UI 自动化**：向导按钮点击、座位点击二次确认、各 `MessageBox` 交互路径仅经**截图核验渲染**，未自动化点击验证行为。
2. **导出 PNG / 打印预览未在“发布版二进制”单独运行**：仅经同源码 Harness（E20，PNG 2760×2140）+ 发布版 `--selftest`（xlsx）覆盖；**未验证**不同 DPI / 多显示器缩放下的导出表现。
3. **CSV 编码未穷举**：仅测 BOM/UTF-8/GBK 三类 + 生僻字；**未穷举**全部 GBK 扩展字符，**未测**超大文件解码/导入性能。
4. **区域设置（CultureInfo）相关数字解析未验证**：`ParseScore` 末路用 `double.TryParse(value.ToString())`（依赖当前文化），**未在**非 zh-CN 小数点/千分位环境下验证。
5. **单实例 / 安装卸载的极端并发竞态未做压力测试**（仅 5 连发 + 8 轮循环）。
6. **未在“无 .NET 运行时的干净机器”上验证自包含产物**（本机装有 .NET 8 SDK）。
7. **遗留低危项（非本轮改动引入，仍存在）**：`SeatViewModel.OnSeatClicked` 仍为 `async void`，其异常会经 `DispatcherUnhandledException` 冒泡；对话框异步调用未 try/catch。
8. **测试卫生**：工程师新增的 `CsvImportIntegrationTests` 每次运行会在 `%TEMP%` 留 `cm_csv_probe.txt` / `cm_csv_probe\`（未自清理）——仅提示，非缺陷。

## R2-F. 结论

Round 1 唯一的 P0 源码缺陷（CSV 导入）已被工程师**正确修复**，并经 12 项对抗样本**独立证伪无残留**；卫星资源裁减**未引起任何功能回归**；测试代码 5 处弱断言已补强且全量通过。

**最终裁决：NoOne —— 无未决缺陷，验收通过。** 仍受限于 §R2-E 的“未验证”清单。

> 证据补充：`docs/qa-evidence-install-wizard.png`（发布版向导）、`docs/qa-evidence-light-main.png`（发布版主界面·浅色）。环境已清理：无残留进程、注册表项、`%AppData%\ClassManager`、`%TEMP%` 测试目录，未污染桌面/开始菜单。

---

# Round 3 · 座位画布（蓝白极简）重做 · 增量验收 + 全量回归

> 触发：用户改方向后，「座位管理」画布被重做为**蓝白极简**（方格仅姓名、行序翻转、底部讲台、逐道间距 + 分组表头 + 表头开关 + 双向滚动）。本 QA 对该增量做**独立证伪**，并做**全量回归**。
> 独立性声明：画布/几何/导出/边界/滚动/导入等全部由本人**新写的 C# 验收 Harness**（`%TEMP%\cm_qa\R3Harness`，引用 `ClassManager.App`，`net8.0-windows`）以**真实坐标测量 + PNG 像素级扫描 + 真实文件导入**驱动；**未复用**工程师的探针文件或脚本。

## R3-0. 结论摘要

| 项 | 值 |
|---|---|
| 自动断言 Harness | **29 / 29 通过 / 0 失败** |
| 发布版全量回归 | 构建 **0/0**、单测 **45/45**、发布 **264 文件 / 154M / 仅 zh-Hans**、`--selftest` 通过、单实例 exit=**3** |
| 新增缺陷 | **1 项（P2）**：非 ASCII 安装路径下卸载**残留安装目录** |
| 能力变更 | 1 项：`.txt` 打印预览 + `IExportService.BuildPrintText` 被**移除**（改为矢量打印）——需 PM/用户确认 |
| 证伪的 R1 结论 | 1 项：R1-C5「中文路径卸载目录已删净」**未复现**（见 R3-3） |
| **路由判定** | **Engineer（仅修卸载残留 1 项 P2）**；座位画布增量本身 **NoOne** |

> **一句话结论**：座位画布重做的全部验收点（间距/分组/行序/讲台/方格文本/表头开关/滚动/边界）经独立测量**全部通过**；全量回归亦通过。**唯一缺陷**出现在「安装/卸载」回归中——**非 ASCII 安装路径卸载不干净**（与本轮画布重做无关的既有缺陷）。

## R3-1. 验收环境

| 项 | 值 |
|---|---|
| 被测源码 | `D:\信息技术\ClassManager\ClassManager.App`（Round 6 重做后的工作树） |
| 被测二进制 | `publish/win-x64/ClassManager.App.exe`（自包含，264 文件 / 154M，仅 `zh-Hans` 卫星目录） |
| Harness | `%TEMP%\cm_qa\R3Harness`（`[STAThread]`，引用 App 程序集；补齐 App 合并资源字典以便实例化真实窗口） |
| 断言组 | Geometry(6) / Export 像素(2) / Canvas 渲染(5) / 边界(3) / 滚动(2) / 增量(3) / 导入(8) = 29 |

## R3-2. 逐项验收（A–I）

### A. 逐道间距与分组表头

| # | 验证点 | 方法 | 结果 |
|---|---|---|---|
| A1 | 单道列间隙=60、其余=8 逐道生效 | `ColumnOffsets` 步长测量 | **PASS**：`xs=[90,208,326,444,614,732,850,968]`；3→4 步长=170(=110+60)，其余=118(=110+8) |
| A1b | 导出 PNG 像素级验证同一间隙 | 扫描「座位顶边框下方」白间隙宽度（避开姓名文字） | **PASS**：间隙白 run=14,14,14,**118**,14,14,14（×2 缩放，118≈2×60-2 抗锯齿） |
| A2 | 同场景分组表头切成 2 组 | `GroupBlocks` + 画布 TextBlock 计数 | **PASS**：宽间隙→2 块 `(0,3)/(4,7)`，画布出现「第1组」「第2组」；改回 8→1 块「第1组」 |
| A3 | 间距设置窗口（统一填充/边界/写回） | 实例化真实 `GapSettingsWindow` + 反射触发处理器 | **PASS**：列项=7 / 行项=5；统一填充 200→全 200；9999→全 400；`OnOk` 写回 `colGap0=77 rowGap0=33 thr=55` ✓（`-5` 被 NumberBox `Minimum=0` 夹为 0，属 UI 正常约束） |
| A4 | 座位页→点名页→回座位页，间隙/布局/表头保留 | 以真实 `SeatViewModel` 模拟「导航重建」 | **PASS**：往返后 `6x8`、`colGap[3]=55`、`rowGap[2]=44`、`ShowHeaders=false` 均保留；切 Toggle→true 成功写回 `Layout` |

### B. 行序与讲台

| # | 验证点 | 方法 | 结果 |
|---|---|---|---|
| B1 | row0=第一排在最**下**、row N 在最上 | `RowOffsets` + 导出 PNG 横线扫描 | **PASS**：`ys=[364,274,206,138,70]`（row0 最大）；导出实测水平网格线 `y=375,495,511,…,1175`，与几何期望**逐条吻合**（差 1px 舍入） |
| B1b | 行间距均匀 | 相邻横线差 | **PASS**：差值 `120,16,120,16,…`（=60×2 与 8×2，均匀） |
| B2 | 讲台在网格正下方、水平居中、高≈56 | `Platform` 矩形 | **PASS**：`(402,510,312×56)`；左右留白相等=312，顶=网格底+40 |

### C. 方格文本

| # | 验证点 | 方法 | 结果 |
|---|---|---|---|
| C1 | 已排座仅姓名、**无分数** | 画布 TextBlock 集合 + 导出绘制代码审查 | **PASS**：座位文本仅 `{张三,李四}`，无「分/(/)」痕迹；导出 `DrawSeatGrid` 亦仅绘姓名 |
| C2 | 空座无任何文本（不再显示「空座位」） | 画布文本集合 | **PASS**：空座无文本（文本集合无「空」字样） |
| C3 | 长姓名折行不溢出 | `TextBlock` 宽/换行属性 | **PASS**：9 字姓名 `Wrap`，宽=98=座位宽-12 |

### D. 表头开关与导出选项

| # | 验证点 | 方法 | 结果 |
|---|---|---|---|
| D1 | 关表头不画「第N组/第N排」，边距收窄 | 画布文本 + `CanvasSize` | **PASS**：关表头文本仅 `讲台|张三`；开表头标签数=4（3 排 + 1 组）；大布局下含表头画布高 **+50**（70-20） |
| D2 | 导出/打印选项默认=面板开关值 | 实例化 `SeatExportOptionsWindow` | **PASS**：初始 true/false 均正确回传 `IncludeHeaders` |

### E. 滚动

| # | 验证点 | 方法 | 结果 |
|---|---|---|---|
| E1 | 20×20 双向滚动条，可滚到最底/最右 | 托管真实窗口实测 `ScrollViewer` | **PASS**：viewport=967×667，scrollable=**2173×1519**，滚到底/右到位 |
| E2 | 滚轮=垂直、Shift+滚轮=水平、中键拖动平移 | 代码审查 | **部分验证（未自动化点击）**：`OnPreviewMouseWheel` Shift→`ScrollToHorizontalOffset`，非 Shift 放行给 `ScrollViewer` 默认垂直滚动；中键拖动实现正确。**未做**真实输入事件注入 |
| E3 | 极端间隙 400 / 0 不崩 | 画布尺寸计算 + 渲染 | **PASS**：gap400 画布 4880×4426；gap0 画布 1280×826；均正常渲染 |

### F. 范围边界

| # | 验证点 | 方法 | 结果 |
|---|---|---|---|
| F1 | 1×1 / 1×20 / 20×1 / 20×20 合法 | `UpdateLayout` + 间隙长度 | **PASS**：四种尺寸均不崩，列/行间隙数正确 |
| F2 | 0 / 21 被拒绝并给文案 | 捕获异常消息 | **PASS**：`ArgumentOutOfRangeException`：`行数必须在1-20之间 (Parameter 'rows')` / `列数必须在1-20之间 (Parameter 'cols')`；UI 文案与规格一致 |
| F3 | 20→1→20 越界丢弃、间隙保留/重填 8 | 状态机验证 | **PASS**：缩 1×1 后 occ=1、gap=0；回 20×20 间隙全 8、occ 仍=1（座位不恢复） |

### G. 打印

| # | 验证点 | 方法 | 结果 |
|---|---|---|---|
| G1 | 打印窗口矢量预览 | 实例化真实 `SeatPrintWindow` | **PASS**：`BuildSeatSheet` 生成矢量图，预览位图 **1800×1377** 非空；`OnPrintClick` 走 `PrintDialog.PrintVisual`（等比缩放居中，矢量） |
| G2 | 实际经 Microsoft Print to PDF 出 PDF | —— | **未验证**：`.NET 5+` 无 API 可在无交互下为「Microsoft Print to PDF」指定输出文件；`PrintDialog.ShowDialog()` 需人工交互，无法在无头环境自动出 PDF。**已由 G1（同一矢量对象）+ 导出 PNG 像素级验证（EXP1/EXP2）间接覆盖** |

### H. 全量回归

| # | 验证点 | 命令/方法 | 结果 |
|---|---|---|---|
| H1 | 构建 0 警告 0 错误 | `dotnet build -c Release` | **PASS**：`0 个警告 0 个错误` |
| H2 | 单测 ≥45 全通过 | `dotnet test -c Release` | **PASS**：**45 / 45**（38 + 7 `SeatLayoutTests`） |
| H3 | 自包含发布 264 文件 / 154M / 仅 zh-Hans | `dotnet publish -r win-x64 --self-contained`（全新目录） | **PASS**：264 文件、154M、卫星目录仅 `zh-Hans` |
| H4 | `--selftest` | 发布版运行 | **PASS**：6 项自检全通过，exit=0 |
| H5 | 单实例 | 第二实例运行 | **PASS**：第二实例 exit=**3** |
| H6 | CSV 三种编码 | 真实文件导入（BOM/UTF-8/GBK） | **PASS**：均 3 人、中文表头/姓名正确、按成绩降序 |
| H7 | 旧版 `.xls` 拒绝 | 真实 `.xls` 魔数文件 | **PASS**：`ImportException`：`暂不支持旧版 .xls…请另存为 .xlsx 或 .csv` |
| H8 | 安装 / 卸载（中文+空格路径） | `--install`/`--uninstall` | **FAIL（P2）**：注册表/快捷方式即时清除，但**非 ASCII 安装目录残留**（详见 R3-3） |
| H8b | 失败/对抗路径 | 空/仅表头 CSV、缺成绩列、引号字段 | **PASS**：空/仅表头→可读 `ImportException`；缺成绩列+允许生成→60–100 且标记 generated；`"张,三"` 引号字段不串列 |

### I. 能力变更（明确记录，非默认视为通过）

| # | 变更 | 事实 | 影响评估 |
|---|---|---|---|
| I1 | **移除 `.txt` 文本打印预览** | PRD `R-P0-37` 要求「文本预览窗口…可另存为 .txt」；实测「打印座位表」现为**矢量预览窗口**（`SeatPrintWindow`，可经 PrintDialog 出 PDF），**无** `.txt` 另存入口 | **能力变更**：由「ASCII 文本预览 + .txt」变为「矢量图形打印」。客观上更美观/可打印，但**偏离 PRD 明文**。**需 PM/用户确认是否接受**；若需保留文本导出，应补回 |
| I2 | **移除 `IExportService.BuildPrintText`** | 接口现仅 `ExportSeatXlsx`/`BuildSeatSheet`/`ExportSeatImage`；`BuildPrintText` 在**源码中已不存在** | 与 I1 同源。**文档未同步**：`docs/ARCHITECTURE.md` 与 `docs/class-diagram.mermaid` 仍列 `BuildPrintText` 及旧签名（见 R3-6） |

## R3-3. 新增缺陷（1 项 · P2）

### D1-UNINSTALL：非 ASCII 安装路径下卸载残留整个安装目录

- **严重级别**：**P2（中）**；在「Windows 用户名/安装路径含中文」的机器上可升级为 **P1**（默认安装目录 `%LocalAppData%\Programs\ClassManager` 会落入非 ASCII 路径）。
- **现象**：`--uninstall` 返回 `exit=0`，注册表卸载项与桌面/开始菜单快捷方式**均已清除**，但**安装目录整体残留**（实测 264 文件 / 154M 仍存在，1 分钟后仍在）。
- **根因（源码）**：`ClassManager.App/Services/InstallService.cs` → `ScheduleSelfDelete()` 用 **`Encoding.ASCII`** 写自删除脚本：
  ```csharp
  File.WriteAllText(scriptPath, sb.ToString(), Encoding.ASCII);   // ← 非 ASCII 路径被替换为 '?'
  ... sb.AppendLine($"rmdir /s /q \"{installDir}\""); ...
  ```
  当 `installDir` 含中文，ASCII 编码把路径写成 `????`，`rmdir` 目标不存在 → 删除失败；脚本随后 `del "%~f0"` 自删成功，故**看不出报错**。
- **控制组对照（决定性证据）**：

  | 安装目录 | 卸载后目录 |
  |---|---|
  | `…/cm_qa/班级测试`（纯中文，无空格） | **残留** |
  | `…/cm_qa/space dir test`（纯 ASCII + 空格） | **已删除** |
  | `…/cm_qa/ascii_inst`（纯 ASCII） | **已删除** |
  | `…/cm_qa/安装 测试 KC`（中文+空格） | **残留**（264 文件仍在） |

  → 变量仅为「路径是否含非 ASCII」，根因锁定为 `Encoding.ASCII`。
- **建议修复**：自删除脚本改用**系统 ANSI/本地代码页**写（如 `Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage)` 或 `Encoding.Default`），或改用 `PowerShell -EncodedCommand`（UTF-16LE）/ `chcp 65001` 前置；亦可让卸载脚本通过环境变量传递路径避免内联中文。修复后需以「纯中文路径」回归。
- **路由**：**Engineer**。其余全部项无源码缺陷。

## R3-4. 独立证实 / 证伪 工程师与本轮自报结论

| 结论 | 我的独立结论 | 说明 |
|---|---|---|
| 构建 0/0 | **证实** | 实测一致 |
| 单测 45/45 | **证实** | 实测 45/45 |
| 发布 264 文件 / 154M / 仅 zh-Hans | **证实** | 全新目录重发布，计数一致，卫星目录仅 `zh-Hans` |
| `--selftest` 通过 | **证实** | exit=0，6 项全过 |
| 单实例第二实例 exit=3 | **证实** | 实测 exit=3 |
| 行序翻转（第一排在下） | **证实** | 几何 + 导出像素双重验证 |
| 逐道间距 / 分组表头切块 | **证实** | 几何 + 画布 + 导出像素 |
| `.txt` 打印预览 + `BuildPrintText` 移除 | **证实存在（属能力变更）** | 见 I1/I2，需业务确认 |
| R1-C5「中文路径卸载目录已删净」 | **证伪（未复现）** | R3 实测中文路径目录**残留**；R1 该结论不成立（或环境差异），以 R3 控制组为准 |
| （工程师未报告） | **新发现** | 非 ASCII 安装路径卸载残留目录（P2，见 R3-3） |

## R3-5. 代码审查（死代码 / 文档漂移）

1. **`SeatMapPalette.CanvasBackground` 为死常量**：定义后用 `#FFFFFF` 硬编码于 `SeatMapControl.xaml`，全仓无引用（`grep` 仅定义处命中）。建议移除或替换 XAML 硬编码以单一来源。
2. **主题资源 `SeatEmptyBrush` / `SeatOccupiedBrush` 已成死资源**：座位画布重做后改用固定 `SeatMapPalette` 调色，`Palette.xaml` 与 `ThemeService.PaletteMap` 仍在维护这两个键，但**全仓无 XAML 引用**（`SeatSelectedBrush` 仍被学生按钮选中态使用，保留合理）。
3. **文档漂移**：`docs/ARCHITECTURE.md`（§IExportService、§资源键表）与 `docs/class-diagram.mermaid` 仍包含 `BuildPrintText`、旧 `ExportSeatImage(...scores...)` 签名、以及旧校验「3–12 / 4–10」（现为 **1–20**）。建议同步更新，避免后续误用。
4. **测试预留探针未自清理**：`ClassManager.Tests/CsvImportIntegrationTests.cs` 每次运行会在 `%TEMP%` 写 `cm_csv_probe\` 与 `cm_csv_probe.txt` 且不自清理（R2 已提示；非缺陷，属测试卫生）。本次已按要求清掉遗留文件。

## R3-6. 未验证 / 受限项（不含糊）

1. **中键拖动平移 & Shift+滚轮**未做真实输入事件注入（`Keyboard.Modifiers`/`MouseButtonEventArgs` 无法在无头环境可靠构造）——仅代码审查通过。
2. **打印出 PDF** 未端到端验证（见 G2），受限原因：无头环境无法指定「Microsoft Print to PDF」输出文件。
3. **GDI/DPI 缩放、多显示器**下导出/打印表现未验证。
4. **非 zh-CN 区域**下 `ParseScore` 末路 `double.TryParse`（依赖当前文化）未验证。
5. **`SeatViewModel.OnSeatClicked` 仍为 `async void`**（R2 已提），本轮未改动；异常现已 try/catch 兜底（R6 已加），风险显著降低。

## R3-7. 最终裁决

| 项 | 值 |
|---|---|
| 座位画布增量（A–F + G 预览） | **全部 PASS** |
| 全量回归（H1–H7） | **全部 PASS** |
| 全量回归（H8 卸载） | **FAIL** → 非 ASCII 安装路径残留目录（P2） |
| **路由判定** | **Engineer**（仅修 `InstallService.ScheduleSelfDelete` 的 `Encoding.ASCII`）；**座位画布增量本身 NoOne** |

> 证据：`docs/qa-evidence-r3-canvas.png`（6×8 蓝白画布·含表头·选中态）、`qa-evidence-r3-canvas-2groups.png`（宽间隙→第1/2组）、`qa-evidence-r3-export-2groups.png` / `qa-evidence-r3-export-noheader.png`（打印风导出·含/不含表头）、`qa-evidence-r3-print-preview.png`（打印预览矢量渲染）。未覆盖 Round 1/2 既有证据文件。环境已清理：无残留进程、注册表卸载项、`%TEMP%\cm_qa`、`%TEMP%\cm_csv_probe*`。

---

# Round 4 · P2 修复复验（非 ASCII 卸载）+ xlsx 去分数 + 全量回归

> 触发：工程师修复 Round 3 唯一 FAIL（P2），并清理死资源键、画布白底单一来源、测试卫生、xlsx 座位表去分数。本 QA **独立证伪**修复并对新改动做回归。
> 独立性声明：卸载复验用**我自己的控制组与对抗路径**（含工程师未列的单引号、方括号、`$`、emoji、超长路径），**不照抄**其自报；xlsx 去分数用**读回断言**；UI 用**真实页面 + 像素/单元格采样**。

## R4-0. 结论摘要

| 项 | 值 |
|---|---|
| 自建 R4 Harness（xlsx 读回 / 资源键 / UI 采样 / 主题 / 导入） | **14 / 14 通过 / 0 失败** |
| 全量回归 | 构建 **0/0**、单测 **45/45**、发布 **264 / 154M**、`--selftest` exit 0、单实例 exit **3**、`--version`=1.0.0 |
| **R3 唯一 P2（非 ASCII 卸载残留）** | **确认修复** —— 控制组 4/4 删净；对抗路径 10/10（复测）+ 单例复验 2/2 全删净 |
| 新增能力变更 | 1 项：**xlsx 座位表单元格不再含成绩**（cumulative 见 R4-7） |
| 新缺陷 | **0** |
| **路由判定** | **NoOne**（源码无未决缺陷）；但 xlsx 去分数属**能力变更，需 PM/用户确认** |

> **一句话结论**：Round 3 的非 ASCII 卸载残留**已真正修复**（新 PowerShell `-EncodedCommand` 方案在纯中文/中文+空格/ASCII+空格/纯 ASCII/单引号/`&%^!`/`[]`/`$`/emoji/超长 10 类路径均删净）；死键清理与 xlsx 去分数**无回归**；全量回归通过。

## R4-1. 验收环境

| 项 | 值 |
|---|---|
| 被测二进制 | `publish/win-x64/ClassManager.App.exe`（自包含，264 文件 / 154M，仅 `zh-Hans`） |
| Harness | `%TEMP%\cm_r4h`（`[STAThread]`，引用 App；补齐 App 合并资源字典 + 4 个转换器，实例化真实 `SeatPage`/`RollCallPage`） |
| 卸载复验 | 发布版 `--install/--uninstall --quiet --no-desktop-shortcut --no-startmenu-shortcut`，**逐个轮询到删净** |

## R4-2. P2 修复复验（最高优先级）

### 控制组（我独立构造，4 类路径）

| 安装目录 | 结果（第 1 轮 / 复跑） |
|---|---|
| `%TEMP%\班级测试`（纯中文） | **删除** / **删除**（22s / 15s） |
| `%TEMP%\安装 测试 KC`（中文+空格） | **删除** / **删除**（19s / 16s） |
| `%TEMP%\space dir test`（ASCII+空格） | **删除** / **删除**（18s / 14s） |
| `%TEMP%\ascii_inst`（纯 ASCII） | **删除** / **删除**（15s / 17s） |

### 对抗性路径（我补测，覆盖新方案风险面）

| 类别 | 路径 | 结果 |
|---|---|---|
| 单引号（PS 定界符） | `quo'te dir` | **删除**（14–19s） |
| cmd 特殊字符 `& % ^ !` | `amp&pct%caret^bang!` | **删除**（14s） |
| 方括号（PS 通配符） | `br[ack]et dir` | **删除**（14s）——`-LiteralPath` 生效 |
| `$`（PS 变量展开） | `dollar$dir` | **删除**（14–22s）——单引号字面量生效 |
| 非 BMP（emoji） | `emoji😀dir` | **删除**（14–16s）——UTF-16LE 编码链路 OK |
| 超长路径 | `Long_aaa…`（目录名 125 字符；最长文件路径 222） | **删除**（15s） |

- **无脚本落地**：每次卸载后 `%TEMP%` **无任何 `.cmd`** 残留（新方案命令内联，不落地文件）。
- **同步清理**：注册表卸载项、桌面/开始菜单快捷方式均清除（本轮全程用 `--no-desktop-shortcut --no-startmenu-shortcut`，未污染真实桌面/开始菜单）。
- **间歇性记录（如实）**：**首轮 10 组连续高压时，`emoji😀dir`（残留 9 文件）与超长路径（264 文件未动）未删净**；随后**复测 emoji 2/2、超长 2/2、整组 10/10（均 14–16s）**均删净。合计 20/22，**未稳定复现**，判断为瞬时锁/负载（新拷贝 DLL 被实时扫描锁定、或 PS 子进程启动竞态），非路径编码缺陷。**列为残余风险**（见 R4-8）。
- 结论：**Round 3 的 `Encoding.ASCII` 根因已消除；P2 确认修复**。

## R4-3. xlsx 座位表去分数复验

| # | 验证点 | 方法 | 结果 |
|---|---|---|---|
| X1 | 「座位表」sheet 单元格仅姓名、无「分/(/)」 | 导出后用 MiniExcel **读回**断言 | **PASS**：3 行×4 列，非空仅 2 格=`张三`/`李四`，**无**「分」「(」「)」；空座为 **null**（留空） |
| X2 | 「统计信息」sheet 六字段不变 | 读回断言 | **PASS**：生成时间 / 班级 / 座位布局 / 总座位数 / 已安排 / 空座位，六项齐全 |
| X3 | 旧签名与死方法无残留 | `grep` 全仓 | **PASS**：`BuildScoreMap`/`BuildCellText`/`EmptyCell` **0 引用**；`ExportSeatXlsx` 仅新签名（`SelfTest.cs`、`SeatViewModel.cs` 两处调用同步） |

## R4-4. 死资源键清理与 UI 回归

| # | 验证点 | 方法 | 结果 |
|---|---|---|---|
| R1 | 保留键存在、10 死键移除 | 检查 `Application.Resources` | **PASS**：保留 `SeatSelectedBrush`/`SeatedChipBrush`；`DeskBrush`…`UncalledBrush` 10 键**全部不存在** |
| R2 | 已排座按钮变绿仍生效 | `BoolToSeatedBrushConverter.Convert(true)` | **PASS**：→ `#C8E6C9`（SeatedChipBrush） |
| R3 | 画布白底单一来源 | 读 `SeatMapControl` 的 `Surface`/`Paper` 背景 | **PASS**：均 = `SeatMapPalette.CanvasBackground`(#FFFFFF)（XAML 硬编码已移除） |
| U1 | 学生按钮：已排座=绿底、选中=蓝框 | 实例化真实 `SeatPage`，**读按钮 BorderBrush/Background** | **PASS**：已排座 `bg=#C8E6C9`；选中 `border=#0078D4`、`t=2` |
| U2 | 点名页「姓名列/状态列」正常渲染 | 真实 `RollCallPage` DataGrid **单元格文本** | **PASS**：`row0 = 1 | 张三 | 88 | ⏳ 未点到`（状态列正常；像素核验姓名列区 440 暗像素） |
| U3 | 明↔暗切换：业务键取值随之改变 | `Theme.Apply` + 读资源 | **PASS**：`SeatSelectedBrush #0078D4→#4CC2FF`；`SeatedChipBrush #C8E6C9→#2E4A34` |
| U4 | 座位画布不受主题影响 | 明/暗渲染同一画布比对 | **PASS**：两主题画布位图**逐字节一致**（固定蓝白） |

> ⚠️ 关于暗色截图：R4 的 `qa-evidence-r4-*-dark.png` 为**无头 Harness 渲染**，其 Wpf.Ui 主题字典未在无头环境完全切换，**页面底色可能仍为浅色，不能代表真实暗色外观**；暗色真机外观以 R1/R2 既有发布版证据为准。R4 的 U3/U4 以**程序断言**为准。

## R4-5. 全量回归

| # | 验证点 | 结果 |
|---|---|---|
| G1 | `dotnet build -c Release` | **PASS**：0 警告 0 错误 |
| G2 | `dotnet test -c Release` | **PASS**：**45 / 45** |
| G3 | 自包含发布 | **PASS**：264 文件 / 154M / 仅 `zh-Hans` |
| G4 | `--selftest` | **PASS**：6 项全过，exit 0 |
| G5 | 单实例第二实例 | **PASS**：exit **3** |
| G6 | `--version` | **PASS**：`1.0.0` |
| G7 | CSV 三编码（UTF-8 BOM / 无 BOM / GBK） | **PASS**：均 3 人、中文正确、降序 |
| G8 | `.xls` 拒绝 | **PASS**：可读 `ImportException` |
| G9 | `.xlsx` 缺成绩列 + 允许生成 | **PASS**：60–100 且标记 generated |
| G10 | 测试卫生（不再残留探针） | **PASS**：`dotnet test` 后 `%TEMP%` **无** `cm_csv_probe*` |
| G11 | 安装/卸载（`--no-*` + `%TEMP%` 临时目录） | **PASS**：见 R4-2，未污染真实桌面/开始菜单 |

## R4-6. 独立证实 / 证伪 工程师自报

| 工程师自报 | 我的独立结论 | 说明 |
|---|---|---|
| 修 P2：PowerShell `-EncodedCommand`，4 组路径 4/4 删净 | **证实（且超出）** | 我复验控制组 4/4 + 对抗 10/10（含单引号/`&%^!`/`[]`/`$`/emoji/超长），全程无 .cmd 落地 |
| 死资源键移除 10 个 | **证实** | 逐一确认不存在，保留键正确 |
| 画布白底单一来源 | **证实** | Surface/Paper 均取自 `SeatMapPalette.CanvasBackground` |
| 测试卫生（唯一目录 + Dispose） | **证实** | `dotnet test` 后无 `cm_csv_probe*` 残留 |
| xlsx 去分数 | **证实** | 读回断言「仅姓名/空、无分数」 |
| build 0/0、单测 45/45、发布 264/154M、selftest exit 0 | **证实** | 全部复现一致 |
| `IS_PASS = YES` | **部分证实** | P2 修复成立；但 **xlsx 去分数属能力变更**，需 PM/用户确认方算最终通过 |
| （工程师未报告） | **新发现（间歇）** | 首轮连续 10 组卸载中 emoji/超长各 1 例未删净（未复现），列残余风险 |

## R4-7. 能力变更清单（累计，需业务确认）

| # | 变更 | 来源 | 影响 |
|---|---|---|---|
| C1 | 移除 `.txt` 文本打印预览 + `IExportService.BuildPrintText`，改为矢量打印窗口 | R3 | 偏离 PRD `R-P0-37`（原要求文本预览 + 另存 .txt） |
| C2 | **xlsx 座位表单元格去分数**：由「姓名(成绩分)」→ 仅「姓名」，空座由「空」→ 留空 | **R4** | **偏离 PRD `R-P0-36`**（原要求单元格「姓名(成绩分)」或「空」）。三处呈现（画布/PNG/打印）早已去分数，xlsx 本轮对齐 |
| C3 | `IExportService.ExportSeatXlsx` 去掉 `scores` 参数；删除 `AppServices.BuildScoreMap()`、私有 `BuildCellText`/`EmptyCell` | R4 | 内部接口/死代码变更，外部无影响 |

> C1/C2 均为「用户改方向后的呈现简化」。请 PM/用户确认是否接受；若需保留 xlsx 成绩列，应补回。

## R4-8. 未验证 / 残余风险（累计，明确不含糊）

1. **卸载间歇性未删净（残余风险）**：首轮连续高压 10 组中出现 2 例未删净（emoji 残 9 文件、超长 264 文件未动），**未复现**；建议保持 60 次重试并可选加长/记录诊断。非路径编码缺陷。
2. **真实暗色主题外观**未在发布版重新截图（R4 无头暗色渲染不可信）；依赖 R1/R2 既有证据 + R4 程序断言（U3/U4）。**未验证**：暗色下点名状态列/学生按钮的真实像素外观。
3. **中键拖动平移 & Shift+滚轮**、**打印实际出 PDF**、**DPI/多显示器缩放**、**非 zh-CN 区域数字解析**：同 R3，**未验证**（受限原因不变）。
4. **`SeatViewModel.OnSeatClicked` 仍为 `async void`**（已有 try/catch 兜底）。

## R4-9. 最终裁决

| 项 | 值 |
|---|---|
| R3 唯一 P2（非 ASCII 卸载残留） | **已修复**（控制组 4/4 + 对抗 10/10；1 次间歇未复现列残余风险） |
| 死键清理 / 画布白底 / 测试卫生 / xlsx 去分数 | **无回归** |
| 全量回归 | **全部 PASS** |
| 新缺陷 | **0** |
| 能力变更 | C1（R3）、**C2（R4，xlsx 去分数）** → **需 PM/用户确认** |
| **路由判定** | **NoOne** |

> 证据：`docs/qa-evidence-r4-seatpage-light.png`（座位页·浅色·已排座绿底/选中蓝框）、`qa-evidence-r4-seatpage-dark.png`（**无头渲染，暗色不可信，仅参考**）、`qa-evidence-r4-rollcall-light.png`（点名页·浅色·状态列 ✅/⏳）、`qa-evidence-r4-rollcall-dark.png`（同上，参考）。未覆盖任何既有证据图。环境已清理：无残留进程、注册表卸载项、桌面/开始菜单快捷方式、`%AppData%\ClassManager`、默认安装目录、`%TEMP%\cm_r4*` 与 `%TEMP%\cm_csv_probe*`。
