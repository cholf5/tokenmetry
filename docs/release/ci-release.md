# CI 打包发版与应用内更新（I-039）

> 本文档描述 2026-10-24 起的自动化发布体系；`windows-publish.md` 是 v0.1 时代的手动流程存档，
> 仅作本地兜底参考，正常发版不要走它。

## 一图流

```
scripts/release.sh 0.3.0          # bump csproj <Version> → commit → tag v0.3.0 → push
  └─ CI (.github/workflows/ci.yml)
       ├─ test: 全量 dotnet test（tag 必须等于 csproj <Version>，否则拒绝打包）
       ├─ macos:  scripts/package.sh --rids "osx-arm64"/"osx-x64" → Tokenmetry.app zip
       ├─ windows: 单文件 self-contained publish → Tokenmetry.exe → zip + ISCC 出 setup.exe（随后静默装/卸冒烟）
       ├─ linux:  scripts/package.sh --rids linux-x64 → Tokenmetry → zip
       └─ release: gh release create v0.3.0（说明取 docs/release/notes/v0.3.0.md，缺文件回落 --generate-notes）+ 上传五个资产
```

## 资产清单（五个）

| 资产 | 谁需要 |
|---|---|
| `Tokenmetry-{版本}-win-x64-setup.exe` | **Windows 首选**：Inno Setup 安装器（按用户装到 `%LOCALAPPDATA%\Programs\Tokenmetry`，带卸载器/快捷键/固定路径）；应用内静默升级也用它 |
| `Tokenmetry-{版本}-win-x64.zip` | Windows 便携版（免安装） |
| `Tokenmetry-{版本}-osx-arm64.zip` / `-osx-x64.zip` | macOS（.app bundle） |
| `Tokenmetry-{版本}-linux-x64.zip` | Linux 单文件 |

## 发版说明（Release notes）

手写说明放在 `docs/release/notes/<tag>.md`（如 `notes/v0.3.0.md`），CI 的 release 步骤检测到就
`--notes-file`，没有则回落 `--generate-notes` 按合自动生成。

**首个版本必须手写**：没有上一个 tag 时 `--generate-notes` 会把全部提交历史按合抖出来（二百余条），
读者看不到「这一版有什么」。后续版本可以两者混用：手写的亮点 + `--generate-notes` 的完整变更列表无法
直接合（gh 只接受一个来源），惯例是手写里自行链接「完整变更」。

亮点、已知限制（链接 [`known-limitations.md`](known-limitations.md)）、安装方式三块是基本盘；
安装方式里 macOS 的 Gatekeeper 提示与 Windows SmartScreen 提示必须写明，否则首版会被当成装不上。

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

## 发版步骤

```bash
scripts/release.sh 0.3.0            # 本地先跑全量测试，再 bump + tag + push
scripts/release.sh 0.3.0 --watch    # 追加：盯 CI 到完成并核对四资产齐全
```

前提：main 分支、工作树干净、与远端同步。失败恢复（tag 已推、CI 挂）：

```bash
git push origin :refs/tags/v0.3.0 && git tag -d v0.3.0   # 删 tag
# 修复后重跑 release.sh（csproj 已在目标版本时会跳过 bump 提交，直接重打 tag）
```

## 应用内更新机制（给后来者）

- **检查**：请求 `https://github.com/cholf5/tokenmetry/releases/latest`，**不跟随 302**——
  `Location` 头末段就是 tag。刻意不用 api.github.com：未认证 60 次/小时/IP，共享代理出口易 403。
  本地版本解析不出（开发构建）按 0.0.0 处理，任何 Release 都算新。
- **入口**：托盘菜单「检查更新」（发现新版本后标题翻成「发现新版本 vX.Y.Z」，点击直达 Release 页）
  与设置页「关于」区（状态行 + 下载/开页按钮）；两者共享 UpdateService 单例，任一处检查另一处同步。
  启动 30 秒后有一次静默周检（`AppSettings.LastUpdateCheckUtc` ≥7 天才发请求，成败都记——
  打不通的网络每次启动重试只是白费），只翻菜单标题，不弹窗。
- **下载**：按当前平台 RID 挑资产（macOS 分 arm64/x64 按进程架构；Windows/Linux 单资产），GitHub
  直链 + 三个 CN 加速镜像前缀依次尝试，`.part` 中转防半截包，进度百分比进设置页状态行；完成后在
  文件管理器中显示（explorer `/select` / Finder `open -R` / `xdg-open` 目录）。
  完整性：`Content-Length` 收满 + 载荷头魔数（zip → `PK\x03\x04`，Windows 安装器 → PE `MZ`）。
- **升级方式分两类**（`UpdateAssets.IsInstalledCopy`：进程可执行文件是否就在
  `%LOCALAPPDATA%\Programs\Tokenmetry`）：
  - **Windows 安装副本** → 下 `…-win-x64-setup.exe`，以
    `/SILENT /CLOSEAPPLICATIONS /NORESTART /SUPPRESSMSGBOXES` 拉起，发 `InstallStarted` 让旧实例
    立即正常退出（释放文件锁），安装器原地升级后按 `[Run]` 把新版本拉起来。全程不弹向导、不要求
    管理员（按用户安装）。
  - **便携版 / macOS / Linux** → 下 zip，在文件管理器里显示，用户解压替换。
    便携版**永不**被静默转成安装版（那等于替用户改了安装形态）。
- **不做自更新替文件**：替文件的是安装器（它懂文件锁重试与回滚），应用自己连连文件都不能碰；
  便携版与 macOS/Linux 依旧无自更新（应用无签名，运行中替换自身单文件 exe / .app 是跨平台雷区）。
  若未来上签名证书或 AppImage，可再评估。
