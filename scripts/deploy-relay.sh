#!/usr/bin/env bash
# 一键部署 Tokenmetry Relay 到远程 Linux 服务器（systemd 裸跑，非 Docker 路线）。
#
# 本地交叉发布 linux-x64 发布目录（tar 整包上传）→ 装成 systemd 服务 → 远端 /health 冒烟。
# 首次部署生成访问令牌并持久化在服务器上；重部署幂等（只换发布目录 + 重启，
# 不碰令牌与数据）。docs/selfhost.md 的 Docker 快速开始之外的裸机路线。
#
# Usage: scripts/deploy-relay.sh <user@host> [options]
#   --mode self-contained|framework  发布模式：self-contained = 二进制自带 runtime，
#                                    远端零依赖（默认）；framework = 复用远端
#                                    ASP.NET Core Runtime 10，包小但远端须装 .NET
#   --port N                         服务监听端口（默认 8080）
#   --bind ADDR                      服务监听地址（默认 0.0.0.0；只供反代回源时用 127.0.0.1）
#   --install-dir DIR                服务器上的安装目录（默认 /opt/tokenmetry-relay）
#   --data-dir DIR                   服务器上的数据目录（默认 /var/lib/tokenmetry-relay，relay.db 所在）
#   --ssh-port N                     ssh/scp 连接端口（--ssh-opt "-p" 在 scp 里语义不同，单列此参数）
#   --ssh-opt OPT                    追加给 ssh 的参数（可重复，如 --ssh-opt -t；不含端口）
#   --scp-opt OPT                    追加给 scp 的参数（可重复；密钥可用 -o IdentityFile=…）
#   --token HEX/STR                  指定访问令牌（仅首次部署生效；服务器已有令牌时须改用 --rotate-token）
#   --rotate-token                   换令牌（= 退役旧账号，docs/selfhost.md「重签」语义）
#   --skip-build                     跳过本地构建，复用上次 publish 产物
#   --dry-run                        只打印将执行的动作与远端脚本，不跑构建/ssh/scp
#
# 服务器上落了什么：
#   <install-dir>/                          发布目录（Tokenmetry.Relay + libe_sqlite3.so 等）
#   /etc/tokenmetry-relay/token.env         TOKENMETRY_RELAY_TOKEN=…（600，root 属主）
#   <data-dir>/relay.db                     SQLite 事实库（WAL）
#   /etc/systemd/system/tokenmetry-relay.service
#   系统用户 tokenmetry（nologin，服务与文件属主）
#
# 设计要点：
#   - 令牌不进远端命令行（ps 可见）：写入走 ssh stdin | sudo tee。
#   - 发布目录整目录 ship：framework/self-contained 单文件的原生库（libe_sqlite3.so）
#     落在 exe 旁，只传 exe 必然启动即崩（真机踩过，Docker 路线靠 COPY 整目录没这坑）。
#     tar 上传 → 解到 stage 目录 → 目录级 rename 换装，运行中进程继续用旧 inode，
#     restart 时切到新目录——没有 ETXTBSY，也没有目录内容被逐个覆盖的窗口。
#   - 令牌放 /etc/tokenmetry-relay/（配置归 /etc，不随安装目录换装丢失）：重部署永远
#     保留 token.env——换令牌 = 旧账号的行/设备/榜单随重签清除（selfhost.md），
#     绝不能静默发生。
#   - 远端命令一律 POSIX sh（不假设 bash，Alpine 也能跑）；本地 bash 3.2 兼容
#     （release.sh 的教训：echo 变量带全角字符旁必须写 ${VAR} 花括号）。
#
# 明确不做（见 docs/selfhost.md 安全须知）：TLS/反代自动化（部署者责任）、限流
# （反代层兜底）、多实例（用户/单元名/数据布局固定，一台服务器一个实例）、回滚。

set -euo pipefail
cd "$(dirname "$0")/.."

usage() {
  sed -n '/^# Usage:/,/^# 设计要点：/p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-1}"
}

say() { printf '%s\n' "$*"; }
die() { printf 'deploy-relay: %s\n' "$*" >&2; exit 1; }

# 本地生成 64 位十六进制令牌（openssl 缺席时走 od，Git Bash/macOS/Linux 都有 od）。
gen_token() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 32
  else
    od -An -N32 -tx1 /dev/urandom | tr -d ' \n'
  fi
}

