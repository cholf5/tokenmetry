---
id: I-044
title: 上架 Homebrew cask——用 brew 装就绕开 macOS Gatekeeper 弹窗
type: infra
priority: P2
size: M
status: todo
created: 2026-10-30
updated: 2026-10-30
blocked: none
---

## Goal

让 macOS 用户能用 `brew install --cask tokenmetry` 装上：装完直接能双击打开，**不出现"无法验证开发者"弹窗**，
也就不需要走「系统设置 → 隐私与安全性 → 最下面 → 仍要打开 → 输开机密码」那一整套。

## Background

- **现状的痛**：包是 ad-hoc 签名、未公证。macOS 26 上**右键「打开」这条捷径已经不存在**——右键打开只会告诉你
  用不了，唯一图形路径是系统设置里最下面那个「仍要打开」再输开机密码。对不熟悉这套 SOP 的用户等于装不上；
  即便熟悉，也是在"信任感最低的时刻"先挨一记系统警告。见 `docs/release/known-limitations.md` 第 9 条。
- **为什么 cask 能解决**：Homebrew 安装 cask 时会去掉下载文件的 quarantine 标记，于是 Gatekeeper 那条检查
  根本不触发——用户看不到任何弹窗。这不是绕过安全检查，而是把"用户手动确认"换成"用户已经通过 brew 表达了
  对来源的信任"。
- **为什么不做 dmg**：已决策（同文件第 13 条）。而且 cask 直接吃 zip 就行——`url` 指向 Release 里的
  `Tokenmetry-{版本}-osx-{arch}.zip` + `app "Tokenmetry.app"`，不需要 dmg 形态，两条决策不冲突。
- **受众匹配**：Tokenmetry 的 macOS 用户是跑 AI 编码 agent 的开发者，其中相当比例本来就活在 brew 里。
  对这批人，cask 不是"小众替代"，而是**默认路径**。
- **成本**：$0。不买证书、不租签名、不上 dmg。

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
- [ ] **关键验收**：装完的 `Tokenmetry.app` 首次双击**不弹** Gatekeeper 对话框（即 quarantine 确实被剥离）；
      用 `xattr -l /Applications/Tokenmetry.app` 或 `xattr -p com.apple.quarantine` 留证
- [ ] `brew upgrade --cask tokenmetry` 能从 v0.3.2 升到下一个版本；`brew uninstall --cask tokenmetry` 干净
      （且**不删** `%LOCALAPPDATA%` 对应的 `~/Library/Application Support/Tokenmetry` 数据）
- [ ] 两架构各自的 sha256 与 Release 产物一致（Intel 资产没有真机也要核对哈希）
- [ ] 发版后的版本/哈希同步有可执行流程（脚本或文档里的两条命令）
- [ ] README（中英）+ known-limitations 更新

## 开放问题（实现前先验证，别假设）

- 当前 Homebrew 版本默认是否**总是**剥离 quarantine（历史上需要 `--no-quarantine`，后来改过默认值）——
  这条是整个卡的前提，验证方式就是上面那条 AC。
- 若用户是通过 `brew install` 直接从 GitHub Release 下载（无 quarantine），装完升级时 cask 的 `version`
  更新是否会被 Homebrew 自动感知（`livecheck` 需要 tap 里配 `livecheck do` 或接受手动 `brew update`）。
- 是否顺带提 `homebrew/cask`：如果将来买了证书/公证，再评估一次；当前**明确不做**。

## Dependencies

- none。不依赖 macOS 签名/公证（`known-limitations.md` 第 9 条），也不依赖 dmg（第 13 条）。

## Development Log

### 2026-10-30 建卡

- 用户指示建卡。触发场景：macOS 26 的放行流程被评估为"对非技术用户地狱"，而对开发者"认知成本为零"；
  结论是**不为这道墙买证书**，改为把墙绕开——brew 装 cask 是零成本且对这批受众最自然的路。
- 与既有决策的关系：补足第 13 条（不做 dmg）留下的空档——不需要 dmg，cask 吃 zip；也不与第 9 条
  （未签名）冲突——cask 只是让"放行"这一步不再发生在用户眼前。
