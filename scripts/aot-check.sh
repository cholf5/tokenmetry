#!/bin/sh
# 本地 AOT 预检：把「打 tag 之后才发现的 IL2026/IL3050」提前成一条命令。
#
# 为什么需要它：AOT 分析器只在 PublishAot=true 时跑。日常 dotnet build / dotnet test 看不到，
# CI 的 test job（ubuntu 普通构建）也看不到——v0.6.0 就因此把 RelaySyncBackend 的反射 JSON
# 序列化一路漏到打 tag 之后，macOS/Windows 两个打包 job 各炸一次（16 条 IL 诊断），release
# 被跳过，只能删 tag 重跑。
#
# 只做 build 不做 publish：publish 需要目标平台的原生工具链（macOS 的 clang、Windows 的 MSVC），
# 跨平台跑不起来；而 IL 诊断、裁剪警告在 build 阶段就已经全部产出。
#
# 用法：
#   scripts/aot-check.sh                       # 四个发布 RID 全查
#   scripts/aot-check.sh "osx-arm64"           # 只查一个
#   scripts/aot-check.sh "osx-arm64 win-x64"   # 查指定的几个（空格分隔）
set -eu
cd "$(dirname "$0")/.."

PROJECT=src/Tokenmetry.App/Tokenmetry.App.csproj
RIDS="osx-arm64 osx-x64 linux-x64 win-x64"
[ $# -eq 0 ] || RIDS=$1

FAILED=
for rid in $RIDS; do
  printf '==> AOT 分析 %s\n' "$rid"
  if ! dotnet build "$PROJECT" -c Release -r "$rid" -p:PublishAot=true -v q --nologo; then
    FAILED="$FAILED $rid"
  fi
done

if [ -n "$FAILED" ]; then
  printf 'AOT 预检失败：%s\n' "$FAILED" >&2
  printf '按上面 IL2026/IL3050 的报错改（反射 JSON → source-gen、反射 API → 类型化调用）。\n' >&2
  exit 1
fi

printf 'AOT 预检通过：%s\n' "$RIDS"
