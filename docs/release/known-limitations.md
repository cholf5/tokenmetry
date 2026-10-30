# 已知限制（v0.3.0 首版）

> 首版发布时点已知、**明知就不修**或**排在发版之后**的问题。每一条都写清：用户看到什么、什么条件下会撞上、
> 现在怎么绕、后续怎么收。定位见 [`../kanban/roadmap.md`](../kanban/roadmap.md)，架构与口径约定见
> [`../../AGENTS.md`](../../AGENTS.md)。

原则：宁可写成文字，也不假装没有。首版的目标是"数字对得上、口径说清楚"，不是"没有坑"。

## 一、数字可信度

### 1. 读失败会被静默吞掉，界面仍报正常

**症状**：某个 Agent 的日志读不动（权限、文件被占用、磁盘错误、格式意外），该源这一轮直接少报甚至归零，
界面上仍是正常状态、没有告警。你不知道自己看到的是"今天真没花这么多"还是"没读到"。

**原因**：适配器与解析器内部对 `IOException` / `UnauthorizedAccessException` 有二十余处兜底
（`Tokenmetry.Infrastructure` 内 27 处），失败即"这条跳过、继续下一条"，不改变扫描状态；
只有解析器整体抛出的异常才会被 `LocalIngestionDataSource` 的逐源隔离登记为 `Error`
（`JsonlScanStatus.Error` 只由那一处产出）。

**现在怎么绕**：怀疑某个源少报时，去「数据与计价」页看该源的文件数与记录数；数字长期冻结再看一眼文件是否可读。

**后续**：把"部分读取失败"升级成 `Partial` + 计数（哪些文件、失败原因），在数据页可见。发版后第一优先。

### 2. Kimi / DSH 的时间戳未做范围钳制，一条坏数据能让整个源停摆

**症状**：这两个源某天开始一直显示红/异常，且不会自愈——即使后续日志都是好的。

**原因**：`KimiLogParser.TimestampFromSeconds` 与 `DshSessionLogParser` 直接调
`DateTimeOffset.UnixEpoch.AddMilliseconds(...)` / `FromUnixTimeMilliseconds(...)`；日志里出现一个
超出 `DateTime` 表示范围的毫秒值（损坏行、错位字段）就抛 `ArgumentOutOfRangeException`，
整轮解析失败 → 该源进 `Error`，下一轮同样的坏行再抛一次。
Kilo / Hermes / Goose / OpenCode 都有 `TimestampOutOfRange` 拒绝枚举兜底，这两个源没有。

**现在怎么绕**：找到并移走那条坏日志文件（或在设置里临时关掉该源），重扫后恢复。

**后续**：照四个 SQLite 源的做法补拒绝枚举，坏行进"被拒记录数"而不是让整源失败。

### 3. Antigravity 在 WAL 未 checkpoint 时数字会冻结

**症状**：Antigravity 正在使用、新会话也产生了，但 Tokenmetry 里的数字不动，重启应用或过一阵才跳变。

**原因**：Antigravity 是 proto 日志库直读，增量判据是文件戳（mtime + 大小），而它取戳的清单只含主库
（`AntigravityAdapter` 走 `FileStampMemo.Capture(databases)`）。SQLite 的写入先落 `-wal` 边车，主库
mtime 在 checkpoint 前不变——只看主库就会判定"没变化"。另外六个 SQLite 源用的是
`FileStampMemo.SqliteFingerprintFiles`（主库 + `-wal`），没有这个问题。

**现在怎么绕**：点一次「立即刷新」或重开应用（首轮不做指纹复用）。

**后续**：Antigravity 改用 `SqliteFingerprintFiles`。

### 4. `grain='full'` 快照行只 upsert、从不删除 —— 时区/夏令时变更后可能双算

**症状**：改过系统时区（或经历夏令时切换）后，某些日子的总量偏高，像是同一批用量被算了两遍。

**原因**：`usage_facts` 有两类行：分解粒度（`grain <> 'full'`，可按日清理，`SqliteUsageStore.cs:274`）
与全量快照（`grain = 'full'`，只 `UPSERT`）。快照行按 UTC 自然日分桶，日志里的时刻重新落到另一个
UTC 日期时，旧日期那一行没有任何清理路径把它带走。

**现在怎么绕**：清空数据目录下的 `tokenmetry.db` 让它全量重建（会丢失历史会话级明细，聚合数字重建后一致）。

