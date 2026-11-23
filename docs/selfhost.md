# 自建中继（Self-hosting Tokenmetry Relay）

Tokenmetry 的跨设备同步由一个刻意"笨"的中继完成：每台设备把**本地聚合事实**（usage/session
两张事实表行 + 设备元数据）推到中继、拉回其他设备的行。中继可读数据（不做端到端加密），但不存
prompt、代码、会话内容——隐私红线与客户端一致。**默认不上传不登录**：只有你在设置页显式开启同步，
客户端才会发出任何网络请求。

排行榜（全球榜单）自 v1.1 内置：`leaderboard_settings` / `leaderboard_rollup` 两张表随建库幂等创建
（`CREATE TABLE IF NOT EXISTS`），旧部署重启即自动补表；自建实例还自带一个只读网页
`/leaderboard`，详见[下文](#排行榜v11)。

应用设置同步自 v1.2 内置：`account_settings` 一张逐 key 的偏好表，同样幂等补表；客户端默认关，
详见[下文](#应用设置同步v12)。

## 快速开始（Docker）

```bash
docker run -d --name tokenmetry-relay \
  -p 8080:8080 \
  -v tokenmetry-relay-data:/data \
  -e TOKENMETRY_RELAY_TOKEN=<换成你自己的长随机串> \
  ghcr.io/cholf5/tokenmetry-relay:latest

# 多用户（可选）：给朋友签发独立账号
docker exec -it tokenmetry-relay /app/Tokenmetry.Relay token add --name Alice
```

## 快速开始（一键部署脚本，本仓库开发者）

仓库里有 `scripts/deploy-relay.sh`：本地交叉发布 linux-x64 单文件 → scp 上传 → 装成 systemd
服务 → 远端 `/health` 冒烟，一条命令完成首次部署与后续重部署。不想在服务器上装 Docker 的裸机
路线走这条：

```bash
scripts/deploy-relay.sh user@your-server                    # 自包含单文件（默认，远端零依赖）
scripts/deploy-relay.sh user@your-server --mode framework   # 复用远端 ASP.NET Core Runtime 10
```

脚本在服务器上落了什么：系统用户 `tokenmetry`（nologin）；安装目录 `/opt/tokenmetry-relay`
（整目录发布产物：`Tokenmetry.Relay` + `libe_sqlite3.so` 等原生库）；令牌
`/etc/tokenmetry-relay/token.env`（600，root 属主，systemd 以 EnvironmentFile 注入）；
数据目录 `/var/lib/tokenmetry-relay`（`relay.db`，WAL）；
`/etc/systemd/system/tokenmetry-relay.service`（`Restart=on-failure` + 常规加固）。
端口/地址/目录用 `--port` / `--bind` / `--install-dir` / `--data-dir` 覆盖；连接参数用
`--ssh-port` / `--ssh-opt` / `--scp-opt`（sudo 需要输密码时加 `--ssh-opt -t`）；`--dry-run`
只打印计划不动服务器。

令牌语义与 Docker 路线一致：

- 首次部署自动生成长随机令牌，写入 `/etc/tokenmetry-relay/token.env`（600，root 属主）
  并在脚本输出里回显；客户端填同一值。
- **重部署永远保留已有令牌**——换 token = 退役旧账号（行/设备/榜单随重签清除，见下文
  「Token 丢失 / 重签」），所以重跑脚本只换二进制并重启，绝不静默轮换；确要更换用
  `--rotate-token` 显式轮换。
- 重部署幂等：数据目录独立于安装目录，重跑不碰 `relay.db`。

主流发行版（Debian/RHEL/Arch/SUSE）开箱即用；Alpine 需先 `apk add shadow`（提供 useradd）。
公网可达通常隔两层防火墙：云安全组 + 服务器本机防火墙（如 ufw）——脚本会检测 ufw 并在部署
末尾从开发机做一次端到端探测，缺哪层它会提示。限流与 TLS 仍遵循本文「安全须知」：公网部署
请配反代终结 TLS。

客户端侧（设置页 →「跨设备同步」→ 自建中继）：

- 服务器地址：`http://<你的主机>:8080`
- 访问令牌：与 `TOKENMETRY_RELAY_TOKEN` 相同的值

多台设备填同一地址 + 同一 token，即构成一个单租户账号（A15：token 即唯一账号凭据，无注册/
登录/找回体系——自建者拥有服务器，丢了 token 就 SSH 上去重签）。

> 官方实例当前未开放：设置页里该选项置灰，客户端同步一律走「自建中继」。

## 多用户：把中继分享给朋友

`TOKENMETRY_RELAY_TOKEN` 是**运营者自己的单租户账号**：多台设备共用它 = 同一份数据。要让朋友各自
拥有独立数据，用中继自带的成员管理命令签发独立账号——每个成员一个 token，按 `account_id` 与你的
账号完全隔离；客户端不需要改，朋友仍然走「设置 → 跨设备同步 → 自建中继」。也可以用下文的
[Web 管理页面](#web-管理页面可选)完成同样的操作。

Docker：

```bash
docker exec -it tokenmetry-relay /app/Tokenmetry.Relay token add --name Alice
docker exec -it tokenmetry-relay /app/Tokenmetry.Relay token list
docker exec -it tokenmetry-relay /app/Tokenmetry.Relay token revoke --name Alice
```

systemd 裸机（CLI 直接读写同一个库，不需要停服务）：

```bash
sudo -u tokenmetry /opt/tokenmetry-relay/Tokenmetry.Relay \
  token add --name Alice --db /var/lib/tokenmetry-relay/relay.db
```

- `add` 输出一次性明文 token（库里只存 SHA-256），把**中继地址 + 该 token**一起发给朋友。
- 成员 token 丢失无法找回：先 `revoke` 再重新 `add`（旧 token 立即失效）。
- `list` 看成员、设备数与最后活跃时间；`revoke` 按展示名或 `--id` 吊销，级联删除该成员的同步行、
  设备与榜单记录（吊销后其 token 立即 401）。
- 展示名在成员中不区分大小写唯一，上限 64 字符。
- 运营者 token 轮换（`TOKENMETRY_RELAY_TOKEN` / 部署脚本 `--rotate-token`）只退役旧的 shared 账号，
  **不会**影响成员账号与他们的 token；成员用 `token revoke` 单独吊销。
- 成员之间互相看不见对方的数据；但中继运营者（你）能读库——这是「中继可读」信任模型的一致延伸。

## Web 管理页面（可选）

自建中继自带一个运营者管理页：`http://<你的主机>:8080/admin`（反代后就是你的 https 地址）。

- 用 `TOKENMETRY_RELAY_TOKEN`（运营者 token）登录；成员 token 会被拒绝（403）。
- 功能与 CLI 等价：查看成员（设备数、最后活跃、创建时间）、添加成员（token 只显示一次）、
  吊销成员（级联删除该成员的同步行、设备与榜单记录）。
- 页面本体是公开空壳，不含任何数据；所有管理 API 都要求
  `Authorization: Bearer <运营者 token>`。不开 Cookie 会话、不开 CORS，因此不会被跨站表单直接驱动。
- 登录后的 token 只放在当前标签页的 `sessionStorage`，关标签页即清除；公共机器上用完点「退出」。
- 页面与榜单页的脚本都是同源外部资源（`/assets/*.js`），CSP 为 `script-src 'self'`——不放行内联脚本。
- 页面标记 `noindex`；仍建议 HTTPS（token 会过线），且与 CLI 一样没有内建限流——公网部署请在反代层
  限流或加访问白名单。
- 不想开网页管理面时可以不用它：CLI 仍是最小面积的管理方式；页面与 CLI 读写同一套成员账号。

## 环境变量

| 变量 | 作用 | 默认 |
|---|---|---|
| `TOKENMETRY_RELAY_TOKEN` | 单租户账号凭据（SHA-256 落库，明文不存储）。**非空 = 自建模式**；留空 = 纯官方模式（仅 GitHub OAuth，客户端无处登录） | 空 |
| `TOKENMETRY_RELAY_DB` | SQLite 库文件路径 | 容器内 `/data/relay.db`（裸跑为工作目录 `relay.db`） |
| `TOKENMETRY_RELAY_GITHUB_CLIENT_ID` / `_SECRET` | GitHub OAuth 交换（官方模式用）；自建不配即自动关闭该端点（404） | 空 |

## 端点一览

除 `/health`（匿名存活检查）、`/leaderboard`（自建只读榜单网页）、`/admin`（管理页空壳）、
`/api/auth/github`（认证前身份交换）与 `GET /api/leaderboard`（公开榜单读）外，全部要求 Bearer 访问令牌：

| 端点 | 方法 | 说明 |
|---|---|---|
| `/leaderboard` | GET | 自建实例自带的只读排行榜网页（匿名；同源读 `/api/leaderboard`） |
| `/admin` | GET | 运营者管理页面（公开 HTML 空壳；数据 API 需运营者 token） |
| `/api/admin/members` | GET / POST | 成员列表 / 添加成员（Bearer + 运营者 `kind=shared`；POST 返回一次性 token） |
| `/api/admin/members/{accountId}` | DELETE | 吊销成员（Bearer + 运营者；级联清该成员全部数据） |
| `/api/auth/github` | POST | GitHub OAuth 交换（官方模式；自建未配 Client ID/Secret 时 404） |
| `/api/me` | GET / DELETE | 账号信息 / 删除账号（级联清全部行、设备与榜单数据） |
| `/api/sync/push` | POST | 推送本地聚合事实行（按 (device, PK) LWW 落库；仅收**已注册**设备的行，未注册设备 400） |
| `/api/sync/pull` | GET | 拉取其他设备增量（单响应 ≤2000 行，`hasMore` 续拉） |
| `/api/devices` | GET / POST | 设备列表 / 注册 |
| `/api/devices/{deviceId}` | PATCH / DELETE | 设备改名 / 解绑（级联清该设备全部行；解绑设备的后续 push 一律 400 拒收，重新入网走 POST /api/devices 注册） |
| `/api/leaderboard` | GET | 公开榜单读：`board=cost\|tokens`（默认 cost）、`month=YYYY-MM`（默认当月）、`limit`（默认 100，钳 [1,100]）；匿名 + CORS 放行（仅 GET） |
| `/api/me/leaderboard` | GET / PUT | 排行榜偏好读/写（opt-in 与展示名；PUT 开启即全量重建 rollup，关闭即删） |
| `/api/me/settings` | GET / PUT | 应用设置同步（逐 key 的纯偏好 TEXT；PUT 服务端赋时间戳并 upsert） |

## 应用设置同步（v1.2）

中继从 v1.2 起承载**一小块纯偏好**的跨设备同步：外观（画板 + 明暗模式）、界面语言、分享海报默认值、
自定义时间预设。在客户端「跨设备同步」区块里由独立开关「同步应用设置」控制，**默认关**；关 = 零出站。

- 存储是一张逐 key 的 `account_settings` 表，值统一为 TEXT（字符串原样、页边距 invariant 数字、
  预设清单 JSON）；写入时间戳由**服务端**赋值，客户端按它判断新旧——设备时钟偏移不会让某台机器永远赢。
- 设备级设置（开机自启、关窗行为、刷新间隔、文件日志、调试）与计价语义（模型目录/别名/价格覆盖）
  **永不过线**；同步凭据本身也不走这条通道。
- 表随建库幂等创建（`CREATE TABLE IF NOT EXISTS`），旧部署重启即自动补表；删除账号 / 更换 token /
  吊销成员都会级联清空该账号的设置行。
- 旧版中继没有这两个端点：客户端打开设置同步后会收到 404，请把中继升级到同一版本。

## 排行榜（v1.1）

中继内置两个月度全球榜单：**Token 榜**（按 total tokens 排名）与**金额榜**（按 cost 排名），通过
`GET /api/leaderboard` 匿名公开读。参与完全 opt-in：

- 默认不上榜。客户端在设置页「跨设备同步」区块单独 opt-in 并确认展示名；开启时服务端全量重建该账号
  的历史 rollup（历史月同样在榜），关闭时**即刻删除**其榜单设置与全部 rollup。
- 公开数据只有账号级聚合数字：排名、展示名（自定义名 → 账号默认名 → `"anonymous"` 回落）、GitHub
  头像（GitHub OAuth 账号才有）、当月 token 与成本合计。没有项目、模型、会话维度——表结构里就没有
  这些列。
- 维护零后台任务：opt-in 账号的 rollup 在 push 事务内按受影响日期增量重算；设备解绑触发全量重建，
  删号与换 token（旧账号退役）级联清理榜单数据。未 opt-in 的账号不写榜单表，push 路径零新增开销。
- 自建实例的榜单范围 = 你服务器上的账号集合：纯 token 单租户自建即只有你自己；用 `token add`
  加了成员、或配了 GitHub OAuth，则为该实例的全部账号。
- 自建实例自带只读网页：打开 `http://<你的主机>:8080/leaderboard`（反代后就是你的 https 地址）即可看
  当月 Token 榜 / 金额榜与历史月份。页面匿名、只读，数据来自同源 `GET /api/leaderboard`；只有显式
  opt-in 的账号会出现。App 设置页的「在网页查看」在自建连接下会直接打开这个页面。
- 成员账号没有 GitHub 头像与昵称，榜单展示名走「自定义名 → 账号展示名 → `"anonymous"`」回落；
  每个成员需要在客户端设置页自己打开排行榜 opt-in 才会出现在榜上。
- tokenmetry.app 的官网榜单页只读官方实例：官网 `apiBase` 与官方中继 URL 一样是**上线前的所有者配置项**
  （部署期覆盖，不硬编码进站点或客户端）。自建实例请用你自己的 `/leaderboard`。

## Token 丢失 / 重签

- **换 token**：改 `TOKENMETRY_RELAY_TOKEN` 环境变量并重启容器。注意：换 token = 退役旧账号并生成
  一个**新的空账号**——旧账号的行、设备与榜单数据随重签清除，各设备会在下一轮同步全量重推；客户端需
  回到设置页用新 token 重新开启。**成员账号不受影响**，他们的 token 独立有效。
- **删库重建**：`docker volume rm tokenmetry-relay-data`（或删除 `relay.db`）后重启。各设备会在
  下一轮同步把本地数据全量重推，无需迁移；库里的成员账号也一并消失，需要重新 `token add` 签发。
- **成员 token**：用 `token revoke --name <展示名>` 或 `--id <account-id>` 吊销，或在 `/admin`
  管理页点「吊销」；吊销即删该成员的同步行、设备、榜单设置与 rollup，无法恢复。
- 官方实例模式（GitHub 登录）每个账号可同时持有多台设备 token，重复登录互不踢线。

## 安全须知（务必阅读）

- **HTTP 明文**：客户端默认允许 `http://`。局域网内可用；跨公网请务必在反代（Caddy / nginx /
  Cloudflare Tunnel）终结 TLS，让客户端填 `https://` 地址——token 与用量数据都会过线。
- **限流未内建**：v1 没有请求频率 / 行数 / 存储配额限制（服务端留了 TODO）。事实上的单请求载荷上限
  是 Kestrel 默认 30 MB 请求体上限（约可容纳数万行事实/批），可在反代层进一步收紧。自建在公网时请用
  反代层（nginx `limit_req`、Cloudflare）或防火墙白名单兜住。**多用户时**每个成员 token 只影响该成员
  自己的账号数据（不能读写其他成员），但运营者 token 泄露 = 你的 shared 账号被读写；给朋友用之前
  务必上 HTTPS，并考虑按 token/账号在反代层加频率限制。
- **中继可读**：信任模型是不做 E2EE（U2）——运营者（或拿到服务器的人）能读到聚合事实行。介意就
  自建，不要用别人的实例。把中继分享给朋友时，对方也应知道这一点。
- **榜单公开读**：`GET /api/leaderboard` 对全网匿名开放（CORS 仅放行 GET、无凭据），上面只会出现
  显式 opt-in 账号的账号级聚合数字；不想暴露就保持设置页 opt-in 关闭。公开端点对限流的需求比同步
  端点更直接——反代层兜底同样适用。
- HTTPS 强制不在服务端做（A15，自建允许 http）；SQLite 走 WAL，建议把数据卷放在本地盘而非网络
  文件系统。

## 备份与清理

- 整个状态就是 `/data/relay.db`（WAL 模式，另有 `-wal`/`-shm` 辅助文件）——冷备份停容器后拷贝即可。
- 删除某台设备：客户端设置页「移除」（级联清该设备全部行），或客户端关同步（保留数据、查询回落本机）。
- 删除账号：客户端 `DELETE /api/me` 语义在官方模式可用；自建单租户直接删库最干净；成员账号用
  `Tokenmetry.Relay token revoke` 或 `/admin` 管理页吊销（级联清该成员全部数据）。
