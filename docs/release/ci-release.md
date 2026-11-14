# CI 打包发版与应用内更新（I-039）

> 本文档描述 2026-10-24 起的自动化发布体系；`windows-publish.md` 是 v0.1 时代的手动流程存档，
> 仅作本地兜底参考，正常发版不要走它。

## 一图流

```
scripts/release.sh 0.3.0          # bump csproj <Version> → commit → tag v0.3.0 → push
  └─ CI (.github/workflows/ci.yml)
       ├─ test: 全量 dotnet test（tag 必须等于 csproj <Version>，否则拒绝打包）
       ├─ macos:  scripts/package.sh --rids "osx-arm64"/"osx-x64" → AOT 散开 + .app bundle → zip
       ├─ windows: 单文件 publish → zip；另出一份 AOT 散开 → ISCC 安装器（随后静默装/卸冒烟）
       ├─ linux:  scripts/package.sh --rids linux-x64 → 单文件 → zip（xvfb 启动冒烟）
       └─ release: 为五个资产各生成 <资产名>.sha256 → gh release create v0.3.0
                   （说明取 docs/release/notes/v0.3.0.md，缺文件回落 --generate-notes）+ 上传五个资产与五份校验和
```

## 资产清单（五个）

| 资产 | 谁需要 |
|---|---|
| `Tokenmetry-{版本}-win-x64-setup.exe` | **Windows 首选**：Inno Setup 安装器（按用户装到 `%LOCALAPPDATA%\Programs\Tokenmetry`，带卸载器/快捷键/固定路径）；应用内静默升级也用它 |
| `Tokenmetry-{版本}-win-x64.zip` | Windows 便携版（免安装） |
| `Tokenmetry-{版本}-osx-arm64.zip` / `-osx-x64.zip` | macOS（.app bundle） |
| `Tokenmetry-{版本}-linux-x64.zip` | Linux 单文件 |
| `{上面任一资产}.sha256`（五份） | **强制**：应用内自更新在替换文件前校验；缺它就不更新 |

## 发版说明（Release notes）

手写说明放在 `docs/release/notes/<tag>.md`（如 `notes/v0.3.0.md`），CI 的 release 步骤检测到就
`--notes-file`，没有则回落 `--generate-notes` 按合自动生成。

**首个版本必须手写**：没有上一个 tag 时 `--generate-notes` 会把全部提交历史按合抖出来（二百余条），
读者看不到「这一版有什么」。后续版本可以两者混用：手写的亮点 + `--generate-notes` 的完整变更列表无法
直接合（gh 只接受一个来源），惯例是手写里自行链接「完整变更」。

亮点、已知限制（链接 [`known-limitations.md`](known-limitations.md)）、安装方式三块是基本盘；
安装方式里 macOS 的 Gatekeeper 提示与 Windows SmartScreen 提示必须写明，否则首版会被当成装不上。

## 打包形态矩阵（v0.3.2 起）

同一个应用按产物形态用两种构建，不是“顺手”：AOT 不支持 `PublishSingleFile`，而单文件的全部价值是
“一个文件就能跑”。两边各自取自己需要的那个。

| 产物 | 构建 | 为何 |
|---|---|---|
| macOS `.app` | **Native AOT，散开** | 无首次启动解包、198MB → 50MB（zip 66MB → 22MB）；dylib 与 exe 同在 `Contents/MacOS/` |
| Windows 安装器 | **Native AOT，散开** | 装进固定目录，单文件的“分发方便”没价值；无解包、启动最快、杀软看不到 temp 里写二进制 |
| Windows 便携 zip | 单文件 + R2R | “一个 exe 就能跑”是便携版存在的理由 |
| Linux zip | 单文件 + R2R | 解压即用；AOT 未真机验证（见 known-limitations） |

