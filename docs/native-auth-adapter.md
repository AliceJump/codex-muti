# 原生 Codex 授权适配器

Daemon 支持两套相互独立的 grant：

| 类型 | 路线 | 额度 |
|------|------|------|
| SIWC | 文档化的公开 Responses 路由 | 显示为未知 |
| Native | 官方 CLI 登录 + 源码确认的 Codex backend 路由 | 真实 5h / weekly 等窗口 |

SIWC token **不会**发往私有 Codex 端点。

## `native-login`

`native-login <name>` 在独立临时 `CODEX_HOME` 中启动官方 `codex login`（file 凭据），**不读不改**用户默认 Codex `auth.json`。helper 退出后，Daemon 校验 ID token，删除 helper 的 `auth.json`，再写入一代 DPAPI 凭据；之后只有 Daemon 刷新。若在删除与持久化之间崩溃，需要重新登录，而不会留下两个 refresh 持有者。

这是**授权适配**，不是 session / agent 适配。

## 额度

原生用量读取固定官方源码中的 endpoint 与字段（`chatgpt.com/backend-api/wham/usage`）。窗口时长区分 5h、weekly 等；缺失保持未知。每账户快照独立缓存，过期后界面可见陈旧，不伪装成 0%。该私有接口版本敏感，不是公开 API 承诺。

ChatGPT Desktop 经本机池发送时的用量改写（按**账户池可用性**返回 `rate_limit.allowed`）见 [wham-usage-rewrite.md](wham-usage-rewrite.md)（草稿 [PR #7](https://github.com/AliceJump/codex-muti/pull/7)）。那是 Desktop UI 放行逻辑，**不是**上游真实额度源。

## 验收指针

原生 A/B 登录、双侧 refresh 轮换、真实额度映射，以及小会话续接 / Compact / Resume / MCP 与配置阈值自动切换，已于 2026-10-13 通过小范围真实账户测试。范围与未清门槛见 [native-acceptance-results.md](native-acceptance-results.md)。自动迁移仍为 opt-in（`--live-boundary-probe`），小会话通过 ≠ 发布验收完成。
