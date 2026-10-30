# 性能调查与实测基线（2026-10-28）

日期：2026-10-28
范围：perf 三提交（`74110a3` 采集探针 / `d37c6a5` 复合快照 / `b294741` 串驻留）、单实例测试修复 `913b8cc`、评审批次一 `5f4b0fd`、静息 tick 评估
相关提交：`74110a3` `d37c6a5` `b294741` `913b8cc` `5f4b0fd` `b47237c` `464777f`
相关记录：看板 2026-10-28 两条注记（性能实测批次 / 续·静息 tick 评估）

## 1. 结论摘要

| 问题 | 判定 | 依据 |
|---|---|---|
| 采集侧的零变更探针还值得继续挖吗 | 不值得 | 静止轮已 12–15 ms（1200 文件），真实库一天只长 3 行 facts |
| 总览查询还值得继续挖吗 | 不值得 | 真实规模一次装载 13 ms；快照已把 13 次全表读收成 2 次 |
| 串驻留还值得继续挖吗 | 不值得 | 真实记录数 1–2k，整记录堆 ≈1 MB；托管堆 35 MB 里不是矛盾 |
| 要不要把"零变化 tick 也重载当前页"改成条件重载 | **不改** | 真实用户一次静息 tick ≈15 ms（探针）+ ≈42 ms（页面），5 分钟一次不可感知；改动要重定义四个交互语义 |
| 下一步如果还要做性能，做什么 | 启动到首帧 | 稳态 Private 230–280 MB 里托管堆仅 35 MB，大头是运行时与渲染栈；需真机 instrument，headless 给不出可信数字 |

一句话：**数据面（采集 / 查询 / 内存）三条轴到此收口**；本批最大的单项收益不是"新优化"，而是修掉上一轮优化自己引入的 70 倍解析回归。

## 2. 真实规模取证（决定一切的前提）

本机应用库 `~/Library/Application Support/Tokenmetry/tokenmetry.db`（2026-10-28）：

| 指标 | 值 |
|---|---|
| `usage_facts` | **154 行** |
| `session_facts` | **662 行** |
| 覆盖 | 49 天 / 4 agent / 12 模型 / 45 项目 |
| 单源最大账本 | ZCode `cli/db/db.sqlite` 的 `turn_usage` **1135 行**（目录 3.4 GB、921 MB 的库里真正参与计量的就这些） |
| 增长速率 | facts ≈ 3 行/天 |

关键结构事实：`usage_facts` 是**聚合表**（day × grain × agent × model × project），不是逐记录账本。按 3 行/天外推，十年 ≈ 1.1 万行。上一轮压测使用的 5.7 万 / 17.5 万行需要"每天三万种维度组合"，单人开发机到不了。

因此：**压测里出现的百毫秒级数字只对应多年代码库 + 多项目的极端用户**，不能当成日常基线。

## 3. 测量方法（含踩过的坑，重启测量前先读）

### 3.1 三个构建变体（记录侧前后对比）

基准工程在 `dist/bench`（`.gitignore` 已忽略 `dist/`，不入库），三个变体共享同一份 `Program.cs`，靠 `ProjectReference` 指向不同 revision：

```
dist/bench/records-prepool   → dist/worktrees/prepool   (74110a3，未驻留)
dist/bench/records-poolcount → dist/worktrees/poolcount (b294741，驻留 + ConcurrentDictionary.Count)
dist/bench/records-current   → 主工作区 HEAD            (驻留 + 原子计数器)
```

worktree 用 `git worktree add dist/worktrees/<name> <sha>` 建，清理用 `git worktree remove`。

### 3.2 五个必须知道的坑

1. **记录集必须保活**：测量期间不 `GC.KeepAlive(records)`，JIT 会认为局部变量已死，`GC.GetTotalMemory(forceFullCollection: true)` 只读到 0.1 MB（第一次跑就是这么错的）。
2. **解析循环的记录必须逃逸**：写进 sink 数组；否则 .NET 10 的栈上分配会把 `new` 消掉，吞吐从 1.5M 虚增到 10M 记录/秒。
3. **种子数据的日期要贴着今天**：页面默认筛选是"最近 7 天"，把种子放在 2026-01-01 起会量到**空页面**（2.8 ms 的假快数字，"rows=0/0" 是识别信号）。种子必须从今天倒推。
4. **Release 与 Debug 差别明显**：查询/UI 相关的数字一律用 `dotnet test -c Release` 或 `dotnet run -c Release`。
5. **headless 是软件光栅**：`CaptureRenderedFrame()` 的渲染成本是**上限**，真机 GPU 更低；只用于比较相对差异，不当真机数字用。

