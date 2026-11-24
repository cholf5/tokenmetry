#!/usr/bin/env bash
# 重新生成 plugin-sdk/contract 下的契约 NuGet 包。
#
# 为什么单独一条命令：这个 nupkg 是插件作者离线 restore 的唯一契约来源，
# 手工 dotnet pack 既容易忘、也容易把旧版本留在目录里。改完
# src/Tokenmetry.Plugin.Abstractions 后跑一次，把 nupkg 与源码放进同一个提交。
#
# CI 的 verify-contract 只比对包内 DLL 与源码的公开 API，不要求逐字节一致：
# 不同 SDK / 不同提交编出的 PE 字节本来就会不同（InformationalVersion 里带提交号）。
set -euo pipefail
cd "$(dirname "$0")/.."

PROJECT=src/Tokenmetry.Plugin.Abstractions/Tokenmetry.Plugin.Abstractions.csproj
OUT=plugin-sdk/contract

# 先打到临时目录，成功了再替换：打包失败时旧包必须原地不动。
STAGING=$(mktemp -d)
trap 'rm -rf "$STAGING"' EXIT

dotnet pack "$PROJECT" -c Release -o "$STAGING" --nologo

# 目录里只保留当前版本：verify-contract 默认只认一个包，且 NuGet 会选最高版本。
rm -f "$OUT"/Tokenmetry.Plugin.Abstractions.*.nupkg
mv "$STAGING"/*.nupkg "$OUT"/

echo
echo "契约包已生成：$(ls "$OUT"/*.nupkg)"
echo "如有变化，把 nupkg 与 src/Tokenmetry.Plugin.Abstractions 一起提交。"

if command -v git >/dev/null 2>&1 && \
   ! git diff --quiet HEAD -- src/Tokenmetry.Plugin.Abstractions Directory.Build.props; then
  echo "注意：契约源码有未提交改动；包内的提交标记不代表最终提交内容。" >&2
fi
