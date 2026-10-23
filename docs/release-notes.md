# Codex Pool 0.1.4

Windows x64 自包含便携版：完整解压后双击 CodexPool.exe，无需另装 .NET 或 PowerShell 7。

- 完整 Windows 账户浮窗、Dashboard 及 macOS Widget 源码原型新增订阅截止时间显示；日期、时间与时区偏移按查看设备的本地时区格式化。
- 截止时间仅从经登录验证后保存的 ID token 中读取 `chatgpt_subscription_active_until`，并在令牌刷新后自动更新；不存在或无效时显示“订阅截止时间未知”，不把 access token 到期时间误认为订阅截止时间。
- 完整 Windows 账户卡片调整布局，给截止日期预留空间；保留原有额度、账户切换和代理行为。

验证：PR #23 的 GitHub Actions Windows 构建、核心/安全测试、客户端检查、Dashboard 测试、自包含打包和 ZIP 解压离线验证均通过。另有订阅元数据、刷新、时区和缺失数据的定向回归。正式版发布必须再由推送 `v0.1.4` 标签触发 Actions 构建与校验，成功后才会公开 Release。

本包不包含官方 Codex CLI／Desktop；原生登录需要可执行的官方 `codex` 命令。旧配置中的本地 `chatgpt_base_url` 应移除，继续使用 `openai_base_url` 接入模型代理。

已知限制：发行包未签名；Mac Widget 仅为源码原型，未进行原生 Mac 编译或真机验收；完整跨账户长会话与所有真实桌面场景仍未完成验收。订阅日期依赖上游可用 ID token 元数据，不提供则显示未知。升级前请正常关闭代理及两个客户端，再完整替换程序文件；用户数据仍保存在 LocalAppData。