### 3.3 复现命令

```bash
# 记录侧：内存 + 解析吞吐（三变体）
cd dist/bench && dotnet run -c Release --project records-current/records-current.csproj

# 查询侧：总览逐方法组合 vs 复合快照（参数：天 模型数 每日会话数）
dotnet run -c Release --project query/query.csproj query 365 60 200

# 采集侧：全量轮 vs 零变更轮
dotnet run -c Release --project query/query.csproj ingest
```

静息 tick 的测量用例不在套件里（会拖慢 headless 并容易在 CI 抖），副本在
`dist/bench/headless/IdleTickCostMeasurement.cs.txt`：拷回 `tests/Tokenmetry.App.Headless.Tests/Shell/`
即可 `dotnet test tests/Tokenmetry.App.Headless.Tests -c Release --filter "FullyQualifiedName~IdleTickCost"`。

## 4. 实测结果

环境：macOS 25.5 arm64 / .NET 10 / Release。

### 4.1 串驻留的内存（300k 记录 / 12k 会话文件）

| 变体 | 托管堆 | 每记录 |
|---|---|---|
| 未驻留 | 208.2 MB | 728 B |
| 驻留 + `ConcurrentDictionary.Count` 判容量 | 154.3 MB | 539 B |
| 驻留 + 原子计数器（HEAD） | 154.7 MB | 541 B |

收益真实但口径要收窄：**整记录堆 -26%（≈54 MB / 300k 记录）**，重复串部分约 -187 B/记录。
原提交"百 MB → MB 量级"的说法不成立。

### 4.2 解析吞吐（200k 记录，逃逸分配，3 轮）

| 变体 | 单线程 | 4 线程 |
|---|---|---|
| 未驻留 | 1.46 / 1.99 / 1.61 M rec/s | 1.36 / 1.13 / 1.45 M |
| 驻留 + `Count` | **37.8k / 27.8k / 23.7k** | **22.8k / 16.4k / 13.9k** |
| 驻留 + 计数器（HEAD） | 1.49 / 2.00 / 2.47 M | 2.16 / 1.50 / 2.05 M |

`ConcurrentDictionary.Count` 的实现是取全部桶锁再逐桶求和，挂在"每条记录 5 次 setter"的解析热路径上：
吞吐掉约 **70 倍**，且 4 线程比单线程更慢（串行化坐实）。`5f4b0fd` 改原子计数后回到与不驻留同级。

### 4.3 总览查询（临时 SQLite，UTC，中位数）

| 指标 | 规模 A（57.6k facts / 18k sessions） | 规模 B（175.2k / 73k） |
|---|---|---|
| 逐方法组合（旧行为） | 910 ms | 3190 ms |
| 复合快照 | **190 ms** | **685 ms** |
| └ facts 全表读 | 136 ms | 339 ms |
| └ sessions 全表读 | 46 ms | 168 ms |
| └ 内存聚合 | ≈8 ms | ≈178 ms |
| 会话页 40（弹层最近活动） | 46 ms | 202 ms |

快照 -79%（13 次全表读 → 2 次），与提交说法一致；但规模 B 的 685 ms 里两次全表读占 74%——
一次装载减少的是**读的次数**，不是"读全表"这件事本身。

### 4.4 采集零变更轮（Gemini，1200 文件 × 40 行 = 48k 记录）

| 轮次 | 耗时 | FilesRead | FilesChanged |
|---|---|---|---|
| 全量 | 302 ms | 1200 | 1 |
| 零变更 #1 / #2 | 14.8 / 11.7 ms | 0 | 0 |

≈9.7 µs/文件，记录集与首轮一致。

### 4.5 静息刷新 tick 的页面重载（headless，Release，总览页）

| 规模（facts / sessions） | VM 重载（查询 + 集合重建） | 重载 + 一帧 |
|---|---|---|
| 无重载时的静息帧 | — | **0.6 ms** |
| 真实（147 / 637，对齐本机库 154 / 662） | 13.2 ms | **41.8 ms** |
| 三年投影（3.3k / 14.2k） | 81.4 ms | 94.1 ms |
| 重用户（21.9k / 65.7k） | 217.4 ms | 230.1 ms |

"无重载静息帧 0.6 ms"这行是关键对照：成本**全部**来自"每轮无差别重载"，不是渲染管线本身。

