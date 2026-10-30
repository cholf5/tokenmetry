---
id: I-044
title: 上架 Homebrew cask——给 brew 用户版本管理与升级便利（Gatekeeper 首次仍需放行一次）
type: infra
priority: P3
size: M
status: todo
created: 2026-10-30
updated: 2026-10-30
blocked: none
---

## Goal

让 macOS 用户能用 `brew install --cask tokenmetry` 装与升：装完仍**需要按 SOP 放行一次**（未公证绕不过去），
但从那以后 `brew upgrade --cask` 升级不再重复被 Gatekeeper 拦；另外获得 brew 用户习惯的版本管理与卸载路径。

> **前提已修正（2026-10-30 验证）**：本卡最初的目标是"用 brew 装就绕开弹窗"，**这个前提是错的**。
> 当前 Homebrew 会给 cask 下载主动打上 quarantine（见下节证据），不公证就无法绕过 Gatekeeper。
> 真正的收益变成"一次性放行 + 升级免重复放行"。

## Background

### 验证结论（2026-10-30，Homebrew 7.0.9 源码实测）

读本机实际安装的 Homebrew（`/opt/homebrew/Library/Homebrew`）得到三条硬事实：

1. **Homebrew 主动给下载打 quarantine**：`cask/download.rb:255` → `Quarantine.cask!(cask:, download_path:)`，
   代码里显式调 `kCFURLQuarantinePropertiesKey`，quarantine agent 名写的是 `"Homebrew Cask"`。
   也就是说 brew 下载被视为一次网络下载，**不是"可信来源豁免"**。
2. **没有 `--no-quarantine` 这条路了**：负责剥除的 `Cask::Quarantine.release!` 在整个代码库里
   **除测试外零调用**；`brew install --help` 也搜不到 quarantine 相关开关。
3. **但升级会继承上一次的放行**：`cask/quarantine.rb:169` 注释——"Fully remove quarantine only when
   explicitly requested; upgrades preserve it and inherit approval above."，配合 `USER_APPROVED_FLAG`
   （0x0100）的逻辑，用户放行过一次后，后续 `brew upgrade` 不再弹窗。

由此：**cask 不能替你消掉第一次的弹窗，但能消掉之后每一次的弹窗**。对比现状——用 zip 的用户
**每个新版本都要重新放行一次**（每个新 zip 都是新下载、都带 quarantine），这才是 cask 真正值钱的地方。

（本机无法完成端到端 `brew install --cask` 实测：brew 在此环境要临时 bootstrap portable-ruby，
ghcr.io 下载反复失败重试，已中止并清理，未污染 cask 清单。上面三条是代码级证据，不是运行时实测——
实现时仍需在一台干净机器上用 `xattr -p com.apple.quarantine` 亲眼确认一次。）

### 其余背景

- **现状的痛**：包是 ad-hoc 签名、未公证。macOS 26 上**右键「打开」这条捷径已经不存在**——右键打开只会告诉你
  用不了，唯一图形路径是系统设置里最下面那个「仍要打开」再输开机密码。对不熟悉这套 SOP 的用户等于装不上；
  即便熟悉，也是在"信任感最低的时刻"先挨一记系统警告。见 `docs/release/known-limitations.md` 第 9 条。
- **为什么以为 cask 能解决**（错的部分）：曾以为 Homebrew 安装 cask 时会去掉 quarantine，于是 Gatekeeper 检查
  根本不触发。验证后知道这条在 Homebrew 7.x 不成立。
- **为什么不做 dmg**：已决策（同文件第 13 条）。而且 cask 直接吃 zip 就行——`url` 指向 Release 里的
  `Tokenmetry-{版本}-osx-{arch}.zip` + `app "Tokenmetry.app"`，不需要 dmg 形态，两条决策不冲突。
- **受众匹配**：Tokenmetry 的 macOS 用户是跑 AI 编码 agent 的开发者，其中相当比例本来就活在 brew 里。
  对这批人，cask 不是"小众替代"，而是**默认路径**。
- **成本**：$0。不买证书、不租签名、不上 dmg；但也**不能指望它消掉首次弹窗**。

## 定案方向（待实现时确认）

1. **先自建 tap**（`cholf5/homebrew-tokenmetry`），不指望一步进 `homebrew/cask` 官方仓库——官方仓库对未公证
   应用有审核口径，进展不可控，也没必要先卡在那里。tap 完全自控，两条命令就能装。
2. cask 定义里按架构分资产：

   ```ruby
   on_arm do
     url "https://github.com/cholf5/tokenmetry/releases/download/v#{version}/Tokenmetry-#{version}-osx-arm64.zip"
     sha256 "..."
   end
   on_intel do
     url "https://github.com/cholf5/tokenmetry/releases/download/v#{version}/Tokenmetry-#{version}-osx-x64.zip"
     sha256 "..."
   end
   ```

   （具体写法以实现时 Homebrew 当前 DSL 为准；两架构 sha256 不同是硬约束。）
