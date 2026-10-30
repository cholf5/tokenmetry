#!/usr/bin/env bash
# Builds release assets into dist/:
#   dist/Tokenmetry-{VERSION}-{rid}.zip            — rid ∈ osx-arm64 / osx-x64 / win-x64 / linux-x64
#   dist/Tokenmetry-{VERSION}-win-x64-setup.exe     — Windows 安装器（Inno Setup；需本机有 ISCC，缺则跳过）
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
#
# AOT 产物是「exe + 若干 dylib」的散开形态（AOT 不支持 PublishSingleFile），
# 所以嵌套的原生库必须逐个先封，否则整包验签不过。
sign_app() {
  local app="$1"
  if command -v codesign >/dev/null 2>&1; then
    local nested
    for nested in "$app"/Contents/MacOS/*.dylib; do
      [[ -f "$nested" ]] || continue
      codesign --force -s - "$nested" || { echo "ad-hoc 签名失败: ${nested}" >&2; exit 1; }
    done
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

# Windows 安装器的取料：一份 AOT 散开产物（exe + 原生 dll）。
# AOT 不支持 PublishSingleFile，产物本来就是多个文件——正好匹配安装器“装进固定目录”的形态，
# 且没有首次启动把原生库解到 %TEMP% 的动作（那一步是杀软启发式与“企业禁用 temp”的经典翻车点）。
publish_windows_aot() {
  local rid="$1"
  echo "==> publish $rid (AOT, 供安装器)"
  dotnet publish src/Tokenmetry.App -c Release -r "$rid" \
    -p:PublishAot=true -p:PublishSingleFile=false -p:SelfContained=true \
    -o "dist/publish/$rid-aot"

  local stage="dist/stage/$rid-aot"
  rm -rf "$stage"; mkdir -p "$stage"
  # 整个 publish 目录搬过去再改名，不猜哪些文件是必需的：AOT 的原生依赖因平台/库版本
  # 而异（e_sqlite3.dll、ANGLE 的 av_libglesv2.dll…），漏一个就是启动即崩。
  cp -R "dist/publish/$rid-aot"/. "$stage"/
  rm -f "$stage"/*.pdb "$stage"/*.xml
  mv "$stage/Tokenmetry.App.exe" "$stage/Tokenmetry.exe"
  [[ -f "$stage/Tokenmetry.exe" ]] || { echo "AOT 产物缺失: $stage/Tokenmetry.exe" >&2; return 1; }
}

# Windows 安装器（Inno Setup）。ISCC 是 Windows 程序：只有 Windows 主机或显式给
# ISCC=<路径> 时才出，其余平台跳过并说明——发版由 CI 的 windows job 负责这一份。
build_windows_installer() {
  local rid="$1"
  local iscc="${ISCC:-}"
  if [[ -z "$iscc" ]]; then
    if [[ -x "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" ]]; then
      iscc="/c/Program Files (x86)/Inno Setup 6/ISCC.exe"
    elif command -v iscc >/dev/null 2>&1; then
      iscc="$(command -v iscc)"
    fi
  fi

  if [[ -z "$iscc" ]]; then
    echo "    跳过安装器（本机无 Inno Setup 的 ISCC；CI 的 windows job 会出 ${rid} 安装器）"
    return 0
  fi

  # 安装器从 stage 里的产品名 exe 取料（与 CI 同一布局），版本由 /D 注入。
  "$iscc" "/DMyAppVersion=${APP_VERSION}" "$REPO_ROOT/installer/Tokenmetry.iss" >/dev/null
  local setup="$DIST/Tokenmetry-${APP_VERSION}-${rid}-setup.exe"
  [[ -f "$setup" ]] || { echo "安装器产物缺失: $setup" >&2; return 1; }
  echo "    $setup"
}

cd "$REPO_ROOT"
mkdir -p "$DIST"

for rid in $RID_LIST; do
  if [[ "$rid" == osx-* ]]; then
    # macOS：AOT + 散开（P-009）。AOT 不支持 PublishSingleFile，所以形态就是
    # 「一个原生 exe + 几个 dylib」全放 Contents/MacOS/；好处是没有首次启动的
    # 原生库解包、体积从 ~190MB 降到 ~50MB、杀软看不到 temp 里写二进制。
    echo "==> publish $rid (AOT)"
    dotnet publish src/Tokenmetry.App -c Release -r "$rid" \
      -p:PublishAot=true -p:PublishSingleFile=false -p:SelfContained=true \
      -o "dist/publish/$rid"

    # 发布产物名 = 程序集名 Tokenmetry.App；出厂统一改回产品名 Tokenmetry
    #（与 CFBundleExecutable、压缩包内布局一致）。
    PUBLISHED="dist/publish/$rid/Tokenmetry.App"
    [[ -f "$PUBLISHED" ]] || { echo "发布产物缺失: $PUBLISHED" >&2; exit 1; }
  else
    # Windows / Linux：单文件 self-contained + ReadyToRun（P-003：预编译进产物，运行时 JIT
    # 代码堆变小、启动更快，代价是包体积 +30-50%）。
    # 为何这两条不 AOT：AOT 不支持单文件，会把产物压成目录，丢掉“一个文件就能跑”这个
    # 便携版/解压即用的全部价值。Windows 的安装器形态另用一份 AOT 产物（见下）。
    echo "==> publish $rid (single-file R2R)"
    dotnet publish src/Tokenmetry.App -c Release -r "$rid" --self-contained true \
      -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true \
      -o "dist/publish/$rid"

    PUBLISHED="dist/publish/$rid/Tokenmetry.App"
    [[ "$rid" == win-* ]] && PUBLISHED="dist/publish/$rid/Tokenmetry.App.exe"
    [[ -f "$PUBLISHED" ]] || { echo "发布产物缺失: $PUBLISHED" >&2; exit 1; }
  fi

  STAGE="dist/stage/$rid"
  rm -rf "$STAGE"; mkdir -p "$STAGE"

  if [[ "$rid" == osx-* ]]; then
    # 最小 .app bundle：托盘常驻应用，双击可开（普通应用，不设 LSUIElement——主窗与 Dock 图标都是产品语义）。
    APP="$STAGE/Tokenmetry.app"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp "$PUBLISHED" "$APP/Contents/MacOS/Tokenmetry"
    cp "dist/publish/$rid"/*.dylib "$APP/Contents/MacOS/"
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
      # 安装器另用一份 AOT 散开产物：zip 保「一个 exe」，安装器保「无解包、启动最快」。
      publish_windows_aot "$rid"
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

  if [[ "$rid" == win-* ]]; then
    build_windows_installer "$rid"
  fi
done

rm -rf dist/stage dist/publish

echo
echo "=== dist/ ==="
ls -lh "$DIST" | awk 'NR>1 {print "  " $9 "  (" $5 ")"}'

cat <<'NEXT'

=== 发布步骤（正常发版走 scripts/release.sh，CI 打包并建 Release）===
1. 本地手动兜底: gh release create vX dist/Tokenmetry-* --title "..." --notes-file docs/release/notes/vX.md
   （Win 安装器与 zip 一个 glob 都带上；忘了 notes-file 会让 Release 正文变成提交列表）
2. macOS 用户首次打开 .app: Gatekeeper 会提示无法验证开发者（ad-hoc 签名、未公证）
   ——右键「打开」一次，或系统设置 → 隐私与安全性 → 仍要打开；
   彻底绕过: xattr -cr "Tokenmetry.app"
3. Windows 用户装 setup.exe: SmartScreen 提示未知发布者（未签名）→「更多信息」→「仍要运行」
NEXT
