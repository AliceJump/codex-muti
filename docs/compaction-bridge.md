# 按额度预警的真实压缩桥接（实验功能）

Daemon 只提供提前预警和有期限的账户预留；真正的 `thread/compact/start` 由持有官方 App Server 客户端管道的桥接层发起。功能默认关闭，不伪造 SSE、压缩失败或上下文状态，也不执行模型工具。

Daemon 必须同时以 `--live-boundary-probe --experimental-compact-bridge` 启动，并显式开启原有自动切换设置。普通 `serve` 不开放实验能力。已有账户迁移门禁保持不变。

预警沿用现有额度切换阈值，提前五个百分点；只使用新鲜、未耗尽且对当前模型适用的额度，并要求存在可用的同通道备用账户。百分比不是单次压缩的预算保证。

控制端点全部只接受 Daemon 的本地 bearer，官方模型凭证及 Dashboard 浏览器会话不能使用：

| 端点 | 行为 |
|---|---|
| `GET /compaction/advice?model=…` | 返回实验能力及当前账户/选择代次的预警，不改变选择 |
| `POST /compaction/reserve` | 对预警中的账户、代次、模型做条件预留，最长三分钟 |
| `POST /compaction/:id/finish` | 接受 `completed` / `failed` / `unknown`，仅确定完成可条件切换 |

预留期间的新模型请求固定使用原账户且不跨账户重试。软性阈值不会提前改变预留目标，真实额度耗尽、认证失败、暂停/排空/禁用仍有效。手动激活推进选择代次并取消预留，旧完成报告不能覆盖新选择。预留过期自动失效；同一选择代次最多尝试一次，失败或结果未知不自动重发压缩。

账户选择仍是全局 sticky。压缩一个线程不会证明所有其他线程已压缩，因此这是可选的提前优化，不是跨账户正确性的前置保证。

桥接是 `CodexPool.Bridge.exe`，由 Desktop 用作 CLI 包装器。普通命令原样委派给官方 CLI；实验模式只处理唯一 stdio 客户端管道。它会等待已观察到的所有线程空闲、无待处理请求，再用只读 `thread/goal/get` 确认目标没有自主 Goal，随后申请账户预留并调用正式压缩。Goal、子代理和无法确认状态的线程会使自动压缩保持保守；带独立 shell command 的线程也不自动处理。

桥接期间暂缓新的 `thread/*`、`turn/start`、`turn/steer` 和 review 请求；审批回复与中断可以继续通过。真实 `turn/*` 和 `contextCompaction` 事件原样交给 Desktop。压缩须同时满足 RPC 成功、匹配 item 完成、匹配 turn 状态为 `completed`；仅 ACK 或仅 item 完成不足以切换。压缩失败停止本连接的后续自动尝试；超时、断线、协议超限或结果未知会结束桥接及其拥有的官方子进程，并且不转发尚未交给 Codex 的输入，不自动重放。

没有额外旁路 App Server，也不复制线程历史。桥接强制 stdio，禁止显式 remote control，并设置 `CODEX_INTERNAL_APP_SERVER_REMOTE_CONTROL_DISABLED=1` 来关闭持久远程控制。当前仅支持核对过源码的官方 CLI `0.162.1`；其他版本拒绝实验桥接。模型 provider 配置、CODEX_HOME 和 OAuth 刷新仍沿用原配置，没有修改或轮换官方 `auth.json`。

构建并检查启动入口：

```powershell
pwsh -NoProfile -File scripts/build-compact-bridge.ps1
pwsh -NoProfile -File scripts/desktop-compact-bridge.ps1 -CheckOnly
```

桥接单独构建到 `.local/compact-bridge/builds/` 的新目录，更新指针仅影响未来启动，不替换运行中二进制。启用需要新版 Daemon 在实验模式下运行，并通过原有设置开启自动切换：

```powershell
# 应在没有进行中的代理请求时切换守护进程；本功能不会自行部署或停止旧进程。
dotnet run --project src/CodexPool.Daemon --no-launch-profile -- serve --live-boundary-probe --experimental-compact-bridge
# 另一终端中，显式选择原有自动切换策略（示例阈值为95）。
.\scripts\pool.ps1 settings on 95
```

结束当前工作并退出 Desktop 后，用以下入口启动新实例：

```powershell
pwsh -NoProfile -File scripts/desktop-compact-bridge.ps1
```

脚本通过当前进程的环境设置 `CODEX_CLI_PATH` / `CODEX_POOL_UPSTREAM_CLI` / `CODEX_POOL_COMPACT_BRIDGE=1`；不修改注册表、用户配置或已有进程。路径可通过 `-CodexPath`、`-DesktopPath`、`-BridgePath` 明确指定。当前 Desktop 包 `26.1007.2314.0` 的静态代码已确认支持 `CODEX_CLI_PATH`；这是安装版本的实现证据，不是公开稳定接口承诺。启动检查只确认入口和官方 CLI 版本，不证明 Desktop 的真实压缩行为。

回滚：退出使用桥接的 Desktop 后，从原来的快捷方式正常启动；Daemon 重新以普通 `serve` 启动即可禁用实验能力。脚本不会自动完成这两个进程切换。

验证命令：

```powershell
dotnet run --project tests/CodexPool.Tests
dotnet run --project tests/CodexPool.BridgeTests
dotnet run --project tests/CodexPool.SecurityTests -- --skip 'Local API'
$taskCli = (Get-Command codex.exe -CommandType Application | Select-Object -First 1).Source
node scripts/compact-bridge-probe.mjs $taskCli src/CodexPool.Bridge/bin/Debug/net10.0-windows/CodexPool.Bridge.exe
```

Core、桥接及账户安全测试使用合成数据。新增控制 API 测试在动态 loopback 端口启动真实路由，验证鉴权、输入校验、重复申请和条件切换；不占用用户的 3210。旧版整套 `Local API` 测试需要独占 3210，只有用户守护进程已退出时才能单独运行，测试不会停止用户进程。

官方协议探针使用隔离配置，只发送 initialize 与 loaded-thread 列表查询，不创建线程、不压缩、不调用模型；原始协议与 stderr 不落盘，仅把通过状态存到主仓库忽略的 `.research/`。这不能替代真实 Desktop/A/B 验收。真实 UI 文案、长会话、远端加密压缩迁移和完整故障矩阵仍是未通过门禁；本功能保持实验状态。

2026-10-14 验证记录：solution 构建无错误/警告；Core 37 项、Bridge 11 项、Security 38 项通过（包含动态端口的新控制 API 测试，跳过需独占 3210 的旧 `Local API` 测试）；官方 `0.162.1` 隔离协议握手及 Desktop 启动入口检查通过。没有启动桥接 Desktop、改变运行中服务或进行真实压缩模型调用。
