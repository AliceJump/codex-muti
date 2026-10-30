# Windows 快速设置接入

Codex Pool 通过独立 `quick-settings` 注册进程接入 [Windows Quick Settings Extensions](https://github.com/AliceJump/windows-quick-settings-extensions)。当前为限版本的实验功能，实际 Windows 组件和宿主安装入口由该项目管理。

用户界面按 Wi-Fi 二级页组织：左上返回箭头、同一行标题及右上代理启停开关，下方账户横条显示名称和缓存额度。主开关控制本地 Daemon 的启动/安全停止，与自动账户切换设置分离。点击可用账户整条行提交激活，成功读回后才改变当前账户标记；激活只影响新请求。

关闭代理会断开经它访问的模型连接，正在进行的请求/授权会触发繁忙拒绝。不要为了验证磁贴自动关闭承载当前开发会话的代理。注册进程独立于 Daemon，停止服务后仍在线，供用户再次开启；未知状态不标为已关闭，也不自动重放启停或切换请求。

独立进程命令（绝对路径由安装脚本传入）：

```powershell
CodexPool.Daemon.exe quick-settings "C:\path\qse-broker.exe" "C:\path\service\CodexPool.Daemon.exe"
```

注册 helper 可在独立发布目录运行，不需要替换现用服务。它校验监听进程的安装路径、用户和会话，认证访问本机管理 API；本地管理密钥不进入 QSE Broker、Shell 或命令参数。状态只投影账户公开字段和缓存额度，不将完整请求历史发送给 Shell。

当前使用 QSE 协议 v2：应用携带 `qse-manifest.json`，通过通用 install 绑定提供方路径；账户列表由标准 `list` 组件描述，底部操作由 `footer` 的标准 `button` 描述。框架中没有账户或代理分支。进程创建 providerEpoch，请求匹配 Broker/提供方 epoch 和 revision；确认结果的 ACK 丢失可补送同一结果及同一状态，绝不重做业务动作。

`contextMenu` 的第一项为“打开网页管理”，动作仍由本应用提供方执行，使用与托盘/命令行一致的固定地址 `http://127.0.0.1:3210/`，没有令牌或密钥参数。代理未运行或状态未知时禁用该项，不隐式启动代理、不切换账户。此入口目前仍依赖注册提供方在线，按需激活属于后续生命周期改造。

`tile.detailLabel` 为“管理账户”，用于磁贴右侧二级入口的原生悬停提示和无障碍名称。右键菜单继续保留系统“编辑快速设置”入口，编辑行为由框架适配器进入原生模式，与提供方业务操作分离。

实验宿主部署入口已迁入本仓库：

```powershell
.\scripts\Deploy-QuickSettings.ps1 -Force
# 只管理本插件，实际代理始终保留：
.\scripts\Stop-QuickSettings.ps1
.\scripts\Start-QuickSettings.ps1
# 显式撤销插件目录注册：
.\scripts\Uninstall-QuickSettings.ps1
```

2026-10-30 起，这些脚本只管理 Codex Pool 的注册 helper 与目录绑定，不再部署、启动或停止共享 QSE。先在 QSE 仓库单独安装/启动框架；必须支持 `plugin-status/provider-enable/provider-disable/install-cas/uninstall`。`-RuntimeRoot` 默认 `C:\QSE-Host`；旧 `-FrameworkRoot` 参数仅为调用兼容而保留，不触发框架操作。

Deploy 在插件锁内核对旧绑定和无 pending 状态，停用并停止专用 helper，再比较旧路径安装新版本；保留用户固定选择和停用状态。新 helper 未能注册时恢复旧绑定、部署记录及先前在线的 helper。`-ProviderPackage` 可指定已发布且带有 `qse-manifest.json` 的 helper 目录，否则使用 dotnet publish。实际代理的监听 PID 被排除，不会被停止。

Stop 保留注册和固定选择，持久化停用并撤销旧提供方会话；旧实例无法重新注册。Start 要求插件已安装且路径匹配，只启用并拉起自己的 helper，卸载后不会静默重装。Uninstall 停用/停止 helper、撤销目录注册并删除本插件部署记录，重复执行幂等；保留旧发布包供恢复，返回 `await-shell-snapshot` 表示尚未确认原生 Shell 移除。以上流程均不停止实际代理或其他插件；有业务操作待确认时拒绝停止、更新和卸载。

按需启动、统一固定/取消固定/原生移除确认 API、安装器断电恢复仍未实现。框架自身升级仍可能等待 Shell 恢复执行；插件日常升级已不再要求卸载共享适配器。

`proxy-service-intent.json` 仅保存已确认的服务运行意图，供新版本桌面启动器避免在用户关闭后再次隐式启动。启动继承此前认证服务的启动模式，不为使开关可用而新增迁移门禁绕过。账户切换仍受现有 Daemon 的资格、额度和生产门禁校验；失败保留原账户。

额度使用本机 `status.usage` 的真实缓存窗口，未知显示未知，过期值标记缓存。底部“刷新状态”只重读本机状态，不等于上游额度刷新。自动切换设置仍由既有控制台管理，当前二级页没有额外阈值编辑器。

主磁贴图标显示当前账户额度，沿用任务栏的窗口选择、百分比、绿/黄/红/灰状态与耗尽叉号；账户切换后的状态更新会同时更新图标和磁贴下方的账户名。额度选择和图标数字/颜色模型位于 Core，任务栏与注册 helper 共用，避免两处规则漂移。未知或过期不当作 0%。没有选中账户或服务未启动时显示对应状态；添加菜单中的应用名称保持 Codex Pool。

Shell 收到的 `tileLabel` 与 `quotaIcon` 是有长度、数值范围和颜色白名单限制的声明数据；注册名称和动作声明仍保持稳定。图标使用 Windows XAML 内置形状绘制，不传文件路径、远程地址或可执行 UI。

测试：

```powershell
dotnet run --project tests/CodexPool.Tests -c Release
dotnet run --project tests/CodexPool.SecurityTests -c Release -- --quick-settings-only
.\scripts\tests\quick-settings-isolation.ps1
```

针对性测试使用隔离数据和随机 loopback 端口，不启动或停止当前端口 3210 的代理；覆盖设置原子更新、门禁、服务意图、未知状态、账户行投影和不可用状态。实际账户切换需用户手动验收，不能把模拟数据测试计为真实切换通过。

2026-10-30 另在真实 QSE Broker 与第二个独立示例提供方并存时验证：插件启停、停用期间更新、在线更新、故意启动失败后的回滚、卸载、卸载后 Start 拒绝及重新安装恢复。各阶段 Broker/引擎/Shell/实际代理与示例 PID 未变，示例持续在线；没有关闭代理或切换真实账户。这是插件运维验收，不代替完整 Shell、安装器与 M5 矩阵。
