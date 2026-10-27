# 机器人与应用模型接口

Codex Pool 提供本机 OpenAI 兼容模型入口。机器人程序负责 QQ 消息、上下文、权限、记忆与工具执行；代理负责模型访问、账户和额度。

典型组合：`QQ ⇄ SnowLuma / NapCat ⇄ AstrBot 等机器人框架 ⇄ Codex Pool`。

## 接入

1. 启动包含本功能的新版本代理，直接访问 `http://127.0.0.1:3210/` 打开完整控制台。
2. 在「机器人与应用接入」填写应用名称，创建并复制模型密钥。密钥只显示一次；丢失后撤销并重新创建。
3. 机器人框架选择 OpenAI 兼容服务，填写：

| 配置 | 值 |
| --- | --- |
| Base URL | `http://127.0.0.1:3210/api/v1` |
| API Key | 控制台创建的 `cpk_…` 模型密钥 |
| 聊天模型 | 从模型目录选择实际 ID，例如账户可用的 `gpt-6.1-sol` |

同一原生 Windows 主机上的后端程序可以连接。容器、WSL 和其他设备中的 `127.0.0.1` 不代表这台主机，本版本不支持这些网络部署；不要直接向外暴露账户管理服务。

SnowLuma / NapCat 的 OneBot 地址及 token 配置在机器人框架的消息适配器里，与上表的模型地址和密钥分别设置。

## 接口与兼容范围

| 路由 | 行为 |
| --- | --- |
| `GET /api/v1/models` | OpenAI `object: list, data[]` 模型目录，仅列出上游可见聊天模型 |
| `POST /api/v1/chat/completions` | 文本多轮、普通 JSON、SSE、函数工具调用及结果回传、用户图片输入 |
| `POST /api/v1/responses` | input 数组或字符串，普通 JSON / SSE；上游统一使用 stream=true、store=false |
| `POST /api/v1/images/generations` | 单张图片生成，返回 `data[0].b64_json`，仅原生账户路线 |

使用模型专用密钥时，上述路由也可通过 `/v1` 前缀调用。推荐统一使用 `/api/v1`，避免混用 Codex 的目录/传输契约。原有 Codex 客户端的 `/v1/models` 原始目录及 `/v1/responses` 严格请求要求保持不变。

聊天支持 `messages` 中的 user、assistant、system、developer、tool；system 转为 developer。函数工具声明、tool_choice、并行调用参数转换到 Responses，代理不执行函数。用户图片支持 HTTPS URL 或 data:image URL，代理不主动下载这些 URL；实际识图能力取决于上游模型。

可选字段包括 `reasoning_effort`、`response_format`（text/json_object/json_schema）和 `stream_options.include_usage`。它们是否被上游接受取决于账户/模型；缺少 usage 时不伪造 Token 数。

首版明确拒绝尚无等价支持的非空参数，例如 temperature、top_p、max_tokens/max_completion_tokens、stop、logprobs、n>1、音频输出等。会返回 HTTP 400 与固定 error.code；**机器人配置中应移除这些字段，或选择允许自定义请求参数的 provider**。本版本不能声称所有 OpenAI-compatible 插件无需调整即可使用。对于 temperature 等参数强制发送且无法关闭的客户端，仍需专门适配。

Responses 入口不提供服务器会话存储。每次发送所需历史；previous_response_id、conversation 等非空字段会被拒绝。未知或畸形参数也会明确失败，不静默删除后声称完全支持。

## 图片生成

配置图片模型 `gpt-image-2`，请求示例：

```json
{
  "model": "gpt-image-2",
  "prompt": "A blue circle on a white background.",
  "quality": "low",
  "n": 1,
  "response_format": "b64_json"
}
```

代理内部使用已验证的 `gpt-6.1-sol` 主模型和 `image_generation` 工具。因此账户必须可用这两个模型，并处于原生路线；SIWC 路线返回 `image_requires_native_account`。模型目录不把图片模型列为聊天模型，图片模型需手工填写。

quality 接受 auto/low/medium/high，由上游处理。size 仅接受省略或 auto；实测指定尺寸未被严格遵从，因此暂不提供自定义尺寸保证。只支持 n=1、非流式、base64 图片；不提供图片编辑、图片 URL 托管、视频、音频或 embeddings。

图片须在收到真实完成事件后交付；中途失败不返回暂存的半成品。转换层有 48 MiB 上游响应上限，超限失败且不重放。图片通用 Token 统计不等于完整媒体费用账单。

## 密钥与错误

- 模型密钥仅允许上述模型路由，不能读取状态、账户、用量或操作控制接口。
- 服务只保存模型密钥的 SHA-256 摘要，并随原有保险库存储使用 Windows DPAPI 保护；列表不返回密钥。
- 创建/撤销走已鉴权管理 API，浏览器写入仍需 CSRF。密钥创建响应仅用于一次性显示，不能写到日志。
- 撤销阻止后续请求，已经接受的请求继续处理。
- 流式失败返回错误或断开，不伪造正常结束标记；网络不明/取消不会授权跨账户重放。
- 账户额度、刷新、sticky 调度及生产自动切换门槛沿用原实现；不按 QQ 用户维护账户或会话。

## 当前验证

已通过转换层测试、既有核心测试、不占生产端口的安全测试和隔离 HTTP 检查。通过隔离的新转换层转发到已有代理，真实上游完成了普通/流式聊天、函数调用和客户端提供结果的往返，以及标准图片响应转换；该验证没有替换正在运行的正式代理。

本轮结果（2026-10-24）：整套 solution 构建零警告/错误；核心测试 35/35，安全测试 44/44（跳过固定占用 3210 的 Local API case），隔离 HTTP 检查 23/23。新增 Local API 断言已保留在测试源码，需在端口空闲环境运行完整安全测试。隔离主机复用生产源码，仅替换端口与测试传输，不能替代正式部署验收。

AstrBot/LangBot 实际应用配置、QQ 端到端消息、不同账户与模型的全面能力、完整桌面视觉和网络部署仍需独立验收。合成 fixture 不代表真实多账户续接、刷新或发布验收完成。