## 5. 评审批次一修了什么（`5f4b0fd`）

评审批次（`3cdff83..913b8cc`）共提出 4 项应修问题，已全部落地：

| 问题 | 位置 | 修法 |
|---|---|---|
| `ConcurrentDictionary.Count` 挂解析热路径 | `Core/Models/UsageStringPool.cs` | 原子计数判容量；触顶后"新值直通、既有条目照常命中" |
| 单一 100k 预算会被 `SourceFile` 吃光 | 同上 + `Core/Models/UsageRecord.cs` | 身份类四列（32k）与 `SourceFile`（256k）分槽；`SourceFile` 走 `InternSourceFile` |
| pi-agent 记忆键漏了解析期配置 | `Infrastructure/JsonSource/FileStampMemo.cs`（`WithConfig`）+ `Infrastructure/PiAgent/PiAgentAdapter.cs`（`ScopeSignature`） | 作用域名 + 作用域根作为签名折进戳集（FNV-1a 64 合成戳），store 改名/重叠根迁移必重扫 |
| OpenClaw 复用轮把 `Partial` 洗成 `Ok` | `Infrastructure/OpenClaw/OpenClawAdapter.cs` | SQLite 侧落真实诊断（原为全零占位）；`TryReuse` 增加 `previousDiagnostics` 供双侧适配器重算 |

顺带统一了 `FileStampMemo.TryReuse` 载荷的内容诊断口径（Malformed / Invalid / LinesSkipped 归零、
累计四项与 `Status` 沿用上轮），与 `IncrementalJsonlScanner` 的零变更轮一致。

## 6. 判定与"重开条件"

明确**不做**的事，以及各自的重开条件：

| 不做 | 理由 | 什么情况下重开 |
|---|---|---|
| 条件重载（零变化跳过页面 reload） | 真实规模省 42 ms/5 分钟；要重定义手动刷新反馈、相对时间文案、数据源状态灯、筛选并发顺序四个语义 | 页面重载中位数 > 100 ms **且**有用户报告卡顿；届时优先做局部重绘或"窗口不可见跳过" |
| SQL 下推聚合 / 增量缓存 | 真实 facts 154 行，快照 13 ms | facts > 10 万行（≈3 万/天维度组合）或用户报告页面卡顿 |
| I-042 二期：混轮按源增量计价（I-043 深水区，**该卡已 2026-10-30 作废**） | 真实记录数 1–2k，全量重算 < 50 ms | 记录数上万 **且**频繁出现"单源变化触发全量轮"（作废不等于否定触发条件，真到了重开时先做窗口化驻留） |
| 弹层复用快照里的 sessions | 真实规模 1 ms 级，纯卫生问题 | 顺手做，不单独排期 |

下一个真正值得投入的方向是**启动到首帧**：稳态 Private 230–280 MB 中托管堆仅 35 MB，大头是
.NET 运行时 + 渲染栈（裸 Avalonia ≈60 MB、+Semi ≈100 MB 是框架地板）。这条路的准入条件是先有
真机测量（dotnet-trace / 帧时间），否则容易再次优化到无人感知的地方。

## 7. 遗留项

1. 代码注释里引用的 `P0-2` / `P0-3` 在 `docs/` 下无对应记录（看板无卡、`docs/plans` 无计划），后续引用应换成可解析的提交/看板指针。
2. `B-020`（单实例守卫 flake）的根因已由 `913b8cc` 修掉，但卡片仍在 Todo，取证结论尚未回填。
3. `StatusPopoverViewModel` 的最近活动仍是独立的一次 sessions 全表读 + 内存排序（规模 B 202 ms）。
4. 启动/首帧成本未量化（见上节）。
5. 驻留池的触顶路径只有单测覆盖（真实数据集去重串 12.8k，未触顶）。

## 8. 数字速查

```
记录（300k / 12k 文件）      未驻留 728 B/记录  → 驻留 541 B/记录   解析 1.5–2.5 M rec/s
解析（Count 版）             23.7k–37.8k rec/s 单线程，14k–23k 四线程（回归 70×）
查询（57.6k+18k）            逐方法 910 ms → 快照 190 ms
查询（175.2k+73k）           逐方法 3190 ms → 快照 685 ms（两次全表读 507 ms）
采集（1200 文件）            全量 302 ms → 零变更 12–15 ms
静息 tick（真实规模）        探针 15 ms + 页面 42 ms，每 5 分钟一次
真实库                       154 facts / 662 sessions / 49 天 / ≈3 行/天
```
