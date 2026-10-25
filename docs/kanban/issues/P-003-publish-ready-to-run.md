---
id: P-003
title: 发布产物启用 PublishReadyToRun——JIT 代码堆变小、启动更快
type: packaging
priority: P3
size: S
status: done
created: 2026-10-25
updated: 2026-10-25
blocked: none
---

## Goal

三平台发布产物（CI tag 打包 + package.sh）加 `-p:PublishReadyToRun=true`：方法体预编译进产物，运行时 JIT 代码堆（tier0/tier1 双份驻留）显著缩小，启动 CPU 同步下降。

## Background

- 内存调查（2026-10-25 会话）实测：应用稳态 Private ~230–280 MB，其中托管堆仅 35 MB，大头在原生侧；JIT 代码堆 + loader heap 是 App 侧可压缩项之一。R2R 把 tier0 预编译产物直接映射进进程（可跨进程共享页），对常驻托盘应用是零行为风险的纯收益。
- 发布是 self-contained single-file（CI ci.yml windows 直跑 dotnet publish；macOS/Linux 走 scripts/package.sh），R2R 与两者兼容（原生 R2R 镜像随包内嵌）。

## Acceptance Criteria

- [x] ci.yml windows publish 命令与 package.sh publish 命令均加 `-p:PublishReadyToRun=true`
- [x] 本地 win-x64 publish 冒烟通过（产物生成、可启动）
- [ ] 三平台 tag 打包由下次发版 CI 验证（本卡不阻塞——命令同构，风险在包体积）
- [x] 卡内登记包体积代价

## Dependencies

- none

## Development Log

### 2026-10-25 收口

- 改动两处：`.github/workflows/ci.yml` windows publish 步骤、`scripts/package.sh` publish 命令，均追加 `-p:PublishReadyToRun=true`。csproj 不加（避免本地普通 build 被 R2R 拖慢，发布侧单点生效）。
- 代价登记：单文件包体积上升约 30–50%（预编译原生码内嵌），换来启动 CPU 与运行时内存下降；对下载分发量是可接受交换，发版时在 Release notes 观察实际体积。
- 有意不做：PublishTrimmed（ScottPlot / 各适配器反射面广，裁剪风险不成比例，不与 R2R 捆绑）；PublishAot（Avalonia + ScottPlot 尚不成熟）。
- 本地冒烟：`dotnet publish -c Release -r win-x64 -p:PublishReadyToRun=true` 产物生成正常，`--autostart` 启动托盘正常（Windows 实机）。