单文件 + `IncludeNativeLibrariesForSelfExtract=true` 的代价（实测 mac arm64）：首次启动把 20MB 原生库
写到 `%TEMP%\.net\<app>\<内容哈希>\`，暖启动 0.32–0.36s vs 冷启动 0.84s；每次版本更新解到新哈希目录，
旧目录等缓存回收。AOT 形态完全不碰 temp。

AOT 的两个硬前提：`TreatWarningsAsErrors` 下零 IL 警告（P-004 把反射 JSON 全迁到源生成）、
任何运行时反射都是“发布时静默、运行时炸”。Windows 与 macOS 都已真机验过；Linux 未验。

## 资产命名契约（改一处必须三处同改）

便携包：`Tokenmetry-{版本}-{rid}.zip`，rid ∈ `osx-arm64 | osx-x64 | win-x64 | linux-x64`。
Windows 安装器：`Tokenmetry-{版本}-win-x64-setup.exe`（Inno 脚本的 `OutputBaseFilename` 即此名）。
消费方：`scripts/package.sh`（产出）、`.github/workflows/ci.yml`（上传）、
`src/Tokenmetry.App/Update/UpdateAssets.cs`（应用内「下载更新包」按此名拼直链，并按
「是否跑在安装目录」在安装器与 zip 之间二选一）。
`scripts/release.sh --watch` 也按这份清单核对 Release 产物齐全（五条）。

## Windows 安装器（Inno Setup 6）

脚本：`installer/Tokenmetry.iss`；CI 的 windows job 里 `choco install innosetup` + `ISCC /DMyAppVersion=<版本>`；
本地 `scripts/package.sh --rids win-x64` 在有 ISCC 的机器上也会出（缺则跳过并提示）。

**取料是 AOT 散开产物**（`dist/stage/win-x64-aot`，由 `[Files] Source: {#SourceDir}\*` 整目录装入），
不是那个单文件 exe：安装器把文件装进固定目录，单文件的好处不存在，而 AOT 少了运行时 JIT 预热、
也把“启动时往 temp 写原生库”这个动作彻底去掉。**整目录复制、不猜依赖**——AOT 的原生依赖随平台/库版本变，
漏一个就是启动即崩。

几条硬约束（改了会静默地损坏升级路径）：

- `DefaultDirName` 必须等于 `UpdateAssets.InstallDirectory`（`%LOCALAPPDATA%\Programs\Tokenmetry`）。
  应用据此判定「已安装副本」→ 决定是下安装器还是下 zip；两边跑偏，已安装用户就永远拿不到静默升级。
- `AppId` 是永久身份：一改，覆盖安装就变成并存两个版本。
- `AppMutex=Local\Tokenmetry.SingleInstance` 与 `SingleInstanceGuard` 同名：托盘常驻应用不先让开文件锁，
  升级就是一场文件占用拉锯战。
- `[Run]` 的 postinstall 条目**不加** `skipifsilent`：静默升级后用户期待应用自己回来。
- 按用户安装（`PrivilegesRequired=lowest`）：不弹 UAC，也避开 Program Files 下升级要提权。

CI 在打包后紧跟一步静默装/卸冒烟（`/VERYSILENT` 安装 → 确认 exe/卸载器存在 → 确认 `[Run]` 把应用拉起 →
杀进程 → `/VERYSILENT` 卸载 → 确认 exe 消失）。向导本身的中文文案没有接入：Inno 官方只内置英文，
而静默升级不显示向导，所以暂时不值得为它引一份非官方翻译文件。

## 版本号唯一事实

`src/Tokenmetry.App/Tokenmetry.App.csproj` 的 `<Version>`（三段数字）：

- 应用内更新检查的比较基线（程序集版本，`UpdateChecker.CurrentVersion`）；
- macOS .app 的 `CFBundleShortVersionString`（package.sh 注入）；
- 发版 tag `v{Version}`（release.sh 打 tag，CI 在 tag push 时强制校验一致）；
- 设置页「关于」区展示值。

## 本地打包（兜底 / 验证）

```bash
scripts/package.sh --rids "osx-arm64"           # 或 "osx-arm64 osx-x64 linux-x64"
```

macOS 产物为最小 .app bundle：Info.plist 由脚本组装、`app-icon.icns` 用 sips+iconutil 现场生成、
ad-hoc 封签（`codesign --force -s -`）。**为什么要 ad-hoc**：单文件 apphost 内嵌签名只覆盖可执行
文件本身，组装后的 bundle 不重新封签会被 Gatekeeper 判 "damaged"（无「仍要打开」逃生口）；
ad-hoc 封签让下载副本走标准的「未验证开发者」提示（右键打开一次 / 系统设置放行 / `xattr -cr`）。
彻底消除该提示需要公证（付费 Apple 开发者账号，暂不做）。

Windows 发布在 CI 的 windows job 内联完成（publish + Compress-Archive）；package.sh 也接受
win-* rid 供本地交叉发布实验。

## 发版前预检（AOT）

```bash
scripts/aot-check.sh                # 四个发布 RID（osx-arm64 osx-x64 linux-x64 win-x64）
scripts/aot-check.sh "win-x64"      # 只查一个
```

**为什么必须单独查一遍**：AOT 分析器只在 `PublishAot=true` 时跑，日常 `dotnet build` /
`dotnet test` 看不到，CI 的 `test` job（ubuntu 普通构建）也看不到——只有打完 tag、macOS 与
Windows 两个打包 job 才会撞。v0.6.0 就是这么踩的：`RelaySyncBackend` 里四处反射 JSON 序列化
在打包时变成 16 条 `IL2026`/`IL3050`，`TreatWarningsAsErrors` 直接挡死，release job 被跳过，
只能删 tag 重跑。脚本只做 `build`（跨平台能做，IL 诊断已全部产出），不做 `publish`
（需要目标平台原生工具链）。

本地 `PublishAot=true` 会 restore 编译期工具链包 `Microsoft.DotNet.ILCompiler`，它跟随本机
运行时补丁号；旧补丁（< 10.0.14）撞 CVE-2026-111497 的 NuGet 审计，在 `TreatWarningsAsErrors`
下变成 `NU1903` 错误。`Directory.Build.props` 里对此单条 advisory 做了 `NuGetAuditSuppress`
（编译期工具、不进产物，发布路径用 runner 新补丁从不触发），补丁到位后可删。

## 发版步骤

```bash
scripts/release.sh 0.3.0            # 本地先跑全量测试，再 bump + tag + push
scripts/release.sh 0.3.0 --watch    # 追加：盯 CI 到完成并核对五资产齐全（自带 45 分钟上限）
```

前提：main 分支、工作树干净、与远端同步。失败恢复（tag 已推、CI 挂）：

```bash
git push origin :refs/tags/v0.3.0 && git tag -d v0.3.0   # 删 tag
# 修复后重跑 release.sh（csproj 已在目标版本时会跳过 bump 提交，直接重打 tag）
```

## 应用内更新机制（给后来者）

一句话：**能做的不是「热更」，是「一键重启式更新」**——Native AOT 把代码编译成原生机器码，
进程里没有可替换的程序集，任何方案都必须重启进程。用户看到的是：发现新版本 → 点一下 →
进度条 → 重启生效 → 「已更新到 vX」。

- **检查**：请求 `https://github.com/cholf5/tokenmetry/releases/latest`，**不跟随 302**——
  `Location` 头末段就是 tag。刻意不用 api.github.com：未认证 60 次/小时/IP，共享代理出口易 403。
  本地版本解析不出（开发构建）按 0.0.0 处理，任何 Release 都算新。
- **提醒**（三处共享同一个 `UpdateService` 单例，任一处触发其余同步）：
  1. **主窗顶部横幅**（`UpdateBannerViewModel`）：发现新版本时自动把隐藏的主窗叫回来（同一版本只叫
     一次），给「立即更新 / 稍后」；下载中就地显示百分比进度条；就绪后变「立即重启并更新」。
     托盘常驻应用不能只靠一条托盘菜单标题——那个位置没人看。
  2. **设置页「关于」区**：状态行 + 下载/重启按钮 + 进度条（与横幅同源同命令）。
  3. **托盘菜单**：「检查更新」在发现新版本后标题翻成「发现新版本 vX.Y.Z」。
  启动 30 秒后仍有一次静默周检（`AppSettings.LastUpdateCheckUtc` ≥7 天才发请求，成败都记），
  结果同样进横幅。
- **下载与校验**：按当前平台 RID 挑资产（macOS 分 arm64/x64；Windows/Linux 单资产），GitHub 直链
  + 三个 CN 加速镜像依次尝试，`.part` 中转防半截包，进度百分比进横幅与设置页。完整性三层：
  `Content-Length` 收满 → 载荷头魔数（zip = `PK\x03\x04`，安装器 = PE `MZ`）→ **SHA-256 对账**。
  最后一个不是加强项而是前提：镜像前缀是第三方，没有校验的自更新等于把执行权交给它们；
  校验文件缺失或对不上就拒绝更新，并删掉已下载的包（不留在下载文件夹里等人手滑双击）。
- **落地方式**（`UpdateApplyMode`）：
  - **Windows 安装副本** → 下 `…-win-x64-setup.exe`，用户点「立即重启并更新」后以
    `/SILENT /CLOSEAPPLICATIONS /NORESTART /SUPPRESSMSGBOXES` 拉起，发 `RestartRequested`
    让旧实例立刻正常退出（释放文件锁），安装器原地升级后按 `[Run]` 把新版本拉起来。
  - **便携版 / macOS / Linux** → `UpdateStager` 做「同卷两次 rename」：先把新载荷解包到目标同目录的
    `.tokenmetry-update-{tag}/`（慢，但不碰旧文件），再把旧目标 rename 成 `.old`、把新载荷 rename
    到目标路径（两次元数据操作，微秒级窗口），任何一步失败都回滚。**刻意不写接力脚本**：Windows
    批处理的编码（中文用户名/路径）与「等进程退出」轮询、macOS 的 shebang/xattr，每一项都能静默
    炸掉升级；rename 方案更短、跨平台一致、而且可测（`UpdateStagerTests` 在临时目录里跑真文件）。
  - **目标目录不可写**（`/Applications` 只读、企业策略）→ 退回下载包 + 在文件管理器里显示，
    由用户手动替换（`UpdateStageUnavailableException` → 状态行说明已下载的位置）。
- **重启交接**：新进程带 `--wait-for-exit <pid>` 启动，`Program.Main` 先等旧进程真的退出再抢单实例
  互斥量（`UpdateRelaunchHandshake`）。不等就会被单实例守卫当成第二实例，发个激活请求就自杀——
  用户看到「更新完应用不见了」。
- **成功判定 / 回滚兜底**：替换前把 `{pendingVersion, backupPath, targetPath}` 写进
  `%LOCALAPPDATA%\Tokenmetry\update-state.json`（`UpdateJournal`）。重启后版本号对上 = 成功 →
  清日志、删 `.old`、横幅弹「已更新到 vX」；对不上 = 替换没生效 → 横幅提示「上次更新未生效」并给出
  备份路径（按钮直达所在目录），旧版本仍在跑。启动时顺手清掉残留的 `.tokenmetry-update-*` 暂存区。
- **不做**：进程内热更（AOT 无 JIT）、无签名条件下的 macOS 静默公证流程、遥测式灰度。

