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

同一名称的重复登录请求会复用当前授权流程，避免重复点击时报 `native_login_already_running`。官方登录一次只运行一个；等待期间 Dashboard 禁用提交按钮，并提供“取消登录”。关闭对话框仅隐藏界面，不会取消授权。

若浏览器已关闭或需要换账户，可在 Dashboard 取消，或执行 `native-login-cancel <attempt-id>`（ID 来自 `status`）。取消接口为 `POST /accounts/native-login/{attempt-id}/cancel`，完成 helper 与临时目录清理后才返回。登录和输出排空统一受 5 分钟超时约束，超时后可重新登录；清理异常也不会遗留登录锁。

普通停服在登录进行时返回 busy；宿主开始退出后拒绝新登录，并取消、等待既有 helper 清理。额度查询与模型目录查询的 30 秒期限覆盖响应头和完整正文，避免上游保持连接却不发送正文时阻塞后续更新。调用者取消仍会向上传递；已经接受的 refresh 轮换继续完成凭据持久化。

## 额度

原生用量读取固定官方源码中的 endpoint 与字段（`chatgpt.com/backend-api/wham/usage`）。窗口时长区分 5h、weekly 等；缺失保持未知。每账户快照独立缓存，过期后界面可见陈旧，不伪装成 0%。该私有接口版本敏感，不是公开 API 承诺。

客户端用量改写与本地 ChatGPT 后端入口已经移除。客户端使用官方后端读取用量和登录状态，代理继续独立读取各池账户的真实额度。历史 Token 与请求记录见 [用量统计](usage-statistics.md)。

## 验收指针

原生 A/B 登录、双侧 refresh 轮换、真实额度映射，以及小会话续接 / Compact / Resume / MCP 与配置阈值自动切换，已于 2026-10-13 通过小范围真实账户测试。范围与未清门槛见 [native-acceptance-results.md](native-acceptance-results.md)。自动迁移仍为 opt-in（`--live-boundary-probe`），小会话通过 ≠ 发布验收完成。