# 单引号包裹一个值用于远端 sh 命令串（本地展开后拼进远端命令文本）。
shq() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }

DRY_RUN=false
run_local() {
  if [ "$DRY_RUN" = true ]; then
    say "[dry-run] $*"
    return 0
  fi
  "$@"
}

# 在目标服务器上执行一条 POSIX sh 命令串（本地拼好，值经 shq）。
remote() {
  if [ "$DRY_RUN" = true ]; then
    say "[dry-run] ssh ${SSH_OPTS[*]+"${SSH_OPTS[*]}"} $TARGET ${1}"
    return 0
  fi
  ssh ${SSH_OPTS[@]+"${SSH_OPTS[@]}"} "$TARGET" "$1"
}

# 把本地 stdin 作为远端命令的 stdin（写 token.env / 单元文件用，内容不走 argv）。
remote_stdin() {
  if [ "$DRY_RUN" = true ]; then
    say "[dry-run] ssh ${SSH_OPTS[*]+"${SSH_OPTS[*]}"} $TARGET ${1}  << 以下 stdin:"
    sed 's/^/    | /'
    return 0
  fi
  ssh ${SSH_OPTS[@]+"${SSH_OPTS[@]}"} "$TARGET" "$1"
}

# ---- 参数解析 ---------------------------------------------------------------

TARGET=""
MODE="self-contained"
PORT="8080"
BIND="0.0.0.0"
INSTALL_DIR="/opt/tokenmetry-relay"
DATA_DIR="/var/lib/tokenmetry-relay"
SSH_PORT=""
SSH_OPT_INPUTS=""
SCP_OPT_INPUTS=""
TOKEN_INPUT=""
ROTATE_TOKEN=false
SKIP_BUILD=false
# 远端提权前缀（预检探测；dry-run 不探测，按 root 直跑计划）。
SUDO=""

while [ $# -gt 0 ]; do
  case "$1" in
    --mode) [ $# -ge 2 ] || die "--mode 缺参数"; MODE="$2"; shift 2 ;;
    --port) [ $# -ge 2 ] || die "--port 缺参数"; PORT="$2"; shift 2 ;;
    --bind) [ $# -ge 2 ] || die "--bind 缺参数"; BIND="$2"; shift 2 ;;
    --install-dir) [ $# -ge 2 ] || die "--install-dir 缺参数"; INSTALL_DIR="$2"; shift 2 ;;
    --data-dir) [ $# -ge 2 ] || die "--data-dir 缺参数"; DATA_DIR="$2"; shift 2 ;;
    --ssh-port) [ $# -ge 2 ] || die "--ssh-port 缺参数"; SSH_PORT="$2"; shift 2 ;;
    --ssh-opt) [ $# -ge 2 ] || die "--ssh-opt 缺参数"; SSH_OPT_INPUTS="$SSH_OPT_INPUTS
$2"; shift 2 ;;
    --scp-opt) [ $# -ge 2 ] || die "--scp-opt 缺参数"; SCP_OPT_INPUTS="$SCP_OPT_INPUTS
$2"; shift 2 ;;
    --token) [ $# -ge 2 ] || die "--token 缺参数"; TOKEN_INPUT="$2"; shift 2 ;;
    --rotate-token) ROTATE_TOKEN=true; shift ;;
    --skip-build) SKIP_BUILD=true; shift ;;
    --dry-run) DRY_RUN=true; shift ;;
    -h|--help) usage 0 ;;
    -*) die "未知参数: ${1}（-h 看用法）" ;;
    *)
      [ -z "$TARGET" ] || die "目标主机只能给一个：先给了 ${TARGET}，又给 ${1}"
      TARGET="$1"; shift ;;
  esac
done

[ -n "$TARGET" ] || usage 1
case "$MODE" in
  self-contained|framework) ;;
  *) die "--mode 只接受 self-contained 或 framework，得到 ${MODE}" ;;
esac
case "$PORT" in
  ''|*[!0-9]*) die "--port 须为纯数字，得到 ${PORT}" ;;
esac
[ "$PORT" -ge 1 ] && [ "$PORT" -le 65535 ] || die "--port 须在 1-65535"
case "$BIND" in
  ''|*[!0-9A-Za-z.\-:]*) die "--bind 只接受 IP/主机名字符（数字字母点横线冒号），得到 ${BIND}" ;;
esac
case "$SSH_PORT" in
  '') ;;
  *[!0-9]*) die "--ssh-port 须为纯数字，得到 ${SSH_PORT}" ;;
