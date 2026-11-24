# Tokenmetry 插件 SDK

给私有 / 内部 Agent 的作者用的本地 SDK。你的 Agent 不在官方支持列表里时，可以用它写一个
数据源插件（C# 程序集 + `plugin.json` 打成的 `.tkmplugin` 包），安装后其用量会像内置 Agent
一样出现在 Tokenmetry 的总览、Agent、模型、项目、会话、时间与成本分析页。

- 契约：`Tokenmetry.Plugin.Abstractions`（v1，程序集版本固定 `1.0.0.0`）
- API 版本：`apiVersion = "1"`
- 目标框架：`.NET 10`（`net10.0`）

## 获取方式

SDK 随 Tokenmetry 仓库一起分发，位于 `plugin-sdk/`，不需要单独下载；契约 NuGet 包已入库，
配合本目录的 `NuGet.config` 可完全离线 restore。校验器在仓库的
`tools/Tokenmetry.PluginValidator/`。

## 维护契约包（仓库维护者）

`contract/` 里的 nupkg 不是手搓的：契约源码在 `src/Tokenmetry.Plugin.Abstractions`，改完后运行
`scripts/pack-plugin-contract.sh` 重新生成，并与源码放进同一个提交。

CI 会跑 `verify-contract`，比对包内 DLL 与当前源码的公开 API；忘了重新打包时这条会红。它只比
公开 API，不比字节——不同 SDK / 不同提交编出的 PE 字节本来就会不同。

## 目录

| 路径 | 内容 |
|---|---|
| `SKILL.md` | 给插件作者（或作者的 AI）的完整工作流、字段检查表与停止条件，**先读它** |
| `contract/Tokenmetry.Plugin.Abstractions.1.0.0.nupkg` | 本地契约 NuGet 包，离线 restore 用 |
| `NuGet.config` | 把包源指向 `contract/`，构建时不需要网络 |
| `templates/PluginTemplate/` | 最小可编译插件骨架（入口类、`plugin.json`、图标） |
| `samples/FakePrivateAgent/` | 假想的私有 Agent：入口类 + `fixtures/usage.jsonl`，端到端示例 |
| `../tools/Tokenmetry.PluginValidator/` | 本地校验器（`validate` / `scan` / `verify-contract`） |

## 平台要求

- 构建插件需要 **.NET 10 SDK**；运行时插件由 Tokenmetry 的宿主进程（普通 CoreCLR，非 AOT）
  加载，主应用本身仍是 AOT / single-file 发布。
- 支持 Windows / macOS / Linux。**纯托管程序集最稳**；若插件带原生依赖，必须按 RID 打包并分别
  验证每个目标平台。校验器会提示原生依赖风险。
- 插件包在目标平台必须可加载：不要依赖只存在于开发机的绝对路径或系统组件。

## 快速开始

`NuGet.config` 的包源是相对 `plugin-sdk/` 的 `contract/`，所以最简单的做法是把新插件项目放在
`plugin-sdk/` 目录树内（NuGet 会向上查找 `NuGet.config`）。该配置用 `<clear />` 清空了默认源以
保证离线；如果插件需要第三方 NuGet 包，请在同一 `NuGet.config` 里加回 nuget.org 源。

```bash
cp -r plugin-sdk/templates/PluginTemplate plugin-sdk/work/MyAgent
cd plugin-sdk/work/MyAgent
dotnet build
```

如果必须把项目放到别处，请把 `plugin-sdk/contract/` 一起带上，并在项目旁放一份 `NuGet.config`，
把 `packageSources` 指向该契约包目录。

改这几处即可开始：

1. `PluginTemplate.csproj`：程序集名。
2. `plugin.json`：`id` / `agentId` / `displayName` / `entryAssembly` / `entryType`（入口类型全名）。
3. `UsagePlugin.cs`：把「发现 + 解析 + 映射」换成你的 Agent。
4. `icon.png`：128×128 PNG，透明背景，明暗主题都可读。

字段规则与停止条件见 `SKILL.md`。

## 打包

`.tkmplugin` 本质是 zip，**根目录**包含 `plugin.json`、图标、入口程序集、`.deps.json` 及其依赖：

```bash
dotnet build -c Release
# 以 macOS / Linux 为例（在发布输出目录内；如有其他依赖 dll 一并列出）：
cd bin/Release/net10.0
zip -r ../../MyAgent.tkmplugin plugin.json icon.png MyAgent.dll MyAgent.deps.json
```

Windows PowerShell：

```powershell
# 输出目录里的 Tokenmetry.Plugin.Abstractions.dll 必须排除（契约由宿主提供）
Get-ChildItem bin\Release\net10.0 |
  Where-Object Name -ne 'Tokenmetry.Plugin.Abstractions.dll' |
  Compress-Archive -DestinationPath MyAgent.zip
Rename-Item MyAgent.zip MyAgent.tkmplugin
```

规则：

- **不要**把 `Tokenmetry.Plugin.Abstractions.dll` 打进包；契约由宿主提供，包里带了会被拒绝。
- 不允许路径穿越、符号链接、绝对路径；包大小 ≤ 100 MB、文件数 ≤ 2000。
- `plugin.json` 必须在包根目录。

## 校验

```bash
# 只检查包结构 / manifest / 入口 / 图标 / apiVersion / 契约程序集
dotnet run --project tools/Tokenmetry.PluginValidator -- validate path/to/MyAgent.tkmplugin

# 解包到临时目录，用指定数据目录跑一轮真实宿主扫描，打印记录数、诊断、前 3 条样例
dotnet run --project tools/Tokenmetry.PluginValidator -- scan path/to/MyAgent.tkmplugin --data path/to/sample-data
```

退出码：

| 码 | 含义 |
|---|---|
| 0 | 通过 |
| 2 | 数据缺失（manifest / 入口程序集 / 图标缺失） |
| 3 | 输出不合规（包结构、manifest 或记录校验失败） |
| 4 | 运行失败（包无法读取、宿主无法启动 / 崩溃、插件抛异常） |

校验器只读本地包与数据，不访问网络。

示例（用仓库自带的假 Agent）：

```bash
# 先按上面的“打包”步骤生成 FakePrivateAgent.tkmplugin
dotnet run --project tools/Tokenmetry.PluginValidator -- validate FakePrivateAgent.tkmplugin
dotnet run --project tools/Tokenmetry.PluginValidator -- scan FakePrivateAgent.tkmplugin \
  --data plugin-sdk/samples/FakePrivateAgent/fixtures
```

## 版本对应

| 项 | v1 |
|---|---|
| 插件 API 版本（`plugin.json` 的 `apiVersion`） | `"1"` |
| 契约程序集版本 | `1.0.0.0` |
| 契约 NuGet 包版本 | `1.0.0` |
| 宿主协议 | 内部协议，不承诺跨版本稳定 |

官方应用升级不会触碰已安装插件；`apiVersion = "1"` 的插件在 v1 线内继续加载。破坏性变更会
开新的程序集身份与 `apiVersion`，宿主并行支持旧版，不要求作者重新生成插件。
