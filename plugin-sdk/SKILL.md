# SKILL：把私有 Agent 接入 Tokenmetry

本文件是一份厂商中立的操作手册，供工程师或其 AI 助手执行。目标是把一个**不在官方支持列表**
里的私有 Agent，做成 Tokenmetry 可安装的 C# 数据源插件。

## 1. 目标

产出一个 `.tkmplugin` 包，安装后满足：

- 该 Agent 出现在 Tokenmetry 的 Agent 列表与所有现有统计页；
- 每条用量记录通过契约校验，能进入 facts 与 session facts；
- `validate` 与 `scan` 都以退出码 0 通过。

不要修改 Tokenmetry 主程序，不要假设自己能访问网络。

## 2. 输入

开始前需要拿到：

1. 目标 Agent 的文档或源码，明确用量的**数据位置**（会话目录、SQLite、日志文件等）；
2. 每个字段的含义：时间、会话、项目、模型、输入/输出 token、缓存 token、成本；
3. 一份**真实或合成的样例数据**，足以覆盖正常记录与至少一种坏记录；
4. 本 SDK：`plugin-sdk/`（契约包、模板、示例、校验器）。

先读 `plugin-sdk/README.md` 的“平台要求”和“打包”两节。

## 3. 字段检查表

逐条对照目标 Agent 的实际数据。**缺任一项即停止**（见第 4 节）。

- [ ] 每条记录有时间戳
- [ ] 每条记录有会话 id（插件内唯一即可；宿主会加命名空间）
- [ ] 每条记录有项目身份（cwd 或等价物）
- [ ] 每条记录有模型名
- [ ] 每条记录有输入 token 字段
- [ ] 每条记录有输出 token 字段
- [ ] 每条记录总 token > 0
- [ ] 若日志有缓存写入细分，5m + 1h 等于缓存写入总数
- [ ] 每条记录能派生稳定的 RecordId
- [ ] 一次扫描能返回当前全量记录集
- [ ] 不把“未知成本”写成 0

补充判定：

- “输入/输出 token 字段”必须分开存在，允许值为 0；只有总数、没有拆分时无法计价，视为缺失。
- 缓存-only 记录（Input = Output = 0，CacheRead > 0）合法。
- 时间未知时区按 UTC；完全无法得到事件时间视为缺失。
- 成本字段：日志没有成本就不写（`null`）；明确免费才写 `0`；有正数成本才写正数。

## 4. 停止条件

如果第 3 节有任一必填项无法从目标 Agent 的数据中得到，**立即停止实现**，不要用 0、空串、
伪造时间或“猜一个模型名”来凑字段。向负责人明确说明：

> 您的 Agent 由于数据缺失而无法接入 Tokenmetry

并列出具体缺失的字段，以及你检查过的数据位置。只有缺失项被补齐后，才继续第 5 节。

## 5. 包格式

`.tkmplugin` 本质是 zip，根目录必须包含：

```text
MyAgent.tkmplugin
├── plugin.json
├── icon.png                     # 128×128 PNG，透明背景，明暗主题可读
├── MyAgent.dll                  # 入口程序集（实现 IUsageSourcePlugin）
├── MyAgent.deps.json
└── ...                          # 其余托管/原生依赖
```

`plugin.json`：

```json
{
  "schemaVersion": 1,
  "id": "com.example.my-agent",
  "agentId": "com.example.my-agent",
  "displayName": "My Agent",
  "version": "1.0.0",
  "apiVersion": "1",
  "entryAssembly": "MyAgent.dll",
  "entryType": "MyAgent.UsagePlugin",
  "icon": "icon.png",
  "author": "Example Corp",
  "description": "Internal My Agent usage source"
}
```

约束：

- `id` / `agentId` 只能匹配 `^[A-Za-z0-9][A-Za-z0-9._-]*$`；它们会进入会话主键命名空间与磁盘路径。
- `displayName` 写入 facts，等同 Agent 身份；改名会切断历史，安装时会警告。
- `version` 是 SemVer 字符串；同 `id` 重装视为升级。
- `entryType` 必须是入口程序集里实现 `IUsageSourcePlugin` 的类型全名。
- **禁止**把 `Tokenmetry.Plugin.Abstractions.dll` 打进包；契约由宿主提供，包里带了会被拒绝。
- 禁止路径穿越、符号链接、绝对路径；包大小 ≤ 100 MB，文件数 ≤ 2000。
- 安装时只解压，不执行任何脚本。

## 6. 模板步骤

```bash
cp -r plugin-sdk/templates/PluginTemplate plugin-sdk/work/MyAgent
cd plugin-sdk/work/MyAgent
dotnet build
```

