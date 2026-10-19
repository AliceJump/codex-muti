# 本地配置认证与模型选择验收

2026-10-13 使用官方 Codex CLI `0.157.1` 和正在运行的 localhost 代理验证。配置项在 CC Switch 中名为“Codex Pool（本地代理，保留登录）”，模型 provider ID 为 `codex_pool_local`。

模型访问使用 `auth.command`，由已发布的 Daemon 执行 `model-credential`，通过私有 stdout 管道读取本机 DPAPI 的本地密钥。`requires_openai_auth=false`，不覆盖官方 ChatGPT 登录。浏览器和控制 API 不返回密钥；直接在终端运行凭据命令会被拒绝。

隔离 `CODEX_HOME` 未复制任何官方登录凭据。分别以 `gpt-6-luna` 和 `gpt-6.1-sol` 验证模型回复、官方 Codex 压缩以及压缩后的信息保留，均完成。GPT-6.1 Sol 的 `thread/start` 解析结果为 `gpt-6.1-sol`，代理收到的三次请求模型字段均为同一 ID，HTTP 状态均为 200。`ResponsesRelay` 原样转发请求正文，不替换模型字段。

另一次仅检查协议元数据的请求中，传入模型为 `gpt-6.1-sol`，上游 SSE 的 `response.model` 同样为 `gpt-6.1-sol`，HTTP 200 并收到 `response.completed`。未记录正文或凭据。现有传输/调度回归测试 25/25 通过，包括 failover 时请求正文保持一致。

安装脚本保留用户既有模型设置，不再插入 Luna 默认模型。CC Switch 保存的新配置默认模型已按用户选择设置为 `gpt-6.1-sol`。修正时当前选择已回到 OpenAI Official，因此保留其选择及生效配置，只修正新代理配置。调查和验收脚本使用 Luna 不表示用户模型请求会被降级。

此结果证明新配置的模型认证与小会话压缩可用，不代表所有已有 Desktop 会话已经更新 provider，也不代表完整 Remote Control、长会话或跨账户加密压缩验收完成。账户模型列表当时没有列出 `gpt-6.1-sol`，但实际请求成功；列表缺失不能直接判为不支持。

## 内置 OpenAI 与仅修改地址

修复了只修改 `openai_base_url` 的两项兼容问题：模型入口原先只接受本地密钥，导致官方登录 bearer 返回 401；原生客户端的 zstd 请求体原先返回 415。新版支持只读核对当前官方 file 登录，以及账户池任一原生账户的当前 bearer。原生 WebSocket 探测返回 426，Codex 自行回落到 HTTP/SSE。补齐 `/v1/models` 和原生 compact 请求使用的 beta、turn metadata、window ID 请求头。解压过程只还原原始 JSON 字节，不替换 model、工具结果或上下文；初版解压正文和解码窗口均限制为 32 MiB。

2026-10-18 将入口及解压后的请求体上限统一提高为 128 MiB，两处共用同一常量；解码窗口上限保持 32 MiB。此调整不代表 ChatGPT 原生后端的大请求已通过真实账户验收。

真实本机验证中，Native Account A、Native Account B 和当前官方登录都能通过模型入口认证：发送无效模型正文得到 400，WebSocket 探测得到 426；这些凭据访问 `/status` 均得到 401。当前官方登录请求 `/v1/models` 得到 200。验证前后官方登录文件的 SHA-256 相同，代理未改写登录。入口凭据不决定实际模型账户，sticky 调度继续负责选路。

新增安全/认证/解压回归测试全部通过（17/17），传输和调度回归测试通过（25/25）。测试覆盖两个原生凭据、当前官方文件登录、轮换旧值失效、控制面隔离、原始 JSON 字节保留、损坏压缩流、未知编码、超出解压上限和取消。

**真实内置 provider 的模型回复和远端压缩尚未验收通过。** 使用原有官方登录并显式指定 `openai`、`openai_base_url` 和 `gpt-6.1-sol` 的请求已不再因认证或压缩格式被拒绝，但进入调度时返回 503。A 的周额度已达到 99%，B 的 5h 已达到 100%。用户授权临时把周阈值从 96% 调到 99%；由于 A 已达到临时阈值，仍无可用账户。测试后已恢复自动切换开启、5h 95%、周 96%。未擅自调高到 100%，也未把先前自定义 provider 的压缩结果算作本次内置 provider 验收。

本适配支持默认 file 登录；其他 keyring/ephemeral grant 尚未自动读取。该限制及剩余真实模型/压缩验收不影响已验证的 A/B 模型入口认证，但不能据此宣称所有 Desktop/Remote Control 场景通过。
