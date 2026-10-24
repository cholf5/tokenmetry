#!/usr/bin/env bash
# Builds release assets into dist/:
#   dist/Tokenmetry-{VERSION}-{rid}.zip — rid ∈ osx-arm64 / osx-x64 / win-x64 / linux-x64
#
# Usage: scripts/package.sh [--rids "osx-arm64 osx-x64 ..."]
#
# The version is read from src/Tokenmetry.App/Tokenmetry.App.csproj <Version>
# (the same single source the release tag and the in-app update check use) and
# baked into the asset name, the macOS Info.plist and nothing else — one fact,
# three consumers.
#
# Asset naming is a cross-component contract: src/Tokenmetry.App/Update/
# UpdateAssets.cs builds download URLs from exactly these names, and
# .github/workflows/ci.yml uploads them verbatim. Renaming here breaks
# "下载更新包" for every installed client.
#
# Windows from CI runs the equivalent publish inline (workflow windows job);
# this script still accepts win-* rids for local cross-publish experiments.
# macOS .app 的 Info.plist 由本脚本组装；应用内不含任何需要外部布局的数据资产，
# 所以没有 stupid-dict 的「带词典变体」与独立 data release。

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST="$REPO_ROOT/dist"
RID_LIST=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rids) RID_LIST="$2"; shift 2 ;;
    *) echo "未知参数: $1" >&2; exit 1 ;;
  esac
done
RID_LIST=${RID_LIST:-"osx-arm64"}

# RID whitelist: rids are spliced into rm -rf / output paths below, so reject
# anything that is not a plain lowercase RID ("../.." must never reach rm -rf).
# The regex lives in a variable: unquoted =~ literals are flaky on bash 3.2
# (the stock macOS bash this script must survive).
rid_re='^[a-z0-9]+(-[a-z0-9.]+)*$'
for rid in $RID_LIST; do
  [[ "$rid" =~ $rid_re ]] || {
    echo "非法 RID: ${rid}（须形如 osx-arm64 / win-x64：小写字母数字，短横线或点分段，不含路径分隔符）"
    exit 1
  }
done

# Finder「显示简介」读 CFBundleShortVersionString——从 csproj <Version> 注入（与发版 tag
# 同一来源）。读不到时回退 1.0（本机临时打包的兜底）。
APP_VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$REPO_ROOT/src/Tokenmetry.App/Tokenmetry.App.csproj" | head -1 | tr -d '[:space:]')
APP_VERSION=${APP_VERSION:-1.0}

# Ad-hoc sign the assembled .app: the single-file apphost's embedded signature
# covers only the executable itself, so a bundle that was never re-sealed
# fails `codesign --verify`, and a *downloaded* copy (quarantine attribute set)
# is rejected by Gatekeeper as "damaged" with no Allow-Anyway path — that
# escape hatch exists only for a valid signature from an unidentified
# developer. Ad-hoc keeps the seal intact, so the downloaded copy gets the
# standard unidentified-developer prompt instead. Notarization would remove
# the prompt entirely but needs a paid Apple Developer account.
sign_app() {
  local app="$1"
  if command -v codesign >/dev/null 2>&1; then
    codesign --force -s - "$app" || { echo "ad-hoc 签名失败: ${app}" >&2; exit 1; }
    codesign --verify --strict "$app" || { echo "签名校验失败（封签不完整）: ${app}" >&2; exit 1; }
  else
    echo "警告: 本机无 codesign（Linux 交叉构建 osx 产物），${app} 未封签——本机可直接运行，但从网络下载分发会被 Gatekeeper 判 damaged" >&2
  fi
}