esac
# 路径进远端命令串与 scp 目标：空白/换行一律拒绝（scp 远端路径转义不值得做）。
for d in "$INSTALL_DIR" "$DATA_DIR"; do
  case "$d" in
    ''|*[[:space:]]*) die "路径含空白不支持: ${d}" ;;
  esac
done
# 令牌最终写进 systemd EnvironmentFile（KEY=value 行格式）：内部不允许空白。
if [ -n "$TOKEN_INPUT" ]; then
  case "$TOKEN_INPUT" in
    *[[:space:]]*) die "--token 不能含空白（会写坏 token.env 的 KEY=value 行）" ;;
  esac
  [ "${#TOKEN_INPUT}" -ge 16 ] || die "--token 太短（至少 16 字符；建议长随机串，见 docs/selfhost.md）"
fi

# ssh/scp 选项拼装：--ssh-port 统一转成 -o（ssh 与 scp 都认 -o Port=N）；
# --ssh-opt 只进 ssh（如 -t 给 sudo 密码用），--scp-opt 只进 scp。
SSH_OPTS=()
SCP_OPTS=()
if [ -n "$SSH_PORT" ]; then
  SSH_OPTS+=(-o "Port=${SSH_PORT}")
  SCP_OPTS+=(-o "Port=${SSH_PORT}")
fi
while IFS= read -r opt; do
  [ -z "$opt" ] && continue
  SSH_OPTS+=($opt)
done <<EOF_SSH
${SSH_OPT_INPUTS}
EOF_SSH
while IFS= read -r opt; do
  [ -z "$opt" ] && continue
  SCP_OPTS+=($opt)
done <<EOF_SCP
${SCP_OPT_INPUTS}
EOF_SCP

ENV_PATH="/etc/tokenmetry-relay/token.env"
ETC_Q=$(shq "/etc/tokenmetry-relay")
ENV_PATH_Q=$(shq "$ENV_PATH")
INSTALL_Q=$(shq "$INSTALL_DIR")
DATA_Q=$(shq "$DATA_DIR")
SERVICE_NAME="tokenmetry-relay"
UNIT_PATH="/etc/systemd/system/${SERVICE_NAME}.service"

# ---- 本地构建 ---------------------------------------------------------------

RELAY_CS="src/Tokenmetry.Relay/Tokenmetry.Relay.csproj"
OUT_DIR="dist/relay/${MODE}"
BINARY="${OUT_DIR}/Tokenmetry.Relay"

if [ "$SKIP_BUILD" = true ]; then
  say "跳过构建（--skip-build），复用上次产物"
else
  case "$MODE" in
    self-contained) SC_ARG=true ;;
    framework) SC_ARG=false ;;
  esac
  say "本地发布 ${MODE}（linux-x64 单文件）..."
  run_local dotnet publish "$RELAY_CS" -c Release -r linux-x64 --self-contained "$SC_ARG" -o "$OUT_DIR"
fi
if [ "$DRY_RUN" = true ]; then
  say "[dry-run] 检查构建产物: ${BINARY}"
else
  [ -f "$BINARY" ] || die "构建产物缺失: ${BINARY}（去掉 --skip-build 重跑）"
fi
say "产物: ${BINARY}"

# ---- 远端预检（sudo / systemd / token.env 现状一次带回）----------------------

ENV_EXISTS=false
if [ "$DRY_RUN" = true ]; then
  say "[dry-run] 跳过远端预检（sudo 探测、systemd、token.env 现状未知——按全新部署计划）"
else
  say "探测远端 ${TARGET} ..."
  PROBE_CMD="printf '%s %s %s\n' \"\$(if [ \"\$(id -u)\" = 0 ]; then echo root; elif command -v sudo >/dev/null 2>&1; then echo sudo; else echo none; fi)\" \"\$(if [ -d /run/systemd/system ]; then echo systemd; else echo nosystemd; fi)\" \"\$(if [ -f ${ENV_PATH_Q} ]; then echo hasenv; else echo noenv; fi)\""
  if ! PROBE_OUT=$(remote "$PROBE_CMD"); then
    die "ssh 连不上 ${TARGET}（检查主机别名/网络；自定义端口用 --ssh-port）"
  fi
  # shellcheck disable=SC2086
  set -- $PROBE_OUT
  SUDO_KIND="${1:-none}"; SYSTEMD_KIND="${2:-none}"; ENV_KIND="${3:-noenv}"
  case "$SUDO_KIND" in
    root) SUDO="" ;;
    sudo) SUDO="sudo " ;;
    *) die "非 root 且无 sudo，无法安装系统服务（或用 root 登录）" ;;
  esac
  [ "$SYSTEMD_KIND" = "systemd" ] || die "远端无 systemd（/run/systemd/system 不存在），本脚本只支持 systemd 路线"
  [ "$ENV_KIND" = "hasenv" ] && ENV_EXISTS=true
