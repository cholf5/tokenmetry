---
id: P-004
title: Native AOT 支持——反射 JSON 全量源生成，PublishAot 编译零警告
type: packaging
priority: P3
size: M
status: done
created: 2026-10-27
updated: 2026-10-27
blocked: none
---

## Goal

让 `dotnet publish -p:PublishAot=true` 在仓库 `TreatWarningsAsErrors=true` 纪律下零警告通过：把全部反射式 System.Text.Json 调用点迁移到源生成（`JsonSerializerContext`），行为零变化，为后续发布矩阵接线扫清代码层阻塞。

## Background

- P-003（2026-10-25）收口时登记"有意不做 PublishAot——Avalonia + ScottPlot 尚不成熟"。该前提经 2026-10-27 实测失效：Avalonia 12.1.4 / Semi.Avalonia 12.1.0.1 / Irihi.Avalonia.Shared / ScottPlot.Avalonia 5.1.59 / CommunityToolkit.Mvvm / MS.DI / Microsoft.Data.Sqlite / ZstdSharp.Port 在 PublishAot 下全部零 IL 警告（前置条件 `AvaloniaUseCompiledBindingsByDefault=true` 与托盘 `[UnmanagedCallersOnly]` interop 早已就位）。
- 唯一阻塞全集：6 处反射式 JSON 调用点（IL2026/IL3050，8 条警告），分布在 5 个文件。本卡 = 清零这 6 处。
- 收益量化（osx-arm64 实测）：AOT 28 MB 单 exe + 4 原生 dylib（Skia/HarfBuzz/AvaloniaNative/e_sqlite3）≈ 48 MB、6 文件；self-contained 为 120 MB、238 文件。托盘常驻应用另得冷启动无 JIT 红利。

## Acceptance Criteria

- [x] 6 处反射 JSON 全部源生成：CCUsageCliParser / ClaudeCodeJsonlLineParser / CodexJsonlFileParser / QwenUsageRecordDeduper / AppSettingsStore（读写两处）
- [x] `dotnet build Tokenmetry.sln` 全绿（唯一警告为存量 AVLN5001 Watermark，与本卡无关）
- [x] 测试：Core 189 / Reconciliation 49 / Infrastructure 678 / Headless 397 全过；App.Tests 264 过 2 失败，经 stash 干净工作区复核为已知 B-020 环境性（SingleInstanceGuardTests 间歇失败，与迁移无关）
- [x] `dotnet publish -c Release -r osx-arm64 -p:PublishAot=true` 在 TreatWarningsAsErrors 下 exit 0、零 IL2026/IL3050
- [x] AOT 产物冒烟取证：真实 settings.json 源生成反序列化成功 + tokenmetry.db 打开（lsof 4 fd）+ 主线程 CFRunLoop 事件循环健康（sample 栈无卡死）

## Dependencies

- none

## Development Log

### 2026-10-27 收口

- 五个文件的改法（行为零变化是硬约束）：
  - CCUsageCliParser：新增顶层 `CCUsageCliJsonContext`（`[JsonSerializable(typeof(CliDailyResponse))]`），自定义实例保留 `PropertyNameCaseInsensitive=true` 原口径，`Deserialize(json, typeInfo)` 重载替代泛型重载。
  - ClaudeCodeJsonlLineParser：嵌套 `ClaudeCodeJsonContext`（RawUsageEntry + AgentProgressEntry 两个根）；泛型 `TryDeserialize<T>` 改收 `JsonTypeInfo<T>` 参数，Default 口径（大小写敏感精确名绑定）不变。
  - CodexJsonlFileParser：嵌套 `CodexJsonContext`（SessionLineDto 根），Default 口径（serde from_str 同判 malformed 语义依赖它）。
  - QwenUsageRecordDeduper.BuildKey：`object[]` 多态无法源生成 → `Utf8JsonWriter` 逐元素手写；全基元元素 + 同一默认转义器，输出与反射序列化字节一致（蓝本 serde_json entry_id 对齐语义不动）。
  - AppSettingsStore：新增 App 层 `AppSettingsJsonContext`（AppSettings 闭图），自定义实例保留 `WriteIndented=true`；读写走 `DeserializeAsync/SerializeAsync` 的 JsonTypeInfo 重载。
- 生成器三课（后续新增源生成上下文会再踩）：
  1. 同一程序集内上下文类不能同名——hintName 是 `上下文名.类型名.g.cs`，两个嵌套 `JsonContext` 直接撞 `JsonContext.String.g.cs`（CS8785 生成器整体罢工）。
  2. 嵌套上下文要求外层类也 `partial`（SYSLIB1032）。
  3. `JsonTypeInfo<T>` 在 `System.Text.Json.Serialization.Metadata` 命名空间，不在 `System.Text.Json`。
- 有意不做（发布侧单点决策，不混入本卡）：PublishAot 接进 ci.yml / package.sh 发布矩阵；`JsonSerializerIsReflectionEnabledByDefault=false` 全局翻转（会把 CoreCLR 构建的反射 JSON 也禁掉， ScottPlot 等三方库运行时行为需先核）。
- 冒烟取证备注：macOS 的 `LocalApplicationData` = `~/Library/Application Support`（非 Linux XDG 的 `~/.local/share`）；db mtime 不因干净打开而变（迁移幂等零写入），判断"AOT 进程到达存储层"用 `lsof -p <pid>` 看 db fd，不看 mtime。
