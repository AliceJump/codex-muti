# ChatGPT Desktop 用量改写（WHAM）

> **合入状态**：实现与路由在草稿 [PR #7](https://github.com/AliceJump/codex-muti/pull/7)。未合入前，`master` 上的 Daemon **不保证**提供下列改写接口；本地若已组合检出 #7，可按本文配置验证。

ChatGPT Desktop 在订阅额度耗尽时，会根据 `GET …/wham/usage` 的 `rate_limit.allowed === false` 硬禁用发送，即使 `openai_base_url` 已指向自定义 provider。见 [openai/codex#48816](https://github.com/openai/codex/issues/48816)。

Codex Pool 在 loopback 上提供改写后的用量接口：Desktop 仍可使用本机 ChatGPT 登录的远端能力，同时按**账户池**是否还有可用账户决定是否放行本地发送。

## 配置

在 `~/.codex/config.toml`：

```toml
openai_base_url = "http://127.0.0.1:3210/v1"
chatgpt_base_url = "http://127.0.0.1:3210/backend-api"
```

| 键 | 作用 |
|----|------|
| `openai_base_url` | 模型流量 → Daemon `/v1` |
| `chatgpt_base_url` | Desktop 用量查询 → Daemon `/backend-api`（`GET /backend-api/wham/usage`） |

可选环境变量 `CODEX_API_BASE_URL=http://127.0.0.1:3210` 时，另有 `GET /api/codex/usage`，响应语义相同。

Daemon 仅监听 `127.0.0.1:3210`，拒绝非 loopback Host / 异源 Origin。

## 行为（PR #7）

| 条件 | `rate_limit.allowed` | `rate_limit.limit_reached` |
|------|----------------------|----------------------------|
| 池内任一账户 Available（复用 `PoolRuntime.Availability`） | `true` | `false` |
| 池为空或全部不可用 | `false` | `true` |

该路径不要求本地控制密钥（Desktop 带 ChatGPT bearer），但仍受 loopback 与 Host/Origin 约束。不会把 Desktop 登录导入账户池，也不会用其 token 向上游查真实用量。

## 与原生额度的关系

原生账户的真实 5h/weekly 额度仍由 `Quota` 读取官方 `chatgpt.com/backend-api/wham/usage`，见 [native-auth-adapter.md](native-auth-adapter.md)。本文接口只服务 **Desktop 发送按钮是否放开**，不是上游额度真相源。

## Desktop 验收

CLI / app-server 路径已有原生验收；**ChatGPT Desktop 经池发送的端到端验收仍待完成**。合入 #7 后仍需单独做 Desktop 真机确认。