按顺序改：

1. `PluginTemplate.csproj`：程序集名改为 `MyAgent`。
2. `plugin.json`：`id` / `agentId` / `displayName` / `entryAssembly` / `entryType` / `icon`。
3. `UsagePlugin.cs`：实现 `ScanAsync`（发现 → 解析 → 映射）。
4. `icon.png`：替换为你的 128×128 图标。

`plugin-sdk/samples/FakePrivateAgent` 是一个可运行的端到端示例，包含同样的三段结构与
`fixtures/usage.jsonl`，可以对照着改。

## 7. 实现提示

- **SessionId 只需插件内唯一**。宿主统一加 `plugin:<pluginId>:` 命名空间，避免与内置 Agent、
  其他插件、同步设备撞主键；不要自己加前缀。
- **未知成本用 `null`，不要写 0**。`null` 交给 Tokenmetry 的定价引擎；`0` 表示明确免费。
- **每次 scan 返回全量快照**。`HasChanges = true` 时 `Records` 必须是本次扫描知道的全部记录，
  不是增量；`HasChanges = false` 时宿主复用上一轮成功快照。
- **RecordId 稳定且在本次快照内唯一**。优先用日志自带事件 id；没有就用能稳定派生的组合
  （如会话 id + 行号 + 时间），不要用随机数或每次变化的序号。
- **字段缺失整条拒绝**：时间、会话、项目、模型、输入/输出 token 字段任一缺失，或总 token ≤ 0，
  或缓存写入细分不自洽（5m + 1h ≠ 总数），该条会被宿主丢弃并计入 `invalid`。
- **时间**：带偏移的 ISO 8601 最稳；未知时区按 UTC。
- **容错**：坏行计入 `InvalidRecords` 后继续扫，不要让整轮扫描失败；只有数据源完全不可读时才抛异常。
- **日志卫生**：不要打印任何记录内容、prompt 或代码；宿主只把短消息回传，堆栈写 stderr。
- **隐私红线**：Tokenmetry 只存聚合事实；但插件有能力读取原始会话内容，不要在插件里落盘或上报。

## 8. 构建 / 校验 / 打包

```bash
# 构建
dotnet build -c Release

# 结构校验（不解包执行插件代码）
dotnet run --project tools/Tokenmetry.PluginValidator -- validate path/to/MyAgent.tkmplugin

# 真实宿主扫描（解包到临时目录，用 --data 指定的数据目录跑一轮）
dotnet run --project tools/Tokenmetry.PluginValidator -- scan path/to/MyAgent.tkmplugin --data path/to/sample-data
```

退出码：`0` 通过 / `2` 数据缺失（manifest、入口、图标）/ `3` 输出不合规 / `4` 运行失败。

`scan` 输出会打印 `records`、`invalid`、`duplicates`、`missing-cost`、`unknown-model` 与前 3 条
样例。要求：`invalid: 0`、`duplicates: 0`；`missing-cost` / `unknown-model` 只作提示，不阻断。

打包（在发布输出目录内，macOS / Linux）：

```bash
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

如果插件引用了第三方 NuGet 包，它们的 dll 也必须在包内（模板已开启
`CopyLocalLockFileAssemblies`，会复制到输出目录）；契约程序集始终排除。

打包后再跑一次 `validate` 和 `scan`，两者都必须退出 0。

## 9. 平台要求

- 构建需要 **.NET 10 SDK**；插件目标框架 `net10.0`。
- 宿主是普通 CoreCLR 进程，支持 Windows / macOS / Linux；主应用可以是 AOT 发布，不影响插件。
- **纯托管程序集最稳**。若必须带原生依赖：按 RID 分别打包，并在每个目标平台用 `scan` 验证。
- 校验器只读本地包与数据，不访问网络；作者环境不需要联网还原契约包（SDK 自带
  `contract/` 与 `NuGet.config`）。

## 10. 交付报告

完成后写一份简短报告，至少包含：

- Agent 名称与 `plugin.json` 的 `id` / `displayName`；
- 数据位置（目录 / 文件 / 表名）与格式；
- `scan` 输出的记录数、`invalid`、`duplicates`、`missing-cost`、`unknown-model`；
- 已知缺口（例如没有缓存细分、成本未知、只覆盖部分会话）；
- `validate` 与 `scan` 的退出码；
- 包文件路径与大小。

如果第 4 节的停止条件被触发，报告里必须包含原文：

> 您的 Agent 由于数据缺失而无法接入 Tokenmetry

以及缺失字段清单。
