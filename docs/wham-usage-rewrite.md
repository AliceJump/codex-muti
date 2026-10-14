# ChatGPT Desktop 用量改写（WHAM）

ChatGPT Desktop 在订阅额度耗尽时会根据 `GET …/wham/usage` 的 `rate_limit.allowed === false` 硬禁用发送按钮，即使 `openai_base_url` 已指向自定义 provider。Codex Pool 在 loopback 上提供改写后的用量接口，使 Desktop 仍可使用本机 ChatGPT 登录的远端能力，同时按**模型路由**能否选择账户决定是否放行本地发送。

## 配置

在 `~/.codex/config.toml`：

```toml
openai_base_url = "http://127.0.0.1:3210/v1"
chatgpt_base_url = "http://127.0.0.1:3210/backend-api"
```

可选：设置环境变量 `CODEX_API_BASE_URL=http://127.0.0.1:3210` 时，对应路径为 `GET /api/codex/usage`，响应语义相同。

Daemon 仅监听 `127.0.0.1:3210`，拒绝非 loopback Host / 异源 Origin。

## 行为

| 条件 | `rate_limit.allowed` | `rate_limit.limit_reached` |
|------|----------------------|----------------------------|
| 当前账户可用 | `true` | `false` |
| 当前账户不可用，允许自动切换且同 provider 内有可用备用 | `true` | `false` |
| 当前账户不可用且自动切换关闭，或同 provider 内无可用账户 | `false` | `true` |
| 未选中账户且池内有可用初始账户 | `true` | `false` |
| 池为空或全部不可用（额度/授权/暂停等） | `false` | `true` |

实现见 `PoolRuntime.CanRouteModelRequest` 与 `WhamUsage.Response`；用量查询只读复用模型入口规则，不切换 active、推进 epoch、刷新凭据或预留请求。`plan_type` 始终存在，未知时为 `unknown`，满足官方 Codex 响应类型的必填约束。路由为 `GET /backend-api/wham/usage` 与 `GET /api/codex/usage`。该路径不要求本地控制密钥（Desktop 携带 ChatGPT bearer），但仍受 loopback 与 Host/Origin 校验约束。不会把 Desktop 登录导入账户池，也不会用其 token 向上游查询真实用量。

## 相关

上游问题背景：[openai/codex#48816](https://github.com/openai/codex/issues/48816)。原生账户真实额度仍由 `Quota` 读取官方 `chatgpt.com/backend-api/wham/usage`，见 [native-auth-adapter.md](native-auth-adapter.md)。