# 班级综合管理系统（ClassManager）

C# + WPF-UI 重写版。单机使用，数据只在本机。

## 功能

| 模块 | 说明 |
|---|---|
| 点名系统 | 随机点名、历史记录 |
| 座位管理 | 拖拽排座、画布导出 |
| 讲评课件 | 把「试题卷 + 答案卷」docx 转成讲评 PPT |
| AI 办公 | 本地 AI 助手：读写文件、生成 Excel/Word/PPT、联网检索、自动装工具 |
| 自动更新 | 检查 GitHub Release，有新版就下载并覆盖安装 |

## 构建

```powershell
dotnet publish ClassManager.App/ClassManager.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false `
  -p:DebugType=none -p:SatelliteResourceLanguages=zh-Hans -o publish
```

产物是 `publish/ClassManager.App.exe`（单文件，目标机不用装 .NET）。

## 自动更新（怎么用）

仓库推一个 `v` 开头的 tag，GitHub Actions 会自动：
构建 exe → 生成 `version.json` → 发 Release（附 exe 和清单）→ 把清单提交回 `docs/`。

应用里「关于 → 更新源设置」填**这个**：

```
https://github.com/KEY-MAKE/ClassManger/releases/latest/download/version.json
```

- 地址永不变，永远指向最新 Release（实测连查 4 次都是最新，无缓存问题）
- 国内下载慢的话程序会自动回退到加速镜像（实测 14KB/s → 369–516KB/s）

> 也有人用 jsDelivr 读仓库里的 `docs/version.json`：
> `https://cdn.jsdelivr.net/gh/KEY-MAKE/ClassManger@main/docs/version.json`
> 但**实测 jsDelivr 的缓存不认 query 参数**，发新版后可能要等很久才更新，
> 所以推荐上面那个 `releases/latest/download/` 地址。

国内下载慢的话，程序会自动回退到加速镜像（实测 14KB/s → 369–516KB/s），
也可以在清单的 `mirrors` 里加自己的地址。

> 仓库必须是**公开**的 —— jsDelivr 读不了私有仓库。

## 本地开发

```powershell
dotnet build ClassManager.sln -c Release
dotnet test ClassManager.sln -c Release
```

## 目录

```
ClassManager.App/      主程序（WPF-UI 4.3.0）
ClassManager.Tests/    xUnit 测试
docs/                 设计文档与更新清单
```