# .icns 由 app-icon.png 现场生成（sips + iconutil，macOS 自带）；Linux 交叉构建时跳过
# （警告见 sign_app——图标缺失只影响 Finder 观感，不影响可运行性）。
make_icns() {
  local png="$1" out="$2"
  if ! command -v iconutil >/dev/null 2>&1 || ! command -v sips >/dev/null 2>&1; then
    echo "警告: 无 sips/iconutil（非 macOS），跳过 .icns 生成：${out}" >&2
    return 1
  fi
  local iconset="${out%.icns}.iconset"
  rm -rf "$iconset"; mkdir -p "$iconset"
  for size in 16 32 64 128 256 512; do
    sips -z "$size" "$size" "$png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    local double=$((size * 2))
    sips -z "$double" "$double" "$png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$iconset" -o "$out"
  rm -rf "$iconset"
}

cd "$REPO_ROOT"
mkdir -p "$DIST"

for rid in $RID_LIST; do
  echo "==> publish $rid"
  dotnet publish src/Tokenmetry.App -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "dist/publish/$rid"

  # 发布产物名 = 程序集名 Tokenmetry.App(.exe)；出厂统一改回产品名 Tokenmetry(.exe)
  #（与 macOS .app 的 CFBundleExecutable、压缩包内布局一致）。
  PUBLISHED="dist/publish/$rid/Tokenmetry.App"
  [[ "$rid" == win-* ]] && PUBLISHED="dist/publish/$rid/Tokenmetry.App.exe"
  [[ -f "$PUBLISHED" ]] || { echo "发布产物缺失: $PUBLISHED"; exit 1; }

  STAGE="dist/stage/$rid"
  rm -rf "$STAGE"; mkdir -p "$STAGE"

  if [[ "$rid" == osx-* ]]; then
    # 最小 .app bundle：托盘常驻应用，双击可开（普通应用，不设 LSUIElement——主窗与 Dock 图标都是产品语义）。
    APP="$STAGE/Tokenmetry.app"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp "$PUBLISHED" "$APP/Contents/MacOS/Tokenmetry"
    chmod +x "$APP/Contents/MacOS/Tokenmetry"
    make_icns "$REPO_ROOT/src/Tokenmetry.App/Assets/app-icon.png" "$APP/Contents/Resources/app-icon.icns" || true
    cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
  <dict>
    <key>CFBundleName</key><string>Tokenmetry</string>
    <key>CFBundleExecutable</key><string>Tokenmetry</string>
    <key>CFBundleIdentifier</key><string>com.cholf5.tokenmetry</string>
    <key>CFBundleShortVersionString</key><string>${APP_VERSION}</string>
    <key>CFBundleIconFile</key><string>app-icon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleHighResolutionCapable</key><true/>
    <key>NSHighResolutionCapable</key><true/>
  </dict>
</plist>
PLIST
    sign_app "$APP"
  else
    # win/linux：单文件可执行，zip 内即产品名（Tokenmetry.exe / Tokenmetry）。
    if [[ "$rid" == win-* ]]; then
      cp "$PUBLISHED" "$STAGE/Tokenmetry.exe"
    else
      cp "$PUBLISHED" "$STAGE/Tokenmetry"
    fi
  fi

  APP_ZIP="$DIST/Tokenmetry-${APP_VERSION}-${rid}.zip"
  # zip -r into an existing file UPDATES it instead of rebuilding: entries from
  # an earlier run would survive (stale layout mixed with new). Always start
  # from a fresh archive.
  rm -f "$APP_ZIP"
  (cd "$STAGE" && zip -q -r -X "$APP_ZIP" .)
  echo "    $APP_ZIP"
done

rm -rf dist/stage dist/publish

echo
echo "=== dist/ ==="
ls -lh "$DIST" | awk 'NR>1 {print "  " $9 "  (" $5 ")"}'

cat <<'NEXT'

=== 发布步骤（正常发版走 scripts/release.sh，CI 打包并建 Release）===
1. 本地手动兜底: gh release create vX dist/Tokenmetry-*.zip --title "..." --notes "..."
2. macOS 用户首次打开 .app: Gatekeeper 会提示无法验证开发者（ad-hoc 签名、未公证）
   ——右键「打开」一次，或系统设置 → 隐私与安全性 → 仍要打开；
   彻底绕过: xattr -cr "Tokenmetry.app"
NEXT