3. 发版后同步：`release.sh --watch` 核对完产物后，tap 里 `version` 与两个 `sha256` 要跟着动。先接受
   "发版后跑一条脚本 / 手动改两行"，稳定了再接 GitHub Action 自动 bump。
4. 文档：README 安装段加 `brew install --cask tokenmetry`（排在 zip 之前），`known-limitations.md` 第 9 条
   指向"用 brew 装可完全避开"。

## Acceptance Criteria

- [ ] tap 仓库建好，`brew tap cholf5/tokenmetry && brew install --cask tokenmetry` 能装上
- [ ] 装完按 macOS 26 SOP 放行一次（预期如此，不是回归）——留证：`xattr -p com.apple.quarantine`
      应能看到 `Homebrew Cask` 来源与 `0081`/`0083` 类标志
- [ ] **关键验收**：放行后 `brew upgrade --cask tokenmetry` 升到下一个版本，**不再弹 Gatekeeper**
      （即 upgrade 继承了上一次的 approval 位）；同时用 `xattr -p` 确认新版本仍带 quarantine 但已带
      user-approved 标志
- [ ] `brew uninstall --cask tokenmetry` 干净（且**不删** `~/Library/Application Support/Tokenmetry` 数据）
- [ ] 两架构各自的 sha256 与 Release 产物一致（Intel 资产没有真机也要核对哈希）
- [ ] 发版后的版本/哈希同步有可执行流程（脚本或文档里的两条命令）
- [ ] README（中英）+ known-limitations 更新：安装段把 brew 作为选项列出，**但不能写成“brew 装就不用放行”**——
      要如实写“首次仍需放行一次，之后升级不用”

## 开放问题（实现前先验证，别假设）

- ~~当前 Homebrew 默认是否总是剥离 quarantine~~ **已验证：不剥离，反而主动打上（不公证就绕不过）。**
  剩下要验的是“upgrade 是否真的继承 approval 不弹窗”——建议在一台干净 Mac 上装 v0.3.2 → 放行 →
  手工把 cask 改成 v0.3.1（或等下个版本）跑 `brew upgrade --cask`，看是否再弹。
- 若用户是通过 `brew install` 直接从 GitHub Release 下载，`version` 变化能否被自动感知（`livecheck` 需在 tap
  里配 `livecheck do` 或接受手动 `brew update`）。
- 是否顺带提 `homebrew/cask`：官方对未公证应用有审核口径；**当前明确不做**，除非将来买证书。
- **优先级：已定为 P3**（2026-10-30 用户确认）。收益从“免掉首次弹窗”降为“免掉每次升级弹窗”后，
  不值得按 P2 排期；动手前先把下面那条 AC 在干净机器上验一次。

## Dependencies

- none。不依赖 macOS 签名/公证（`known-limitations.md` 第 9 条），也不依赖 dmg（第 13 条）。

## Development Log

### 2026-10-30 建卡

- 用户指示建卡。触发场景：macOS 26 的放行流程被评估为"对非技术用户地狱"，而对开发者"认知成本为零"；
  结论是**不为这道墙买证书**，改为找一个零成本的分发渠道。
- 与既有决策的关系：补足第 13 条（不做 dmg）留下的空档——不需要 dmg，cask 吃 zip；也不与第 9 条
  （未签名）冲突。

### 2026-10-30 验证：前提被推翻，卡重定向

- 用户要求先验证“brew 装是不是真能免掉弹窗”。读本机 Homebrew 7.0.9 源码：
  `cask/download.rb:255` 主动调 `Quarantine.cask!` 给下载打 quarantine（agent 名 "Homebrew Cask"）；
  `Quarantine.release!` 除测试外零调用且无 `--no-quarantine` 开关；`quarantine.rb:169` 注释表明
  upgrade 会保留 quarantine 并继承上次 approval。
- 结论：**cask 不免疫 Gatekeeper**，原来写的“首次双击不弹窗”是不可能验收的 AC，已删除；
  收益改为“一次放行 + 之后 `brew upgrade` 不再弹”，这比 zip 的“每个新版本都要重新放行”确实好一档。
- 端到端 `brew install --cask` 未跑成：本机 brew 需临时 bootstrap portable-ruby，ghcr.io 下载反复失败，
  已中止；未安装任何东西、未污染 cask 清单。因此上面的结论是**代码级证据**，实现时仍需在一台干净机器
  上用 `xattr -p com.apple.quarantine` 亲眼确认，并把“upgrade 不再弹窗”作为唯一的硬验收。
- 优先级建议降为 P3（收益面变窄）；**2026-10-30 用户确认降为 P3**。
