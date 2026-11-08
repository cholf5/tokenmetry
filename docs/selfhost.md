# 自建中继（Self-hosting Tokenmetry Relay）

Tokenmetry 的跨设备同步由一个刻意"笨"的中继完成：每台设备把**本地聚合事实**（usage/session
两张事实表行 + 设备元数据）推到中继、拉回其他设备的行。中继可读数据（不做端到端加密），但不存
prompt、代码、会话内容——隐私红线与客户端一致。**默认不上传不登录**：只有你在设置页显式开启同步，
客户端才会发出任何网络请求。

排行榜（全球榜单）是 v1.1 规划，v1 中继不建任何排行榜表。

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

## Token 丢失 / 重签

- **换 token**：改 `TOKENMETRY_RELAY_TOKEN` 环境变量并重启容器。注意：换 token = 生成一个**新的空账号**
  （旧账号的数据仍在库中，但客户端需回到设置页用新 token 重新开启）。
- **删库重建**：`docker volume rm tokenmetry-relay-data`（或删除 `relay.db`）后重启。各设备会在
  下一轮同步把本地数据全量重推，无需迁移。
- 官方实例模式（GitHub 登录）每个账号可同时持有多台设备 token，重复登录互不踢线。

## 安全须知（务必阅读）

- **HTTP 明文**：客户端默认允许 `http://`。局域网内可用；跨公网请务必在反代（Caddy / nginx /
  Cloudflare Tunnel）终结 TLS，让客户端填 `https://` 地址——token 与用量数据都会过线。
- **限流未内建**：v1 没有请求频率 / 行数 / 存储配额限制（服务端留了 TODO）。自建在公网时请用
  反代层（nginx `limit_req`、Cloudflare）或防火墙白名单兜住；单租户 token 泄露的最大影响面 =
  你的用量数据被读写。
- **中继可读**：信任模型是不做 E2EE（U2）——运营者（或拿到服务器的人）能读到聚合事实行。介意就
  自建，不要用别人的实例。
- HTTPS 强制不在服务端做（A15，自建允许 http）；SQLite 走 WAL，建议把数据卷放在本地盘而非网络
  文件系统。

## 备份与清理

- 整个状态就是 `/data/relay.db`（WAL 模式，另有 `-wal`/`-shm` 辅助文件）——冷备份停容器后拷贝即可。
- 删除某台设备：客户端设置页「移除」（级联清该设备全部行），或客户端关同步（保留数据、查询回落本机）。
- 删除账号：客户端 `DELETE /api/me` 语义在官方模式可用；自建单租户直接删库最干净。
