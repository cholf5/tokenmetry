# CI 打包发版与应用内更新（I-039）

> 本文档描述 2026-10-24 起的自动化发布体系；`windows-publish.md` 是 v0.1 时代的手动流程存档，
> 仅作本地兜底参考，正常发版不要走它。

## 一图流

```
scripts/release.sh 0.3.0          # bump csproj <Version> → commit → tag v0.3.0 → push
  └─ CI (.github/workflows/ci.yml)
       ├─ test: 全量 dotnet test（tag 必须等于 csproj <Version>，否则拒绝打包）
       ├─ macos:  scripts/package.sh --rids "osx-arm64"/"osx-x64" → Tokenmetry.app zip
       ├─ windows: 单文件 self-contained publish → Tokenmetry.exe → zip
       ├─ linux:  scripts/package.sh --rids linux-x64 → Tokenmetry → zip
       └─ release: gh release create v0.3.0 --generate-notes + 上传四个 zip
```

## 资产命名契约（改一处必须三处同改）

`Tokenmetry-{版本}-{rid}.zip`，rid ∈ `osx-arm64 | osx-x64 | win-x64 | linux-x64`。
消费方：`scripts/package.sh`（产出）、`.github/workflows/ci.yml`（上传）、
`src/Tokenmetry.App/Update/UpdateAssets.cs`（应用内「下载更新包」按此名拼直链）。
`scripts/release.sh --watch` 也按此清单核对 Release 产物齐全。

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
- **不做自更新替换**：应用无签名，运行中替换自身的单文件 exe / .app 是跨平台雷区（进程锁、封签、
  Gatekeeper）；用户拿到包后解压替换即完成更新（设置页 AboutHint 文案已如实说明）。
  若未来做 Windows 安装器（Inno）或 Linux AppImage，可再评估升级为半自动/全自动安装。
