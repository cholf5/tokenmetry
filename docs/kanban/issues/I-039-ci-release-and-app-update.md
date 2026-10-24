---
id: I-039
title: 发布自动化与版本更新机制——CI 三平台打包 + GitHub Release + 应用内检查/下载
type: feature
priority: P1
size: L
status: done
created: 2026-10-24
updated: 2026-10-24
blocked: none
---

## Goal

对齐 stupid-dict 的发布体系（参考 `~/dev/cholf5/stupid-dict`，机制同源改配置）：

1. **CI 打包发版**：tag `vX.Y.Z` 推送 → 跑全量测试 → 三平台打包（macOS .app zip arm64/x64、Windows 单文件 zip、Linux 单文件 zip）→ 自动创建 GitHub Release 并上传产物；PR/push 只跑测试。
2. **应用内更新检查**：读 GitHub 网页端 `releases/latest` 302 重定向（免 API 限流）与本地程序集版本比较；版本唯一事实 = csproj `<Version>`，CI 校验 tag 与之一致。
3. **更好的更新体验（超越"跳 Release 页手动找包"）**：设置页一键下载**当前平台**的更新包到「下载」文件夹（GitHub 直链 + CN 镜像回退、进度百分比），完成后自动在文件管理器中显示；托盘常驻「检查更新」菜单项，发现新版本后标题就地翻成「发现新版本 vX.Y.Z」、点击直达 Release 页；启动期静默周检（≥7 天才发请求，成败都记时间戳，不打扰）。

## Background

- 此前仓库无任何 CI（`.github/` 不存在），发布是 `docs/release/windows-publish.md` 的 v0.1 时代手动流程；应用内无版本号展示、无更新检查。
- 版本更新体验现状：检查到新版本后跳 GitHub Release 页让用户自己找对应平台的包。改进目标是不跳页、不找包：检查 → 应用内直下 → 文件管理器显示，用户只差一次解压/安装。
- **不做自更新替换**（明确不做）：应用无开发者签名/公证，运行中的单文件 exe（Windows）与 .app（macOS）替换自身是跨平台雷区（进程锁文件、封签、Gatekeeper）；下载 + reveal 已消除全部手工找包环节，收益/风险比不值。Windows Inno 安装器同理留待后续卡（需要时按 stupid-dict 的 `scripts/StupidDict.iss` 照抄）。
- GitHub 未认证 API 60 次/小时/IP，共享代理出口常 403——更新检查走网页端 302（stupid-dict 实证方案）。
- 版本检查/资产挑选/下载文件名是**跨组件契约**：`UpdateAssets.cs` ↔ `scripts/package.sh` ↔ `.github/workflows/ci.yml` 三处同改（workflow 头注释固化警示）。

## Acceptance

- [x] `.github/workflows/ci.yml`：test（ubuntu + CJK 字体 + tag/csproj 一致性校验）→ tag 触发 macos(arm64/x64)/windows/linux 打包 → release job（gh release create --generate-notes + upload --clobber）；paths-ignore 文档类改动不触发。
- [x] `scripts/package.sh`：三/四平台出包，macOS 组装 .app（Info.plist 注入 csproj 版本、iconutil 现场生成 icns、ad-hoc 封签）、win/linux 单文件改名产品名 Tokenmetry(.exe)；产物名 `Tokenmetry-{版本}-{rid}.zip`。
- [x] `scripts/release.sh`：版本 bump 提交 + tag + push + `--watch`（盯 CI、核对四资产齐全、恢复路径留注释）。
- [x] 应用内：Update/ 目录（UpdateChecker / UpdateAssets / UpdateDownloadService / UpdateService）+ 设置页「关于」区（版本、检查更新、下载、打开 Release 页、状态行）+ 托盘「检查更新」项（interop SetItemTitle 与 Avalonia Header 两路径就地翻标题）+ `AppSettings.LastUpdateCheckUtc` 周检节流。
- [x] i18n 全部文案进 Translations.cs（zh 源 + en 覆盖，20 个 key）。
- [x] 测试：App.Tests 增 Update 域 5 文件（检查器解析/比较、平台 RID 映射、下载镜像回退/进度/半截文件清理、服务状态机/周检门槛、设置页状态行联动）+ VM 用例 4 条。全量 **1442 Passed**（Core 183 / Reconciliation 49 / Infrastructure 661 / App 208 / Headless 341，本机 macOS 全绿）。
- [x] 打包实测（osx-arm64）：44MB zip，`codesign --verify --strict` 过、plist 版本 0.2.0、icns 生成。真机三平台安装/自更新冒烟移交用户（见 Development Log）。

## Dependencies

- none

## 明确不做 / 后续

- 自更新替换（应用无签名，见 Background）；Windows Inno 安装器；macOS 公证（需付费开发者账号）；Linux AppImage（自更新友好形态，需时立卡）。
- CI 上 headless 测试尚无一次真实运行验证（本地 macOS 全绿；Linux runner 若有渲染类差异按需修）。

## Development Log

### 2026-10-24

- 建卡并落地。机制整体移植 stupid-dict（UpdateChecker 302 方案 / release.sh bump-tag-watch / ad-hoc 封签与 Gatekeeper 说明），差异点：① 仓库无 data release（本应用资产全内嵌，删去 dictionary 分支）；② 程序集名 Tokenmetry.App → 出厂改名 Tokenmetry(.exe)（zip 内与 .app 的 CFBundleExecutable 一致）；③ icns 不进仓库、package.sh 用 sips+iconutil 现场生成（macOS runner 自带）；④ 托盘"检查更新"常驻项——MacTrayInterop 无运行期插项能力，只能安装时常驻 + SetItemTitle 就地翻状态（发现新版本后点击从"发起检查"切换为"打开 Release 页"）。
- 设置页与托盘共享同一 UpdateService DI 单例：任一处检查完成，另一处状态行/菜单标题同步（Changed 事件，TrayService 侧 RunOnUIThread marshal）。
- 下载失败聚合各镜像 host 的报因进状态行；`.part` 中转 + 失败清理，不落半截 zip；检查与下载互不阻塞（AsyncRelayCommand 天然禁重入）。
- 全量 1442 绿；package.sh osx-arm64 实测出包结构/签名/版本号三查过。
