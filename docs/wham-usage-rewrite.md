# ChatGPT Desktop 用量改写（WHAM）

> **合入状态**：实现、路由与审阅修复已通过 [PR #7](https://github.com/AliceJump/codex-muti/pull/7) 合入 `master`；源码合入不代表本机已部署或 Desktop 已通过验收。

ChatGPT Desktop 在订阅额度耗尽时，会根据 `GET …/wham/usage` 的 `rate_limit.allowed === false` 硬禁用发送，即使 `openai_base_url` 已指向自定义 provider。见 [openai/codex#48816](https://github.com/openai/codex/issues/48816)。

Codex Pool 在 loopback 上提供改写后的用量接口，同时转发其余 ChatGPT 后端请求：Desktop 使用自己的 ChatGPT 登录访问官方服务，按**模型路由**能否选择账户决定是否放行本地发送。

## 配置

在 `~/.codex/config.toml`：

```toml
openai_base_url = "http://127.0.0.1:3210/v1"
chatgpt_base_url = "http://127.0.0.1:3210/backend-api"
```

| 键 | 作用 |
|----|------|
| `openai_base_url` | 模型流量 → Daemon `/v1` |
| `chatgpt_base_url` | ChatGPT 后端请求 → Daemon `/backend-api`；用量在本地改写，其余请求转发官方服务 |

可选环境变量 `CODEX_API_BASE_URL=http://127.0.0.1:3210` 时，另有 `GET /api/codex/usage`，响应语义相同。

Daemon 仅监听 `127.0.0.1:3210`，拒绝非 loopback Host / 异源 Origin。

## 行为（PR #7）

| 条件 | `rate_limit.allowed` | `rate_limit.limit_reached` |
|------|----------------------|----------------------------|
| 当前账户可用 | `true` | `false` |
| 当前账户不可用，允许自动切换且同 provider 内有可用备用 | `true` | `false` |
| 当前账户不可用且自动切换关闭，或同 provider 内无可用账户 | `false` | `true` |
| 未选中账户且池内有可用初始账户 | `true` | `false` |
| 池为空或全部不可用 | `false` | `true` |

用量查询只读复用 `PoolRuntime.CanRouteModelRequest`，不改变 active、epoch、凭据或请求预留。`plan_type` 始终存在，未知时为 `unknown`。该路径不要求本地控制密钥（Desktop 带 ChatGPT bearer），但仍受 loopback 与 Host/Origin 约束。不会把 Desktop 登录导入账户池，也不会用其 token 向上游查真实用量。

## 登录状态与后端转发

`chatgpt_base_url` 还决定账号检查、profile 和配置初始化等请求的地址。Codex 在官方 OAuth 完成后会读取账号，读取失败会把登录结果改为失败。旧实现只支持用量接口，`/backend-api/wham/accounts/check` 等请求进入本地控制认证并得到 401，因此出现“配置后掉登录、重新登录也失败”。这条故障路径与池账户的 OAuth refresh 无关。

修复后，除上述两个用量路径外，`/backend-api/*` 与 `/api/codex/*` 使用客户端请求中的 Bearer 和账号头，单次转发至固定的 `https://chatgpt.com` 对应路径。支持仅存在于系统凭据库中的 Desktop 登录，不依赖 `auth.json`。不会选择池账户、自动重试、刷新或改写客户端登录；本地控制密钥禁止转发，浏览器 Cookie、CSRF 头及上游 Set-Cookie 均不转发。上游重定向返回 502，不跟随；官方真实 401 仍保留，网络错误返回 502，超时返回 504，错误正文使用本地安全代码。

升级后保持上面的两项配置即可。旧 Daemon 尚未升级时，临时移除 `chatgpt_base_url` 并重启客户端，可恢复官方后端直连；模型仍由 `openai_base_url` 进入池。如果客户端已清除登录或凭据确实过期，仍需完成官方重新登录。

源码证据：[账号检查路径](https://github.com/openai/codex/blob/6c372fbef45f730b6d8aa727b0ec8332365588db/codex-rs/backend-client/src/client.rs#L400)、[登录后读取账号并处理失败](https://github.com/openai/codex/blob/6c372fbef45f730b6d8aa727b0ec8332365588db/codex-rs/app-server/src/request_processors/account_processor.rs#L923)。官方凭据缓存与 refresh 行为见 [OpenAI Docs](https://developers.openai.com/codex/auth/#login-caching)。

## 与原生额度的关系

原生账户的真实 5h/weekly 额度仍由 `Quota` 读取官方 `chatgpt.com/backend-api/wham/usage`，见 [native-auth-adapter.md](native-auth-adapter.md)。本文接口只服务 **Desktop 发送按钮是否放开**，不是上游额度真相源。

## Desktop 验收

CLI / app-server 路径已有原生验收；**ChatGPT Desktop 经池发送的端到端验收仍待完成**。#7 合入后仍需单独做 Desktop 真机确认。

后端转发回归使用合成凭据覆盖账号检查、profile、配置初始化、身份与查询参数保留、本地凭据隔离、真实认证拒绝、重定向和网络失败。合成测试不代表真实账号重新登录或 refresh 已通过验收。
