---
id: I-055
title: ZCode 探针逐库准入"部分读取轮×删库"漏洞——全或无准入 + 覆盖率守卫（I-049 P1 同款，修复先行、后补留档）
type: infra
priority: P3
size: S
status: done
created: 2026-11-14
updated: 2026-11-14（done）
blocked: none
---

## Goal

修复 `ZCodeSqliteAdapter` 逐库准入的"部分读取轮 × 下轮删库"漏洞（I-049 审查在 OpenCode
侧发现的 P1 的移植源头）：部分读取轮里失败库的残行留在记录集、库却被吐出探针记忆，下一轮
删掉该库后 探针集 == 库集 == 记忆集，零变更短路永续复用混有已删库残行的缓存——违反
`ProbesUnchanged` 自己声明的"库集收缩必判变化"不变式。

## Background（已对照源码核实）

- `ZCodeSqliteAdapter.cs` 旧落账：`readMemory` 逐库收集"探针成功 + 读取成功"的库，
  `:226-229` 无条件落账四件套——记录集与记忆的覆盖面不一致，缩水的记忆在删库轮"巧合相等"。
- 三轮序列：轮 k+1 库 B 探针失败/值变化 → 全量重读 → B 读取中途抛异常（`ReadDatabase`
  保留已读行、`readSucceeded=false`）→ B 的半截行进记录集、B 不进记忆；轮 k+2 删 B →
  零变更短路 → 复用含 B 残行的缓存，直至某个无关变化强制全量。
- I-049 已在 OpenCode 侧落地修法（全或无准入 + 覆盖率守卫），ZCode 是移植源头、带同款
  漏洞；I-049 按"不越界顺手修"只登记未修。

## 定案方向（照抄 I-049，不自创第二套算法）

1. **全或无准入**：仅当 `readMemory` 覆盖本轮库集才落账四件套；部分读取轮整体作废记忆
   （探针、干净指纹、记录、诊断全清），下轮必全量重读。健康路径行为不变。
2. **覆盖率守卫**：`ProbesUnchanged` 加 `discoveredCount` 参数，探针集 ≠ 本轮库集即判
   变化——正确性不依赖"探针失败 ⇒ 同库读取必失败"的假设（该假设并非取证）。
3. 不抽两适配器的探针公共件（I-049 同款边界，注释互相指向）；不动读取面/去重/排序/
   status 口径。

## Acceptance Criteria（修复先行，f6f0218 已全部满足）

- [x] 部分读取轮不落账四件套：回归测试
      `ChangeProbe_PartialRound_ThenFailingDatabaseRemoved_DoesNotServeStaleCache`
      三真实轮锁定（健康基线 → B 截断成部分轮 → 删 B），**已实证修复前该测试失败**
      （删库轮 `FilesChanged=0` 判零变更、复用含残行缓存）。
- [x] 覆盖率守卫落地；新增库探针失败（库集增长）场景不再永驻缓存。
- [x] 健康路径逐位不变：既有探针/快路径用例零改动通过（旧删库用例仅按 P2-1 补 Windows 跳过）。
- [x] 全量 `dotnet test Tokenmetry.sln` 2379 passed / 0 failed（Infrastructure 837 = 836 + 新增 1）；
      SmokeScan 真机三轮 ZCode 首轮 `1/1/1`、空闲轮 `1/0/0` 零读取零 SQL，与修复前逐位一致。
- [x] 顺路落掉 I-049 审查登记的两项：旧删库测试补 Windows 跳过（P2-1）、
      `_readOnlyConnections` 注释补生命周期说明（P2-2）。

## fixture 构造经验（本卡留档的主要动机之一）

- **OpenCode 的 WITHOUT ROWID 构造不适用 ZCode**：ZCode 探针用 `MAX(id)` 业务列，
  在 WITHOUT ROWID 表上照常工作，构造不出"探针失败但读取成功"。
- **未知 COLLATE 已被 SQLite 在 DDL/prepare 即拒绝**（3.51 CLI 与 Microsoft.Data.Sqlite
  实测一致）："建表埋未知 collation 让 MAX(id) 聚合炸、行读取无碍"的路线不可行。
- **物理截断的坑**：只截尾部不补 header 页数（offset 28 大端 u32）时，header 声称页数 >
  实际页数，SQLite 在 prepare 阶段（读连接无法回写修正）整体判 CORRUPT、永远零行，
  构造不出"中途"失败；补写页数后 prepare 通过、故障推迟到 stepping——首轮 500 行截 3 页
  实测 373 行存活后抛错。行存活性不靠页数推算，断言用上下界锁定。
- **Windows 无需跳过删库**：部分轮里探针失败与读取失败都走弃连路径，适配器不持坏库
  句柄（与 OpenCode 侧"读取成功持句柄须跳过"不同）；`ClearAllPools` 只为释放 fixture
  侧池化写连接。

## 明确不做

- 不抽探针公共件；不动读取面口径；不做行级增量（I-043 同源理由）。
- 不改 OpenCode 行为（仅更新两处"ZCode 无此守卫"的交叉注释为同款口径）。

## Dependencies / Related

- I-049（OpenCode 侧 P1 修复与探针设计来源）、I-006（ZCode 适配器 + 探针先例）。

## Development Log

### 2026-11-14 修复先行（f6f0218，未开卡）

- 用户拍板"直接修"（不开卡）；I-049 登记句继续挂起。改动三文件：适配器（准入 + 守卫 +
  注释）、OpenCode 交叉注释、测试（新增回归 + P2-1 跳过）。实证修复前测试失败后收口。
- fixture 构造走了两步弯路（COLLATE 被 DDL 拒 → 改截断 + 补 header），教训沉淀在上节。

### 2026-11-14 留档收口（本卡）

- 用户拍板后补留档卡（I-049 登记句改为指向本卡）。I-054 已被"文件型源逐文件记账"占用，
  本卡顺延 I-055。