fi

# framework 模式：远端须有 ASP.NET Core Runtime 10（Sdk.Web 依赖 Microsoft.AspNetCore.App）。
if [ "$MODE" = "framework" ] && [ "$DRY_RUN" = false ]; then
  RT_CHECK='command -v dotnet >/dev/null 2>&1 && dotnet --list-runtimes 2>/dev/null | grep -q "^Microsoft\.AspNetCore\.App 10\." && echo OK || echo MISSING'
  if ! RT_OUT=$(remote "$RT_CHECK"); then
    die "ssh 连不上 ${TARGET}，无法核对远端 runtime"
  fi
  if [ "$RT_OUT" != "OK" ]; then
    die "远端没有 ASP.NET Core Runtime 10（dotnet --list-runtimes 无 Microsoft.AspNetCore.App 10.*）。先在远端安装，或改用 --mode self-contained"
  fi
  say "远端 ASP.NET Core Runtime 10 就绪"
fi

# ---- 准备用户与目录 ----------------------------------------------------------

say "准备系统用户与目录 ..."
PREP_SH=$(cat <<PREP
set -e
if ! command -v useradd >/dev/null 2>&1; then
  echo "missing useradd (Alpine: apk add shadow)" >&2
  exit 1
fi
if ! id tokenmetry >/dev/null 2>&1; then
  if command -v nologin >/dev/null 2>&1; then NL=\$(command -v nologin); else NL=/sbin/nologin; fi
  ${SUDO}useradd -r -s "\$NL" tokenmetry
fi
${SUDO}mkdir -p ${INSTALL_Q} ${DATA_Q} ${ETC_Q}
${SUDO}chown tokenmetry:tokenmetry ${INSTALL_Q} ${DATA_Q}
PREP
)
remote_stdin 'sh' <<EOF_PREP
${PREP_SH}
EOF_PREP

# ---- 令牌 --------------------------------------------------------------------
# 语义对齐 docs/selfhost.md：换 token = 退役旧账号（行/设备/榜单随重签清除），
# 所以默认重部署绝不碰 token.env；更换必须显式 --rotate-token。

NEW_TOKEN_WRITTEN=false
TOKEN=""
if [ "$ROTATE_TOKEN" = true ]; then
  if [ -z "$TOKEN_INPUT" ]; then
    TOKEN=$(gen_token)
  else
    TOKEN="$TOKEN_INPUT"
  fi
  say "轮换令牌（旧账号数据将随重签清除，各设备下一轮同步全量重推）..."
  printf 'TOKENMETRY_RELAY_TOKEN=%s\n' "$TOKEN" | remote_stdin "${SUDO}tee ${ENV_PATH_Q} >/dev/null && ${SUDO}chmod 600 ${ENV_PATH_Q}"
  NEW_TOKEN_WRITTEN=true
elif [ "$ENV_EXISTS" = true ]; then
  if [ -n "$TOKEN_INPUT" ]; then
    die "服务器已有令牌（${ENV_PATH}），重部署请去掉 --token；确要更换用 --rotate-token（会退役旧账号）"
  fi
  say "令牌沿用服务器已有值（token.env 不动）"
else
  if [ -z "$TOKEN_INPUT" ]; then
    TOKEN=$(gen_token)
  else
    TOKEN="$TOKEN_INPUT"
  fi
  say "生成并写入访问令牌 ..."
  printf 'TOKENMETRY_RELAY_TOKEN=%s\n' "$TOKEN" | remote_stdin "${SUDO}tee ${ENV_PATH_Q} >/dev/null && ${SUDO}chmod 600 ${ENV_PATH_Q}"
  NEW_TOKEN_WRITTEN=true
fi

# ---- 上传发布目录并整目录换装 ------------------------------------------------
# scp 以 SSH 用户身份写不了 /opt（root 属主）：tar 包先落到 /tmp，再 sudo 解到 stage
# 目录做目录级 rename 换装（stage 与目标同在 /opt 下，rename 同卷原子）。

