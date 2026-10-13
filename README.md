# Codex Pool

本地 Codex 多账户模型访问代理。**Codex owns the workflow. The proxy only owns model access.**

核心代码、localhost Dashboard 和 Windows 悬浮窗已实现，当前处于验收阶段。两个原生账户已通过登录、独立 refresh-token rotation、真实 5h/weekly 额度、小会话续接/Compact/Resume/MCP，以及真实额度驱动的阈值自动切换。默认关闭自动切换，不能把小会话通过等同于完整验收。范围见 [docs/native-acceptance-results.md](docs/native-acceptance-results.md)。

## 运行

需要 Windows 11、.NET 10 SDK 和官方 Codex CLI。协议基准为 CLI `0.157.1`。Node 22 仅用于验证脚本/MCP 测试工具，Daemon 本身不依赖 Node。

```powershell
dotnet build CodexPool.slnx
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- serve
```

便捷启动和界面：

```powershell
.\scripts\pool.ps1 start
.\scripts\pool.ps1 dashboard
.\scripts\pool.ps1 window
.\scripts\pool.ps1 widget
```

Dashboard 位于 `http://127.0.0.1:3210`，`dashboard` 命令建立短期安全浏览器会话。页面提供账户 Radio、额度、pause/drain/enable、阈值、请求与切换原因；悬浮窗通过 `/events` 同步状态，通过本地 API 切换账户。窗口支持 Acrylic、完整/紧凑/胶囊模式、系统/浅色/深色、置顶、拖动、托盘、`Ctrl+Alt+C`、位置保存、DPI 和工作区钳制。独立悬浮窗的 × 或 Alt+F4 只关闭该 UI；托盘状态面板由独立进程提供，隐藏后可从图标唤醒，Daemon 始终继续运行。

桌面悬浮窗快捷方式（`scripts/open-floating.ps1`）和悬浮窗本身会在代理未运行时自动启动代理，并携带 `--live-boundary-probe`，允许网页开启自动切换。自动切换开关及阈值由 Dashboard 保存，后续启动沿用设置；首次安装仍默认关闭。已有代理会继续运行，启动器不会中断其请求或修改启动模式。此启动方式用于当前用户的迁移实验，完整真实账户验收仍以验收报告为准。

另一个终端中登录两个账户。每次由系统浏览器完成官方 Continue with ChatGPT；不要复制 token 或覆盖 Codex auth.json。

```powershell
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- login "Account A"
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- login "Account B"
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- status
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- models
```

要使用 Codex 原生 5h/weekly 额度适配器，分别执行 `native-login "Native Account A"` 和 `native-login "Native Account B"`。它启动官方登录的独立临时 profile，将新 grant 一次性交给 DPAPI，删除 helper 凭据后只有 Daemon 刷新。详情见 [docs/native-auth-adapter.md](docs/native-auth-adapter.md)。原有 SIWC 注册会保留；SIWC token 只访问公开路线，额度仍显示未知。

首次成功授权成为 active，其余账户保持独立登录。相同 client 注册的重新授权保留本地 ID；不同注册即使显示标签相同也不混用 token。`distinctUsers` 用于检查测试是否确有不同 ChatGPT 用户，不公开真实 subject。

账户控制命令为 `activate <id>`、`pause <id>`、`resume <id>`、`drain <id>`、`undrain <id>`、`enable <id>`、`disable <id>`，ID 来自 `status`。控制状态持久保存。暂停/排空/禁用仅阻止后续请求，已接受的 streaming 请求继续使用原账户；状态中单独显示各账户正在处理的请求数量。被排除账户不能激活。Sticky 调度只在 active 达到条件时重新选择，恢复的 standby 不抢回 active；手动选择推进 epoch，旧请求不能覆盖它。自动关闭且 active 不可用时返回明确错误。

使用可选的单一 wrapper：

```powershell
Set-Alias mcodex (Join-Path $PWD 'scripts\mcodex.ps1')
mcodex -m gpt-6-luna
```

Wrapper 只为官方可执行文件提供 custom provider 配置和本地代理凭证，继承标准输入/输出，不处理 session、工具、上下文或 Git。默认仅连接 `http://127.0.0.1:3210/v1`，关闭 WS。所有上游 OAuth 凭据留在 Daemon。

使用内置 OpenAI provider 和现有 ChatGPT 登录时，也可在用户级 `~/.codex/config.toml` 的顶层设置：

```toml
openai_base_url = "http://127.0.0.1:3210/v1"
```

模型入口接受账户池中任一原生登录的当前 access token，或只读核对 Daemon 的 `CODEX_HOME/auth.json` 中当前 ChatGPT 登录。现有文件由官方 Codex 维护，代理不导入、复制、刷新或写入该登录。请求入口认证和账户选择独立；实际上游账户仍由 sticky 调度决定。此只读适配目前针对官方 file 存储；keyring/ephemeral 中的其他 grant 没有自动读取支持。

原生客户端的 WebSocket 探测返回 426，让 Codex 回落到 HTTP/SSE；`/v1/models` 返回原生模型目录。Codex 请求 `/v1/models?client_version=...` 时，Daemon 优先转发该查询参数到上游 `backend-api/codex/models`，使选择器目录与当前 CLI 一致（例如 `gpt-6.1-sol`）；无查询参数时回退到启动时解析的本机 `codex --version`，也可通过 `CODEX_POOL_CLIENT_VERSION=0.162.1` 手动指定，失败则回退到已验证的 `0.157.1`。升级 CLI 后建议重启 Daemon。支持原生请求的 zstd 压缩，解压正文和解码窗口均限制为 32 MiB，解压后原样转发 JSON 字节。官方/native 登录凭据仅能调用这些模型接口，控制 API 继续使用本地密钥或 Dashboard 安全会话。模型调用仍受账户额度和切换阈值约束，两个账户均不可用时返回 503。验收范围见 [docs/local-provider-acceptance.md](docs/local-provider-acceptance.md)。

