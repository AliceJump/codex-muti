# Codex Pool

本地 Codex 多账户模型访问代理。**Codex owns the workflow. The proxy only owns model access.**

## 当前状态（2026-10-14）

| 范围 | 状态 |
|------|------|
| Daemon（`127.0.0.1:3210`）+ localhost Dashboard | 已实现，可本地运行 |
| Windows Floating 悬浮窗 / Widget 托盘面板 | 已实现；完整桌面验收仍待完成 |
| 原生多账户（登录、独立 refresh、真实 5h/weekly 额度、小会话续接与阈值自动切换） | **核心已通过**，**不等于完整发布验收** |
| ChatGPT Desktop 接入 | **仍待验收** |
| macOS 客户端 | **源码原型**，未通过 Mac 编译/真机验收 |
| 生产默认自动切换 | **关闭**（需显式实验开关） |

验收细节见 [docs/native-acceptance-results.md](docs/native-acceptance-results.md)。不要把小会话通过当成完整发布门槛已清。

### 进行中的草稿 PR（尚未合入 `master`）

本仓库当前用**分 PR** 在 GitHub 上审查，与**本地组合检出/部署**是两条线：

| PR | 主题 | 说明 |
|----|------|------|
| [#6](https://github.com/AliceJump/codex-muti/pull/6) | Responses 连字符头透传 + 产品显示名 | 透传 `session-id` / `thread-id`；Floating 显示名为 `Codex Pool` |
| [#7](https://github.com/AliceJump/codex-muti/pull/7) | Desktop WHAM usage 改写 | 按账户池可用性改写 `wham/usage`；`~/.codex/config.toml` 增加 `chatgpt_base_url` |
| [#8](https://github.com/AliceJump/codex-muti/pull/8) | 托盘右键玻璃菜单 | WPF `GlassTrayMenu`，与悬浮窗主题一致 |

- **GitHub**：上述均为 **draft**，互不强制叠合；审查/合并请按各自 PR 进行。
- **本地**：可在同一 worktree 组合 cherry-pick / 合并后再用 `deploy-local.ps1` 做整包验证；本地组合部署**不等于**这些 PR 已合入 `master`。

## 组件与端口

| 组件 | 作用 | 默认地址 / 入口 |
|------|------|-----------------|
| **Daemon** | 模型代理、账户、额度、本地 API、Dashboard 静态页 | `http://127.0.0.1:3210`（仅 loopback） |
| **Dashboard** | 浏览器账户/额度/阈值控制台 | 同端口；`pool.ps1 dashboard` 建立短期安全会话 |
| **Floating** | 可拖动 Acrylic 悬浮窗 | `pool.ps1 window` / `Ctrl+Alt+C` |
| **Widget** | 托盘状态面板（独立进程） | `pool.ps1 widget` / `Ctrl+Alt+W` |

Daemon 本身不依赖 Node。协议基准为官方 Codex CLI `0.157.1`（Node 22 仅用于验证脚本/MCP 夹具）。

## 快速开始

需要 Windows 11、.NET 10 SDK 和官方 Codex CLI。

```powershell
dotnet build CodexPool.slnx
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- serve
```

便捷命令：

```powershell
.\scripts\pool.ps1 start
.\scripts\pool.ps1 dashboard
.\scripts\pool.ps1 window
.\scripts\pool.ps1 widget
```

### 登录账户

每个账户用系统浏览器完成官方 Continue with ChatGPT；不要复制 token，也不要覆盖用户默认的 Codex `auth.json`。

```powershell
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- login "Account A"
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- login "Account B"
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- status
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- models
```

原生 5h/weekly 额度适配器用：

```powershell
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- native-login "Native Account A"
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- native-login "Native Account B"
```

详情见 [docs/native-auth-adapter.md](docs/native-auth-adapter.md)。原有 SIWC 注册会保留；SIWC token 只走公开路线，额度仍显示未知。

首次成功授权成为 active；其余保持独立登录。控制命令：`activate` / `pause` / `resume` / `drain` / `undrain` / `enable` / `disable`（ID 来自 `status`）。暂停/排空/禁用只挡后续请求，已接受的流继续用原账户。

### 可选 CLI wrapper

```powershell
Set-Alias mcodex (Join-Path $PWD 'scripts\mcodex.ps1')
mcodex -m gpt-6-luna
```

Wrapper 只提供 custom provider 配置和本地代理凭证，不接管 session、工具、上下文或 Git。默认连接 `http://127.0.0.1:3210/v1`，关闭 WS。OAuth 凭据只留在 Daemon。

## Codex / Desktop 配置（`~/.codex/config.toml`）

使用内置 OpenAI provider、并希望流量与用量查询都打到本机 Daemon 时，在用户级配置顶层设置：

```toml
openai_base_url = "http://127.0.0.1:3210/v1"
chatgpt_base_url = "http://127.0.0.1:3210/backend-api"
```

| 键 | 作用 |
|----|------|
| `openai_base_url` | 模型请求转到本机池（`master` 已支持相关入口说明） |
| `chatgpt_base_url` | 让 ChatGPT Desktop 的用量查询打到 Daemon；**完整 WHAM 改写见草稿 [PR #7](https://github.com/AliceJump/codex-muti/pull/7)**，说明见 [docs/wham-usage-rewrite.md](docs/wham-usage-rewrite.md) |

可选：`CODEX_API_BASE_URL=http://127.0.0.1:3210`（对应 `GET /api/codex/usage`，语义与 WHAM 改写相同，同样依赖 #7）。

模型入口可接受池内原生 access token，或只读核对 Daemon `CODEX_HOME/auth.json` 中的当前 ChatGPT 登录；代理不导入、复制、刷新或写入该文件。实际上游账户仍由 sticky 调度决定。

原生 WebSocket 探测返回 426，使 Codex 回落 HTTP/SSE。`/v1/models` 返回原生目录；带 `client_version` 时优先原样转发。支持原生 zstd（解压上限 32 MiB）。错误汇总见 [docs/model-errors.md](docs/model-errors.md)；provider 验收见 [docs/local-provider-acceptance.md](docs/local-provider-acceptance.md)。

## 本地组合部署

一键停旧起新（会中断当前模型请求，不是热更新）：

```powershell
pwsh -NoProfile -File .\scripts\deploy-local.ps1 -Dashboard
# 或双击仓库根目录 deploy.cmd
```

只构建并启动、保留已有 Daemon：

```powershell
.\scripts\build-and-start.ps1
.\scripts\build-and-start.ps1 -Dashboard
```

说明：

- 部署脚本只动本仓库（及 worktree）`.local` 下的代理与 UI；不改账户凭据/阈值，不停止 Codex 本身。
- **本地组合部署**：你可以把未合入的草稿改动一并检出后再部署，用于本机验证。
- **GitHub 分 PR**：#6 / #7 / #8 仍是独立 draft，审查时请分开看，不要默认它们已经在 `master`。

账户改名：`pool.ps1 rename <account-id> "新名称"` 或 Dashboard 卡片操作，见 [docs/account-rename.md](docs/account-rename.md)。

## 桌面端：Floating、Widget、Mac

### Windows Floating / Widget

- Floating：Acrylic、完整/紧凑/胶囊、主题、置顶、拖动、位置保存；`×` / Alt+F4 只关该窗，Daemon 继续跑。
- Widget：托盘面板，展示账户与额度，可打开悬浮窗/Web；与 Floating 可同时运行，经 `/events` 同步。
- 快捷方式/`open-floating.ps1` 在代理未运行时可自动拉起 Daemon（可带 `--live-boundary-probe`）；完整真实验收仍以报告为准。
- 托盘右键玻璃菜单（与悬浮窗同主题）在草稿 [PR #8](https://github.com/AliceJump/codex-muti/pull/8)；合入前 `master` 行为以已发布代码为准。

详见 [docs/desktop-widget.md](docs/desktop-widget.md)。

### macOS 原型

源码在 `src/CodexPool.Widget.Mac/`，构建脚本 `scripts/build-widget-mac.sh`。**尚未** Mac 编译/真机验收；Daemon 仍依赖 Windows DPAPI，Mac Keychain 适配未做。Windows 不能直接跑原生 `.app`。

## 本地状态与安全（摘要）

- 数据：`%LOCALAPPDATA%\CodexPool`，DPAPI CurrentUser；无明文凭据回退。
- 单 Daemon 独占锁；刷新按注册单飞，成功后原子替换凭据。
- 默认仅 loopback；Host/Origin、HttpOnly 会话与 CSRF 保护控制 API。
- `/health` 公开；`/status`、`/accounts`、`/usage`、`/models` 与控制/模型请求需本地鉴权。
- 原生 `/usage` 按真实窗口时长展示；SIWC 为未知。Token 统计见 [docs/usage-statistics.md](docs/usage-statistics.md)。

## 验证

```powershell
dotnet run --project tests/CodexPool.Tests
dotnet run --project tests/CodexPool.SecurityTests
node scripts/phase0-smoke.mjs
```

SecurityTests 会占用 `3210`，请先停正在跑的 Daemon。产物只写忽略目录 `.research/`。

真实双账户实验需显式测试模式与两次登录：

```powershell
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- serve --live-boundary-probe
node scripts/phase0-live.mjs
```

部署请用普通 `serve`，不要长期开实验开关。请求一旦开始交付正文即不可跨账户重放。

## 打包

```powershell
.\scripts\build-release.ps1
.\scripts\build-release.ps1 -SelfContained
```

产物在 `.local\release`。仍需官方 Codex CLI。

## 文档索引

- [architecture.md](architecture.md) — 责任边界与协议安全
- [implementation-plan.md](implementation-plan.md) — 分阶段门槛
- [docs/phase0-results.md](docs/phase0-results.md) — Phase 0 已通过 / 待验证
- [docs/native-acceptance-results.md](docs/native-acceptance-results.md) — 原生多账户验收范围
- [docs/native-auth-adapter.md](docs/native-auth-adapter.md) — 原生登录与额度适配
- [docs/wham-usage-rewrite.md](docs/wham-usage-rewrite.md) — Desktop WHAM 改写（对应草稿 PR #7）
- [docs/desktop-widget.md](docs/desktop-widget.md) — Floating / Widget / Mac
- [docs/model-errors.md](docs/model-errors.md) — 错误与账户池汇总
- [docs/local-provider-acceptance.md](docs/local-provider-acceptance.md) — 本地 provider 验收