TARBALL="dist/relay/${MODE}-deploy.tar"
TMP_TAR="/tmp/tokenmetry-relay.$$.tar"
TMP_TAR_Q=$(shq "$TMP_TAR")
STAGE_BIN_Q=$(shq "${INSTALL_DIR}.stage.$$/Tokenmetry.Relay")
STAGE_Q=$(shq "${INSTALL_DIR}.stage.$$")
OLD_Q=$(shq "${INSTALL_DIR}.old.$$")

if [ "$DRY_RUN" = true ]; then
  say "[dry-run] tar -cf ${TARBALL} -C ${OUT_DIR} ."
else
  tar -cf "$TARBALL" -C "$OUT_DIR" .
fi
say "上传发布目录包 ..."
if [ "$DRY_RUN" = true ]; then
  say "[dry-run] scp ${SCP_OPTS[*]+"${SCP_OPTS[*]}"} ${TARBALL} ${TARGET}:${TMP_TAR}"
else
  scp ${SCP_OPTS[@]+"${SCP_OPTS[@]}"} "$TARBALL" "$TARGET:$TMP_TAR"
fi
say "换装发布目录到 ${INSTALL_DIR} ..."
# Windows bsdtar 落盘的文件位不可靠：解包后显式 chmod 755 再换装。旧目录整个挪到
# .old.$$ 后删除——失败中途死掉时旧目录还在 .old.$$ 里，手工可恢复。
INSTALL_CMD="${SUDO}mkdir -p ${STAGE_Q} && ${SUDO}tar -xf ${TMP_TAR_Q} -C ${STAGE_Q} && ${SUDO}chmod 755 ${STAGE_BIN_Q} && ${SUDO}chown -R tokenmetry:tokenmetry ${STAGE_Q} && if [ -d ${INSTALL_Q} ]; then ${SUDO}mv ${INSTALL_Q} ${OLD_Q}; fi && ${SUDO}mv ${STAGE_Q} ${INSTALL_Q} && ${SUDO}rm -rf ${OLD_Q} ${TMP_TAR_Q}"
remote "$INSTALL_CMD"

# ---- systemd 单元 ------------------------------------------------------------

UNIT=$(cat <<UNIT
[Unit]
Description=Tokenmetry Relay (self-hosted sync relay)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=tokenmetry
Group=tokenmetry
WorkingDirectory=${INSTALL_DIR}
EnvironmentFile=${ENV_PATH}
Environment=TOKENMETRY_RELAY_DB=${DATA_DIR}/relay.db
Environment=ASPNETCORE_URLS=http://${BIND}:${PORT}
ExecStart=${INSTALL_DIR}/Tokenmetry.Relay
Restart=on-failure
RestartSec=3
NoNewPrivileges=true
ProtectSystem=full
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
UNIT
)
say "写入 systemd 单元 ${UNIT_PATH} ..."
UNIT_CMD="${SUDO}tee ${UNIT_PATH} >/dev/null && ${SUDO}systemctl daemon-reload && ${SUDO}systemctl enable ${SERVICE_NAME} >/dev/null"
printf '%s\n' "$UNIT" | remote_stdin "$UNIT_CMD"

say "重启服务 ..."
remote "${SUDO}systemctl restart ${SERVICE_NAME}"

# ---- 健康检查 ----------------------------------------------------------------
# curl → wget 回落；两者都无时退到 systemctl is-active（NOPROBE_*）。

HEALTH_SH=$(cat <<HEALTH
set -u
URL="http://127.0.0.1:${PORT}/health"
if command -v curl >/dev/null 2>&1; then
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
    curl -fsS "\$URL" >/dev/null 2>&1 && { echo HEALTHY; exit 0; }
    sleep 1
  done
  echo UNREACHABLE
  exit 3
fi
if command -v wget >/dev/null 2>&1; then
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
    wget -qO- "\$URL" >/dev/null 2>&1 && { echo HEALTHY; exit 0; }
    sleep 1
  done
  echo UNREACHABLE
  exit 3
fi
if systemctl is-active --quiet ${SERVICE_NAME}; then
  echo NOPROBE_ACTIVE
  exit 2
fi
echo NOPROBE_DOWN
exit 3
HEALTH
)
say "健康检查（/health，最长 15 秒）..."
HEALTH_RC=0
if [ "$DRY_RUN" = true ]; then
  # remote() 的 dry-run 输出走 stdout，被命令替换捕获就变成“远端输出”了：
  # 先让它打印，再按 HEALTHY 走计划。
  remote "$HEALTH_SH"
  HEALTH_OUT="HEALTHY"