## 桌面小组件

手动构建并启动新版本（构建到独立目录，已有代理时保留原进程）：

```powershell
.\scripts\build-and-start.ps1
# 也打开 Web：
.\scripts\build-and-start.ps1 -Dashboard
```

脚本不会停止或替换当前代理。要让新代理修复生效，请先结束请求、自己退出旧代理，再执行上述命令；旧托盘界面从菜单“退出托盘面板”退出即可。

Windows 提供右下角托盘状态面板，单击 Codex Pool 图标展开当前账户、额度、自动切换状态，以及“打开悬浮窗 / 打开 Web”入口；点外部自动收起。Web 服务默认随代理启用。运行 `scripts/build-widget.ps1`、`scripts/install-widget-shortcut.ps1` 后，可双击桌面的“Codex Pool 小组件”启动托盘入口；快捷键为 `Ctrl+Alt+W`。它与原浮窗独立运行，退出 UI 后代理继续运行。

macOS SwiftUI 客户端原型和构建脚本已提供，当前尚未通过 Mac 编译/真机验收，Mac Daemon 的 Keychain 存储适配仍待实现。Windows 无法直接运行原生 `.app`。使用方式与准确验收范围见 [docs/desktop-widget.md](docs/desktop-widget.md)。

## 本地状态和凭据

- 数据位于 `%LOCALAPPDATA%\CodexPool`，Windows DPAPI CurrentUser 加密并限制目录 ACL；没有明文凭据回退。
- 一个 Daemon 持有跨进程独占锁；各注册单独刷新锁，成功后原子替换整组凭据。
- 本地 API 使用独立访问凭证，CLI 从本机加密存储读取；不把 OAuth token 返回 Web API。
- 不记录完整 prompt、工具参数/结果、token、cookies 或 OAuth callback query。最近请求只保留 100 条元数据。
- 默认只监听 loopback，不提供 LAN 开关。精确 Host/Origin 检查、HttpOnly/SameSite 浏览器会话及 CSRF 校验保护控制 API；浏览器 cookie 不能调用模型或取得本地模型 key。
- `/health` 为最小公开状态；`/status`、`/accounts`、`/usage`、`/models` 和控制/模型请求需要本地鉴权。`/auth/callback` 使用一次性 state、nonce、PKCE 和 ID token 校验。

`/usage` 对 SIWC 明确返回未知。原生注册使用固定官方源码中的 usage endpoint，按实际窗口时长识别 5h/weekly，保存每账户独立快照并每 30 秒刷新；过期/缺失数据不伪装为 0%。阈值决策只使用新鲜且未过 reset 的窗口。两个原生账户的真实接口已通过；它是版本敏感的私有 Codex 接口。

## 验证

```powershell
dotnet run --project tests/CodexPool.Tests
dotnet run --project tests/CodexPool.SecurityTests
node scripts/phase0-smoke.mjs
```

SecurityTests 会在端口 3210 启动隔离测试服务，因此应先停止正在运行的 Daemon。所有生成的资料、隔离 Codex home、计数器和实验输出只写主仓库忽略目录 `.research/`。

协议测试覆盖明确 quota/auth 拒绝前切换、普通 429/5xx/网络错误不重试、首事件后流中断不重放、取消、错误体不泄漏、未知字段/工具结果原样保留、Responses Lite 请求头、缺失 Content-Type 的大 SSE 首事件。

官方 CLI mock 实验覆盖响应、首事件前 A->B、工具拒绝结果回传、MCP 操作一次执行、Resume、Compact、首事件后断连不调用 B。Shell 是否执行由官方策略决定；若被拒绝，报告显式标记 pending，不把拒绝误算为执行通过。

真实双账户实验需要显式启动测试模式并完成两个登录：

```powershell
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- serve --live-boundary-probe
node scripts/phase0-live.mjs
```

测试模式允许手动迁移，并允许在 Dashboard 显式开启自动调度；默认仍为 OFF。脚本使用 GPT-6 Luna、隔离官方 Codex home、受控事实及不可预测的 MCP 结果证明；不会访问用户原有工作树。它区分可读上下文、加密 reasoning 和 local compact；不宣称 remote encrypted compaction、全部长会话或 Desktop 都已通过。原生与 SIWC grant 不作为同次透明 failover 的混合候选。

部署使用普通 `serve`，不要长期保留实验开关。请求一旦开始交付任何正文即锁死重放门；生命周期/工具事件也是正文。错误类型不明确、网络故障或客户端取消不会透明切号。Proxy 不执行也不去重工具，最终工具副作用由官方 Codex 及测试计数器验收。

## 打包

```powershell
.\scripts\build-release.ps1
# 无需目标机器安装 .NET runtime 时：
.\scripts\build-release.ps1 -SelfContained
```

产物位于忽略目录 `.local\release`，Daemon/Floating 是两个独立进程。普通包需要 .NET 10 Desktop 和 ASP.NET Core runtime；self-contained 包包含运行时。仍需要官方 Codex CLI。源码验收入口不需要安装或修改默认 Codex 配置。

## 文档

- [architecture.md](architecture.md)：责任边界、并发/刷新/协议安全与证据。
- [implementation-plan.md](implementation-plan.md)：分阶段提交和真实验收门槛。
- [docs/phase0-results.md](docs/phase0-results.md)：已通过与仍待验证项目。
- [docs/native-acceptance-results.md](docs/native-acceptance-results.md)：原生账户、真实额度、轮换与自动切换验收范围。
