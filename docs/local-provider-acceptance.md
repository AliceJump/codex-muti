# 本地配置认证与模型选择验收

2026-10-13 使用官方 Codex CLI `0.157.1` 和正在运行的 localhost 代理验证。配置项在 CC Switch 中名为“Codex Pool（本地代理，保留登录）”，模型 provider ID 为 `codex_pool_local`。

模型访问使用 `auth.command`，由已发布的 Daemon 执行 `model-credential`，通过私有 stdout 管道读取本机 DPAPI 的本地密钥。`requires_openai_auth=false`，不覆盖官方 ChatGPT 登录。浏览器和控制 API 不返回密钥；直接在终端运行凭据命令会被拒绝。

隔离 `CODEX_HOME` 未复制任何官方登录凭据。分别以 `gpt-6-luna` 和 `gpt-6.1-sol` 验证模型回复、官方 Codex 压缩以及压缩后的信息保留，均完成。GPT-6.1 Sol 的 `thread/start` 解析结果为 `gpt-6.1-sol`，代理收到的三次请求模型字段均为同一 ID，HTTP 状态均为 200。`ResponsesRelay` 原样转发请求正文，不替换模型字段。

另一次仅检查协议元数据的请求中，传入模型为 `gpt-6.1-sol`，上游 SSE 的 `response.model` 同样为 `gpt-6.1-sol`，HTTP 200 并收到 `response.completed`。未记录正文或凭据。现有传输/调度回归测试 25/25 通过，包括 failover 时请求正文保持一致。

安装脚本保留用户既有模型设置，不再插入 Luna 默认模型。CC Switch 保存的新配置默认模型已按用户选择设置为 `gpt-6.1-sol`。修正时当前选择已回到 OpenAI Official，因此保留其选择及生效配置，只修正新代理配置。调查和验收脚本使用 Luna 不表示用户模型请求会被降级。

此结果证明新配置的模型认证与小会话压缩可用，不代表所有已有 Desktop 会话已经更新 provider，也不代表完整 Remote Control、长会话或跨账户加密压缩验收完成。账户模型列表当时没有列出 `gpt-6.1-sol`，但实际请求成功；列表缺失不能直接判为不支持。
