# 自建中继（Self-hosting Tokenmetry Relay）

Tokenmetry 的跨设备同步由一个刻意"笨"的中继完成：每台设备把**本地聚合事实**（usage/session
两张事实表行 + 设备元数据）推到中继、拉回其他设备的行。中继可读数据（不做端到端加密），但不存
prompt、代码、会话内容——隐私红线与客户端一致。**默认不上传不登录**：只有你在设置页显式开启同步，
客户端才会发出任何网络请求。

排行榜（全球榜单）自 v1.1 内置：`leaderboard_settings` / `leaderboard_rollup` 两张表随建库幂等创建
（`CREATE TABLE IF NOT EXISTS`），旧部署重启即自动补表，详见[下文](#排行榜v11)。

## 快速开始（Docker）

```bash
docker run -d --name tokenmetry-relay \
  -p 8080:8080 \
  -v tokenmetry-relay-data:/data \
  -e TOKENMETRY_RELAY_TOKEN=<换成你自己的长随机串> \
  ghcr.io/cholf5/tokenmetry-relay:latest
```

客户端侧（设置页 →「跨设备同步」→ 自建中继）：

- 服务器地址：`http://<你的主机>:8080`
- 访问令牌：与 `TOKENMETRY_RELAY_TOKEN` 相同的值

多台设备填同一地址 + 同一 token，即构成一个单租户账号（A15：token 即唯一账号凭据，无注册/
登录/找回体系——自建者拥有服务器，丢了 token 就 SSH 上去重签）。

## 环境变量

| 变量 | 作用 | 默认 |
|---|---|---|
| `TOKENMETRY_RELAY_TOKEN` | 单租户账号凭据（SHA-256 落库，明文不存储）。**非空 = 自建模式**；留空 = 纯官方模式（仅 GitHub OAuth，客户端无处登录） | 空 |
| `TOKENMETRY_RELAY_DB` | SQLite 库文件路径 | 容器内 `/data/relay.db`（裸跑为工作目录 `relay.db`） |
| `TOKENMETRY_RELAY_GITHUB_CLIENT_ID` / `_SECRET` | GitHub OAuth 交换（官方模式用）；自建不配即自动关闭该端点（404） | 空 |

## 端点一览

除 `/health`（匿名存活检查）、`/api/auth/github`（认证前身份交换）与 `GET /api/leaderboard`（公开
榜单读）外，全部要求 Bearer 访问令牌：

| 端点 | 方法 | 说明 |
|---|---|---|
| `/api/auth/github` | POST | GitHub OAuth 交换（官方模式；自建未配 Client ID/Secret 时 404） |
| `/api/me` | GET / DELETE | 账号信息 / 删除账号（级联清全部行、设备与榜单数据） |
| `/api/sync/push` | POST | 推送本地聚合事实行（按 (device, PK) LWW 落库；仅收**已注册**设备的行，未注册设备 400） |
| `/api/sync/pull` | GET | 拉取其他设备增量（单响应 ≤2000 行，`hasMore` 续拉） |
| `/api/devices` | GET / POST | 设备列表 / 注册 |
| `/api/devices/{deviceId}` | PATCH / DELETE | 设备改名 / 解绑（级联清该设备全部行；解绑设备的后续 push 一律 400 拒收，重新入网走 POST /api/devices 注册） |
| `/api/leaderboard` | GET | 公开榜单读：`board=cost\|tokens`（默认 cost）、`month=YYYY-MM`（默认当月）、`limit`（默认 100，钳 [1,100]）；匿名 + CORS 放行（仅 GET） |
| `/api/me/leaderboard` | GET / PUT | 排行榜偏好读/写（opt-in 与展示名；PUT 开启即全量重建 rollup，关闭即删） |

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
- 自建实例的榜单范围 = 你服务器上的账号集合：纯 token 单租户自建即只有你自己（榜单对单账号自建是
  自娱但无害）；配了 GitHub OAuth 则为该实例的全部 GitHub 账号。
- tokenmetry.app 的榜单页只读官方实例：官网 `apiBase` 与官方中继 URL 一样是**上线前的所有者配置项**
  （部署期覆盖，不硬编码进站点或客户端）。自建的榜单数据在你自己服务器的 `GET /api/leaderboard`。

## Token 丢失 / 重签

- **换 token**：改 `TOKENMETRY_RELAY_TOKEN` 环境变量并重启容器。注意：换 token = 退役旧账号并生成
  一个**新的空账号**——旧账号的行、设备与榜单数据随重签清除，各设备会在下一轮同步全量重推；客户端需
  回到设置页用新 token 重新开启。
- **删库重建**：`docker volume rm tokenmetry-relay-data`（或删除 `relay.db`）后重启。各设备会在
  下一轮同步把本地数据全量重推，无需迁移。
- 官方实例模式（GitHub 登录）每个账号可同时持有多台设备 token，重复登录互不踢线。

## 安全须知（务必阅读）

- **HTTP 明文**：客户端默认允许 `http://`。局域网内可用；跨公网请务必在反代（Caddy / nginx /
  Cloudflare Tunnel）终结 TLS，让客户端填 `https://` 地址——token 与用量数据都会过线。
- **限流未内建**：v1 没有请求频率 / 行数 / 存储配额限制（服务端留了 TODO）。事实上的单请求载荷上限
  是 Kestrel 默认 30 MB 请求体上限（约可容纳数万行事实/批），可在反代层进一步收紧。自建在公网时请用
  反代层（nginx `limit_req`、Cloudflare）或防火墙白名单兜住；单租户 token 泄露的最大影响面 =
  你的用量数据被读写。
- **中继可读**：信任模型是不做 E2EE（U2）——运营者（或拿到服务器的人）能读到聚合事实行。介意就
  自建，不要用别人的实例。
- **榜单公开读**：`GET /api/leaderboard` 对全网匿名开放（CORS 仅放行 GET、无凭据），上面只会出现
  显式 opt-in 账号的账号级聚合数字；不想暴露就保持设置页 opt-in 关闭。公开端点对限流的需求比同步
  端点更直接——反代层兜底同样适用。
- HTTPS 强制不在服务端做（A15，自建允许 http）；SQLite 走 WAL，建议把数据卷放在本地盘而非网络
  文件系统。

## 备份与清理

- 整个状态就是 `/data/relay.db`（WAL 模式，另有 `-wal`/`-shm` 辅助文件）——冷备份停容器后拷贝即可。
- 删除某台设备：客户端设置页「移除」（级联清该设备全部行），或客户端关同步（保留数据、查询回落本机）。
- 删除账号：客户端 `DELETE /api/me` 语义在官方模式可用；自建单租户直接删库最干净。
