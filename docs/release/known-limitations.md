# 已知限制（v0.3.2 起）

> 当前发布时点已知、**明知就不修**或**排在后面**的问题。每一条都写清：用户看到什么、什么条件下会撞上、
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

### 4b. 便携版仍会向临时目录解包原生库

**症状**：Windows 便携 zip 与 Linux 单文件包首次启动时会往临时目录写约 20MB（Windows：
`%TEMP%\.net\Tokenmetry.App\<哈希>\`），之后启动复用；每次更新版本会再写一份新的。

**原因**：单文件包里的原生库（Skia / HarfBuzz / SQLite）无法从内存加载，只能先落盘——
这是“一个文件就能跑”的对价。**安装器与 macOS .app 从 v0.3.2 起改成 AOT 散开形态，不碰 temp**。

**现在怎么绕**：想要无 temp 解包就用 Windows 安装器，或用 macOS 版。

**后续**：Linux 是否转 AOT 待真机验证（见第 12 条）。

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

### 9. 包都是未签名的

**症状**：Windows 上装安装器时 SmartScreen 提示「未知发布者」；macOS 首次打开 `.app` 提示
`"Tokenmetry" 无法验证开发者`；部分企业环境/杀软会直接拦掉下载或启动。

**原因**：没有代码签名证书（Windows EV 证书 / Apple 开发者账号都是付费且需实名）。macOS 产物是最小
`.app` bundle + ad-hoc 签名，只保证不报「已损坏」，不消除上面那个提示。

**现在怎么绕**：

- Windows：安装器 →「更多信息」→「仍要运行」；
- macOS：右键「打开」一次，或系统设置里放行，或 `xattr -cr Tokenmetry.app`；
- Linux：解压即用。

**后续**：买证书 / Apple 开发者账号后接进 `package.sh`（macOS 还需公证 + staple）。付费前提，不是代码问题。

### 10. 安装器只有 Windows 有，向导也只有英文

**症状**：Linux 没有 `.deb` / `.rpm` / AppImage；Windows 安装向导的按钮与提示是英文，而应用界面本身
可以是中文。（macOS 不发 `.dmg` 是**已决策不做**，不是缺口——见第 13 条。）

**原因**：Linux 各包格式要各自的仓库与依赖体系，没有用户需求时不值得做。向导语言：Inno Setup 官方
只内置英文，中文要非官方 `chinesesimplified.isl`——而应用内静默升级根本不显示向导，价值不在首位。

**后续**：Windows 向导中文可以随时补（一个 `.isl` 文件）；Linux 包格式待有用户需要再谈。

### 11. 更新在 macOS / Linux 仍是「下载 + 自己解压」

**症状**：macOS / Linux 点「下载更新」只把 zip 下载并在文件管理器里定位，替换要自己做。

**原因**：只有 Windows 有安装器可以接管替换。macOS 的 `.app` 可以在运行中被替换（目录可卸），但没有
安装器就没有可靠的回滚与“关旧实例→换文件→重启”流程；Linux 则连统一分发格式都没有。

**后续**：macOS 可试「下载 → 解压到临时目录 → 脚本替换 + 重启」；Linux 等 AppImage。

### 12. Linux 仍是单文件 R2R，AOT 未真机验证

**症状**：Linux 包比 AOT 形态大得多（约 66MB zip），首次启动还会向临时目录解包原生库。

**原因**：macOS 与 Windows 的 AOT 产物都已人工验过（窗口 / 托盘 / 图表正常），Linux 没有；
CI 只能做 `xvfb` 启动冒烟（进程存活 + 建库），验不了画面与托盘。

**后续**：拿到一轮 Linux 真机反馈后切 AOT；改动只有 publish 参数，其余与 macOS 同构。

### 13. macOS 仍发 zip，不做 `.dmg`（已决策，含重开条件）

**结论**：macOS 继续发 `Tokenmetry-{版本}-osx-{arch}.zip`，不改成 `.dmg`。这是决定，不是排期缺口——
不要再把它当成待办或“体验债”摘回去重新评估。

**为什么收益不明显**：

- **步数上不赚。** 现在 zip 只有一个顶层条目 `Tokenmetry.app/`，Finder 解压后 app 直接落在原地：
  双击 zip → app 出现在旁边 → 双击运行 / 拖去 Applications，三步。dmg 是双击 → 等挂载 → 拖 → 推出
  镜像，四步。dmg 唯一的真本事是那张“往右拖”的示意图在教用户怎么做。
- **换不来信任。** Mac 上的信任信号是签名与公证，不是容器格式。没有证书时，两种格式首次打开弹的是
  同一句“无法验证开发者”——漂亮的 dmg 只是在漂亮的无背景窗口里弹同一个警告。
- **体积还略大。** 实测同一份 R2R `.app`（189MB）：dmg 74MB、zip 66MB（UDZO 对里面已压缩的原生库
  不如 deflate 划算）。
- **成本在契约面，不在那 4 行 `hdiutil`。** 资产名要同步改 `UpdateAssets.cs` / `ci.yml` /
  `package.sh` / `release.sh --watch` 四处，下载完整性校验也得改（dmg 开头没有魔数，标识 `koly`
  在文件末尾 512 字节）；而这条路径（应用内下载 macOS 资产）发版前在本机验不到，只能靠发版后真机点
  一次——为格式美观去沾一条“只能事后验证”的改动，不划算。

**什么时候重开**（满足任一条再做）：

1. 有 Apple 代码签名与公证了——那时 dmg 才能给出“打开就是干净的拖拽窗口、无警告”的完整体验；
2. 有 macOS 用户反馈“下下来不知道怎么装”；
3. 某次本来就要动 `package.sh` 的 mac 分支，顺带做（边际成本最低，但下载校验那条仍要单独验）。

**替代投入**：同样半天，花在第四条（快照双算）上对“数字可信”这个信任锚的价值远高于换容器格式。

## 五、内部小瑕疵（不影响日常使用）

- 迁移 2 的 `DROP TABLE usage_facts` 缺 `IF EXISTS`（迁移 1 有）。只在 `usage_facts` 不存在的库上才会炸，
  而迁移 1 一定会先建它——所以是洁癖级问题，不是隐患。
- 首次发版没有上一个 tag，`gh release create --generate-notes` 会把整个提交历史抖出来。首版用
  [`notes/v0.3.0.md`](notes/v0.3.0.md) 手写说明（CI 已支持 `docs/release/notes/<tag>.md`）。
- Windows 安装器从 v0.3.1 起提供：v0.3.0 的 Windows 用户要手动跑一次安装器才能进入「可静默升级」状态
  （安装到 `%LOCALAPPDATA%\Programs\Tokenmetry`，数据不受影响；旧 zip 目录可自行删掉）。
