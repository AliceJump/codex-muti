# Codex Pool

本地 Codex 多账户模型访问代理。**Codex owns the workflow. The proxy only owns model access.**

目前处于 Phase 0：可运行的官方 OAuth 登录、DPAPI 存储、单账户 Responses/SSE 转发、显式手动迁移实验和协议验证。自动调度、真实 5h/weekly 额度、Dashboard 和 Windows 悬浮窗尚未实现。生产跨账户切换保持关闭，不能把小会话通过等同于全部验收完成。

## 运行

需要 Windows 11、.NET 10 SDK 和官方 Codex CLI。协议基准为 CLI `0.157.1`。Node 22 仅用于验证脚本/MCP 测试工具，Daemon 本身不依赖 Node。

```powershell
dotnet build CodexPool.slnx
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- serve
```

另一个终端中登录两个账户。每次由系统浏览器完成官方 Continue with ChatGPT；不要复制 token 或覆盖 Codex auth.json。

```powershell
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- login "Account A"
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- login "Account B"
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- status
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- models
```

首次成功授权成为 active，其余账户保持独立登录。相同 client 注册的重新授权保留本地 ID；不同注册即使显示标签相同也不混用 token。`distinctUsers` 用于检查测试是否确有不同 ChatGPT 用户，不公开真实 subject。

账户控制命令为 `activate <id>`、`pause <id>`、`resume <id>`、`drain <id>`、`undrain <id>`、`enable <id>`、`disable <id>`，ID 来自 `status`。控制状态持久保存。暂停/排空/禁用仅阻止后续请求，已接受的 streaming 请求继续使用原账户；状态中单独显示各账户正在处理的请求数量。被排除账户不能激活。尚未开放自动调度，因此 active 不可用时返回明确错误，由用户手动选择 eligible 账户。

使用可选的单一 wrapper：

```powershell
Set-Alias mcodex (Join-Path $PWD 'scripts\mcodex.ps1')
mcodex -m gpt-6-luna
```

Wrapper 只为官方可执行文件提供 custom provider 配置和本地代理凭证，继承标准输入/输出，不处理 session、工具、上下文或 Git。默认仅连接 `http://127.0.0.1:3210/v1`，关闭 WS。所有上游 OAuth 凭据留在 Daemon。

## 本地状态和凭据

- 数据位于 `%LOCALAPPDATA%\CodexPool`，Windows DPAPI CurrentUser 加密并限制目录 ACL；没有明文凭据回退。
- 一个 Daemon 持有跨进程独占锁；各注册单独刷新锁，成功后原子替换整组凭据。
- 本地 API 使用独立访问凭证，CLI 从本机加密存储读取；不把 OAuth token 返回 Web API。
- 不记录完整 prompt、工具参数/结果、token、cookies 或 OAuth callback query。最近请求只保留 100 条元数据。
- 默认只监听 loopback；LAN、浏览器控制会话和 Dashboard 不在本阶段。
- `/health` 为最小公开状态；`/status`、`/accounts`、`/usage`、`/models` 和控制/模型请求需要本地鉴权。`/auth/callback` 使用一次性 state、nonce、PKCE 和 ID token 校验。

`/usage` 明确返回未知。官方第三方 OAuth 文档公开了 model list/Responses 路线，没有确认可读的 Codex 5h/weekly 额度接口；不会猜测额度，也不会拿这条授权访问文档排除的 ChatGPT backend-api。后续额度方案必须单独定案。

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

测试模式仅允许手动迁移，不开启自动调度。脚本使用 GPT-6 Luna、隔离官方 Codex home、受控测试事实和 MCP 计数器；不会访问用户原有工作树。它区分可读上下文、加密 reasoning 和 local compact；不宣称 remote encrypted compaction、全部长会话或 Desktop 都已通过。

部署使用普通 `serve`，不要长期保留实验开关。请求一旦开始交付任何正文即锁死重放门；生命周期/工具事件也是正文。错误类型不明确、网络故障或客户端取消不会透明切号。Proxy 不执行也不去重工具，最终工具副作用由官方 Codex 及测试计数器验收。

## 文档

- [architecture.md](architecture.md)：责任边界、并发/刷新/协议安全与证据。
- [implementation-plan.md](implementation-plan.md)：分阶段提交和真实验收门槛。
- [docs/phase0-results.md](docs/phase0-results.md)：已通过与仍待验证项目。