else
  # set -e 下不能靠 if ! 取退出码（! 把 $? 反转成 0）：临时关 e 拿真实 rc。
  HEALTH_OUT=""
  set +e
  HEALTH_OUT=$(remote "$HEALTH_SH")
  HEALTH_RC=$?
  set -e
fi
case "$HEALTH_OUT" in
  HEALTHY) say "服务健康" ;;
  NOPROBE_ACTIVE)
    say "警告: 远端无 curl/wget，无法 HTTP 冒烟；systemd 报告服务 active（自行验证: curl http://${TARGET#*@}:${PORT}/health）"
    ;;
  *)
    say "健康检查失败（${HEALTH_OUT:-exit=${HEALTH_RC}}），最近日志：" >&2
    if [ "$DRY_RUN" = false ]; then
      remote "${SUDO}journalctl -u ${SERVICE_NAME} -n 40 --no-pager" >&2 || true
    fi
    die "部署未通过健康检查，按上方日志排查（数据在 ${DATA_DIR}/relay.db，重跑本脚本即重试）"
    ;;
esac

# ---- 可达性核查（服务器本机防火墙 + 开发机视角端到端）------------------------
# 真机教训：云安全组之外服务器上还可能有 ufw 这一层——127.0.0.1 健康检查全绿，
# 外部照样进不来。两层各查各的：ufw 给出精确修法，端到端探测兜住云侧防火墙。

HOST_PART="${TARGET#*@}"

say "防火墙核查 (ufw) ..."
UFW_SH=$(cat <<UFW
if command -v ufw >/dev/null 2>&1 && ${SUDO}ufw status 2>/dev/null | grep -q "Status: active"; then
  if ! ${SUDO}ufw status 2>/dev/null | grep -Eq "^${PORT}(/tcp)? "; then
    echo UFW_MISSING
    exit 0
  fi
fi
echo UFW_OK
UFW
)
if ! UFW_OUT=$(remote "$UFW_SH"); then
  UFW_OUT=""
fi
if [ "$UFW_OUT" = "UFW_MISSING" ]; then
  say "警告: 服务器 ufw 防火墙开启且未放行 ${PORT}/tcp，外部访问会被本机防火墙挡住。"
  say "      SSH 上去执行: sudo ufw allow ${PORT}/tcp（云安全组之外还有这一层）"
fi

if [ "$DRY_RUN" = false ] && [ "$BIND" != "127.0.0.1" ] && [ "$BIND" != "localhost" ]; then
  say "外网可达性探测 ..."
  if command -v curl >/dev/null 2>&1; then
    if curl -fsS --connect-timeout 5 -m 8 "http://${HOST_PART}:${PORT}/health" >/dev/null 2>&1; then
      say "外网可达"
    else
      say "警告: 开发机连不上 http://${HOST_PART}:${PORT}/health（服务器本机服务健康）——"
      say "      检查云安全组/上游防火墙是否放行 TCP ${PORT}，或本机出网受限。"
    fi
  fi
fi

# ---- 收尾 --------------------------------------------------------------------
say ""
say "部署完成。"
say "  服务: ${SERVICE_NAME}.service（systemd，Restart=on-failure）"
say "  地址: http://${HOST_PART}:${PORT}（bind=${BIND}）"
if [ "$NEW_TOKEN_WRITTEN" = true ]; then
  say "  令牌: ${TOKEN}"
else
  say "  令牌: 沿用服务器已有值（root 登录后: cat ${ENV_PATH}）"
fi
say ""
say "客户端（设置页 →「跨设备同步」→ 自建中继）："
say "  服务器地址: http://${HOST_PART}:${PORT}"
if [ "$NEW_TOKEN_WRITTEN" = true ]; then
  say "  访问令牌: ${TOKEN}"
else
  say "  访问令牌: <见服务器 ${ENV_PATH}>"
fi
say ""
say "提醒: 跨公网务必反代终结 TLS（Caddy / nginx / Cloudflare Tunnel），令牌与数据都过线——"
say "      见 docs/selfhost.md「安全须知」。重部署: 重跑本脚本即可（幂等，不碰令牌与数据）。"
