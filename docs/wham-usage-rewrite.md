# ChatGPT Desktop 用量改写（WHAM）

> **合入状态**：初始用量路由通过 [PR #7](https://github.com/AliceJump/codex-muti/pull/7) 合入；登录转发与 HTTPS 兼容修复见 [PR #15](https://github.com/AliceJump/codex-muti/pull/15)。源码实现不代表本机已部署或 Desktop 已通过验收。

ChatGPT Desktop 在订阅额度耗尽时，会根据 `GET …/wham/usage` 的 `rate_limit.allowed === false` 硬禁用发送，即使 `openai_base_url` 已指向自定义 provider。见 [openai/codex#48816](https://github.com/openai/codex/issues/48816)。

Codex Pool 在 loopback 上提供改写后的用量接口，同时转发其余 ChatGPT 后端请求：Desktop 使用自己的 ChatGPT 登录读取完整官方用量，再按**模型路由**能否选择账户调整发送许可。

## 配置

新版 Codex 的 workspace routing 要求后端为 HTTPS，HTTP loopback 也不例外。先在当前 Windows 用户下安装本地服务器证书：

```powershell
pwsh -NoProfile -File scripts/setup-backend-https.ps1
```

脚本在 `CurrentUser\My` 创建 SAN 为 `127.0.0.1` 的服务器证书，私钥不可导出；将公开证书加入 `CurrentUser\Root` 信任。不会安装机器级 CA、导出 PFX、修改 Codex 配置或停止进程。可选 `-DataDirectory` 应与 Daemon 的 `CODEX_POOL_DATA_DIR` 一致，默认 `%LOCALAPPDATA%\CodexPool`；目录内只增加 `backend-https.thumbprint` 选择文件。

部署新版 Daemon 后，它读取证书选择文件，在 `127.0.0.1:3211` 增加 TLS 监听。选择文件不存在时沿用 HTTP 服务；文件存在但所选证书缺失、过期、没有私钥或 IP SAN / serverAuth 用途不匹配时拒绝启动，不降级为明文。证书有效期一年，再次执行脚本会复用有效证书或在剩余不足 14 天时更新证书；更新后需重新部署或重启 Daemon。

然后在 `~/.codex/config.toml` 配置并重启 Codex：

```toml
openai_base_url = "http://127.0.0.1:3210/v1"
chatgpt_base_url = "https://127.0.0.1:3211/backend-api"
```

| 键 | 作用 |
|----|------|
| `openai_base_url` | 模型流量 → Daemon `/v1` |
| `chatgpt_base_url` | ChatGPT 后端请求 → Daemon TLS 端口 `/backend-api`；用量在本地改写，其余请求转发官方服务 |

另有兼容路由 `GET /api/codex/usage`，响应语义相同。它不是已验证可独立配置的用量入口，不把 `CODEX_API_BASE_URL` 视为绕过 workspace routing 的办法。

两个端口仅监听 loopback，拒绝错误 Host / 异源 Origin。HTTPS 端口仅接受 `/backend-api/*` 与 `/api/codex/*`，不暴露 Dashboard、控制 API、OAuth callback 或模型入口；原 HTTP 端口行为保留。客户端发来的 Origin 必须与该连接协议、端口一致。

要停用：先移除 Codex 的 `chatgpt_base_url` 并重启 Codex，再移除数据目录中的 `backend-https.thumbprint` 并重启 Daemon。可在 `certmgr.msc` 的当前用户个人、受信任的根证书存储中按 `Codex Pool Local Backend` 名称和选择文件中的指纹移除本项目证书。

## 行为（PR #7）

| 条件 | `rate_limit.allowed` | `rate_limit.limit_reached` |
|------|----------------------|----------------------------|
| 当前账户可用 | `true` | `false` |
| 当前账户不可用，允许自动切换且同 provider 内有可用备用 | `true` | `false` |
| 当前账户不可用且自动切换关闭，或同 provider 内无可用账户 | `false` | `true` |
| 未选中账户且池内有可用初始账户 | `true` | `false` |
| 池为空或全部不可用 | `false` | `true` |

用量查询只读复用 `PoolRuntime.CanRouteModelRequest`，不改变 active、epoch、凭据或请求预留。请求必须带客户端自己的 ChatGPT Bearer，使用同一身份单次读取官方 WHAM；缺失返回 401，本地控制密钥返回 403，绝不出站。两个本地用量路径均读取官方 `/backend-api/wham/usage`。

完整保留官方 `user_id`、`account_id`、`plan_type`、额度窗口、重置时间、余额与未知字段；只修改 `rate_limit.allowed / limit_reached`。池可路由时，同时解除登录账户的 `spend_control.reached` / `credits.overage_limit_reached` 发送限制，清除普通额度耗尽的 `rate_limit_upsell`；保留 reserve 专属 banner，带 `normal_model_slug` 的附加桶按该模型的池可路由状态单独改写许可。不会把已用百分比改为零或编造余额。

原简化响应缺少 `user_id` 和窗口数据，只返回 `allowed=true`，无法保留 Desktop 身份匹配、用量详情及状态恢复所需信息。修复后不再使用池账户的套餐代替客户端套餐，也不使用请求头伪造响应账号。官方认证/限流失败保留状态码，网络失败返回 502/504，响应无效或超过 1 MiB 返回 502；不伪造成功用量、不重试、不切换池账户。`recentRequests` 只记录用量请求的时间、状态码和池许可，不记录身份、凭据或原始用量正文。

## 登录状态与后端转发

`chatgpt_base_url` 还决定账号检查、profile 和配置初始化等请求的地址。Codex 在官方 OAuth 完成后会读取账号，读取失败会把登录结果改为失败。旧实现只支持用量接口，`/backend-api/wham/accounts/check` 等请求进入本地控制认证并得到 401，因此出现“配置后掉登录、重新登录也失败”。这条故障路径与池账户的 OAuth refresh 无关。

修复后，除上述两个用量路径外，`/backend-api/*` 与 `/api/codex/*` 使用客户端请求中的 Bearer 和账号头，单次转发至固定的 `https://chatgpt.com` 对应路径。支持仅存在于系统凭据库中的 Desktop 登录，不依赖 `auth.json`。不会选择池账户、自动重试、刷新或改写客户端登录；本地控制密钥禁止转发，浏览器 Cookie、CSRF 头及上游 Set-Cookie 均不转发。上游重定向返回 502，不跟随；官方真实 401 仍保留，网络错误返回 502，超时返回 504，错误正文使用本地安全代码。

另一个独立故障是 `workspace backend must use an HTTPS origin without credentials`：Codex 在解析工作区后端时拒绝非 HTTPS 或 URL 内嵌用户名/密码，loopback 无豁免。这是客户端校验，后端透传无法单独修复。即使没有内嵌凭据，HTTP 仍会触发同一报错；不能将它理解为“登录凭据已丢失”。需完成上面的证书、TLS 部署与配置步骤。

尚未升级或需要先恢复登录时，临时移除 `chatgpt_base_url` 并重启客户端，可恢复官方后端直连；模型仍由 `openai_base_url` 进入池，暂时没有本地用量改写。如果凭据确实过期，仍需完成官方重新登录。

官方账号检查返回的 `workspace_backend_origin` 与 `account_routing_override` 原样透传。无约束工作区可沿用本地 HTTPS origin；特定区域或受管理工作区可能转向官方要求的后端，此时不能承诺本地用量改写生效。不会篡改工作区路由来强制本地入口。

源码证据：[账号检查路径](https://github.com/openai/codex/blob/6c372fbef45f730b6d8aa727b0ec8332365588db/codex-rs/backend-client/src/client.rs#L400)、[登录后读取账号并处理失败](https://github.com/openai/codex/blob/6c372fbef45f730b6d8aa727b0ec8332365588db/codex-rs/app-server/src/request_processors/account_processor.rs#L923)。官方凭据缓存与 refresh 行为见 [OpenAI Docs](https://developers.openai.com/codex/auth/#login-caching)。

HTTPS 校验与工作区路由选择见 [workspace_routing.rs](https://github.com/openai/codex/blob/6c372fbef45f730b6d8aa727b0ec8332365588db/codex-rs/app-server/src/request_processors/account_processor/workspace_routing.rs#L444)。

## 与原生额度的关系

原生账户的真实 5h/weekly 额度仍由 `Quota` 读取官方 `chatgpt.com/backend-api/wham/usage`，见 [native-auth-adapter.md](native-auth-adapter.md)。本文接口只服务 **Desktop 发送按钮是否放开**，不是上游额度真相源。

## Desktop 验收

CLI / app-server 路径已有原生验收；**ChatGPT Desktop 经池发送的端到端验收仍待完成**。#7 合入后仍需单独做 Desktop 真机确认。

后端转发回归使用合成凭据覆盖账号检查、profile、配置初始化、身份与查询参数保留、本地凭据隔离、真实认证拒绝、重定向和网络失败。用量回归额外检查完整身份/窗口/余额/未知字段保留、普通与模型专属许可、阻止本地密钥出站、上游错误和超大响应。合成测试不代表真实账号重新登录或 refresh 已通过验收。

现场只读对比已确认旧服务返回的 `allowed=true` 响应缺少官方 `user_id` 与 primary window，而官方接口同身份返回完整字段且 `allowed=false`。这能证明响应缺陷，不能单独证明实际发送按钮已恢复。安装客户端的用量走独立 HTTP fetch service，不能用修改 App Server `ordinaryUsageAllowed` 代替；当前桥接与启动入口保持原有实现。推送模式还可能接收官方 `usage.snapshot`，仅拒绝订阅 POST 不能保证隔离旧快照，不能拿 GET 改写测试当成 push/真实按钮验收。

HTTPS 回归使用临时自签名测试证书和空闲 loopback 端口，验证实际 TLS 握手、显式信任与未信任拒绝、证书 IP/有效期/用途校验及控制入口隔离；不写系统信任库。Windows 安装脚本、当前用户系统信任与真实 Desktop 登录仍需真机验收。