**后续**：给快照行加"本轮日期集合"对账删除。**建议作为发版后的第一张卡**——这是清单里唯一会让
信任锚失真的问题。

## 二、启动与常驻

### 5. 没有全局异常处理器，崩了不留证据

**症状**：应用突然消失，没有对话框、没有日志，你无法告诉作者发生了什么。

**原因**：`App` 没有订阅 `AppDomain.UnhandledException` / `Dispatcher.UnhandledException`，也没有
崩溃转储文件。UI 线程上的意外异常直接掀翻进程。

**现在怎么绕**：终端里 `dotnet run --project src/Tokenmetry.App` 启动，异常会落在终端。

**后续**：捕获 → 写本地 `crash-*.log`（不含 prompt/代码，符合隐私红线）→ 下次启动提示"可附上报告"。

### 6. 冷启动期的"双击图标没反应"

**症状**：第一次启动还没起来的那几秒内再双击一次图标，第二次双击像被吞了，窗口不会更快出现。

**原因**：第二实例的激活监听在主窗与托盘就绪之后才启动（`App.axaml.cs:131` 的 `StartListener`）。
在此之前第二实例只能靠重试窗口盲发（`SingleInstanceGuard.TrySignalActivation`：3 次尝试、每次连接与
回执各 1.5 秒），超时即静默退出——按设计它不该抢占首实例，所以退出是正确的，只是没有反馈。

**现在怎么绕**：等首实例启动完成（托盘图标出现）再操作。

**后续**：第二实例失败时给一次短提示，或把监听提前到窗口创建之前。

### 7. 托盘弹层：点走焦点后 400ms 内点托盘会被吞一次

**症状**：弹层正开着时先点了别处（弹层失焦关闭），紧接着点托盘图标，第一次没反应。

**原因**：`StatusPopoverController` 的焦点丢失守卫（`FocusLossToggleGuard`，400ms）。这是有意的取舍：
macOS 上"失焦自动关闭"与"点击即切换"会撞成开-关-开的抖动，守卫用 400ms 换稳定手感。

**现在怎么绕**：隔半秒再点。

## 三、界面与文案

### 8. 会话表的 `Token` 列头是硬编码英文

**症状**：切到中文/日文等其他语种，会话表这一列表头仍是 `Token`。

**原因**：`Views/SessionsView.axaml:66` 直接写了 `Text="Token"`，没走 `Translations`。这是 i18n 纪律
（文案唯一出处 `Localization/Translations.cs`）的一处漏网。

**后续**：进 `Translations` 全语种补 key——注意 `TranslationsTests` 会强制 10 个语种全覆盖。

## 四、分发与安装

### 9. 只有 zip，没有安装器

**症状**：没有"下一步下一步"的安装向导、没有开始菜单项、没有卸载入口；Linux 没有 deb/rpm/AppImage。

**原因**：v1.0 里程碑内容（Windows 安装器、macOS 公证），首版刻意不做。macOS 产物是最小 `.app`
bundle + ad-hoc 签名（无付费开发者账号，不公证），所以首次打开会看到系统提示：

- macOS：`"Tokenmetry" 无法验证开发者` → 右键「打开」一次，或系统设置里放行，或 `xattr -cr Tokenmetry.app`；
- Windows：SmartScreen「未知发布者」→「更多信息」→「仍要运行」；
- Linux：解压即用（`chmod +x` 后直接运行）。

### 10. 更新不是自更新

**症状**：检查到新版本后，应用只帮你下载并在文件管理器里选中，剩下的解压替换要自己做。

**原因**：应用无签名，运行中替换自身单文件 exe / `.app` 是跨平台雷区（进程锁、封签、Gatekeeper）。
详见 [`ci-release.md`](ci-release.md) 的「应用内更新机制」。

## 五、内部小瑕疵（不影响日常使用）

- 迁移 2 的 `DROP TABLE usage_facts` 缺 `IF EXISTS`（迁移 1 有）。只在 `usage_facts` 不存在的库上才会炸，
  而迁移 1 一定会先建它——所以是洁癖级问题，不是隐患。
- 首次发版没有上一个 tag，`gh release create --generate-notes` 会把整个提交历史抖出来。首版用
  [`notes/v0.3.0.md`](notes/v0.3.0.md) 手写说明（CI 已支持 `docs/release/notes/<tag>.md`）。
