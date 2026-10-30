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

`contextMenu` 的第一项为“打开网页管理”，动作仍由本应用提供方执行，使用与托盘/命令行一致的固定地址 `http://127.0.0.1:3210/`，没有令牌或密钥参数。代理未运行或状态未知时禁用该项，不隐式启动代理、不切换账户。提供方离线时，已有菜单可先按需唤醒，再以新状态校验操作；冷启动没有菜单状态时可用磁贴/详情入口唤醒。

`tile.detailLabel` 为“管理账户”，用于磁贴右侧二级入口的原生悬停提示和无障碍名称。右键菜单继续保留系统“编辑快速设置”入口，编辑行为由框架适配器进入原生模式，与提供方业务操作分离。

实验宿主部署入口已迁入本仓库。首次安装须提供与正在运行的代理一致的绝对数据目录，以下路径仅为示例；已有记录时可省略：

```powershell
.\scripts\Deploy-QuickSettings.ps1 -Force -DataDirectory 'D:\CodexPoolData'
# 只管理本插件，实际代理始终保留：
.\scripts\Stop-QuickSettings.ps1
.\scripts\Start-QuickSettings.ps1
# 显式撤销插件目录注册：
.\scripts\Uninstall-QuickSettings.ps1
# 固定 / 取消固定（目录提交和原生完成分别返回）：
.\scripts\Set-QuickSettingsPinned.ps1 -Pinned $true
.\scripts\Set-QuickSettingsPinned.ps1 -Pinned $false
# 查询最近一次注册 / 固定 / 注销操作：
.\scripts\Get-QuickSettingsOperation.ps1
```

2026-10-30 起，这些脚本只管理 Codex Pool 的注册 helper 与目录绑定，不再部署、启动或停止共享 QSE。先在 QSE 仓库单独安装/启动框架；必须支持 `plugin-status/provider-enable/provider-disable/install-cas/uninstall/on-demand-v1/owned-management-v1/native-confirmation-v1`。`-RuntimeRoot` 默认 `C:\QSE-Host`；旧 `-FrameworkRoot` 参数仅为调用兼容而保留，不触发框架操作。

Provider 新包位于 `%LOCALAPPDATA%\Programs\CodexPool\QuickSettingsProvider\provider-版本号\`，由应用自己管理，QSE 只保存声明、路径、启动授权及用户固定选择。同目录的 `CodexPool.QseManager.exe` 是复用 Daemon 程序集的轻量管理 apphost，操作结束就退出，避免管理命令被误认为等待注册的 Provider。安装时以 `--owner` 授权该路径；它通过标准 `plugin.*` 管理 API 只管理 Codex Pool。原服务和账户数据目录保持原位。旧 `C:\QSE-Host\CodexPool\provider-*` 保留用于历史恢复，新版管理接口要求更新插件后使用。

数据目录按显式 `-DataDirectory`、绝对的 `CODEX_POOL_DATA_DIR`、已有部署记录顺序选择；无法确定时在变更绑定前拒绝，不静默猜测默认目录。相对路径和规范化后的 UNC 路径拒绝，避免从错误位置读取本地密钥。

Manifest 声明 `activation.mode:on-demand`，安装时显式传入 `--allow-activation`。桌面 Broker 校验 EXE 路径、摘要、所有者和当前用户/会话，再以固定参数 `qse-provider <broker.exe> codex-pool <nonce>` 拉起本应用 EXE。应用从自己目录的 `qse-provider.json` 读取服务路径及数据目录；配置不包含密钥，也不由 QSE 解析。详情入口只唤醒；主开关保留用户启停目标，待新状态校验，目标已满足时只读确认。未知结果不自动重放。

Deploy 在插件锁内核对旧绑定和无 pending 状态，停用并停止专用 helper，再比较旧路径安装新版本；保留用户固定选择和停用状态。新 helper 未能注册时恢复旧绑定、部署记录及先前在线的 helper。`-ProviderPackage` 可指定已发布且带有 `qse-manifest.json` 的 helper 目录，否则使用 dotnet publish。实际代理的监听 PID 被排除，不会被停止。

Stop 保留注册和固定选择，持久化停用并撤销旧提供方会话；旧实例无法重新注册。Start 要求插件已安装且路径匹配，只启用并拉起自己的 helper，卸载后不会静默重装。Uninstall 停用/停止 helper，再通过应用自己的 owner API 撤销注册，确认目录已删除后移除部署记录；保留旧发布包及最近操作回执供查询。以上流程均不停止实际代理或其他插件；有业务操作待确认时拒绝停止、更新和卸载。

管理命令返回 `operationId/state/catalogCommitted/nativeConfirmed`。`awaiting-shell` 只表示目录已提交，`complete` 才表示原生完成；`superseded` 表示被后续意图覆盖。Shell 收起或挂起时不会强行展开它，后台清理等待恢复。默认最多等 2 秒，可用 `-WaitSeconds 0` 立即返回。最近操作保存在 Provider 根目录的 `last-operation.json`，注销后仍可查询；这里没有密钥。未知传输结果只按保存的操作 ID 读回，不重新提交变更。注册实例和固定版本的 CAS 防止旧请求覆盖新选择或删除重装后的磁贴。

提供方遵守 QSE 活动租约：面板可见或正在处理操作时继续运行，无活动约 60 秒后自行退出；注册与固定选择保留，下次点击再唤醒。空闲退出不同于 Stop 的显式停用，后者不会被点击重新拉起。统一管理 API 和原生结果确认已实现；完整安装器文件删除、断电恢复仍待验收。框架自身升级仍可能等待 Shell 恢复执行；插件日常升级不再要求卸载共享适配器。

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

同日按需阶段另通过原生详情箭头唤醒 Codex Pool，两个独立 Provider 收起面板后自行空闲退出，再次通过原生详情箭头创建新的 Codex Pool 进程；示例的冷启动显式启停和重复唤醒单实例也已确认。测试基线内共享进程及实际代理 PID 未变，未切换真实账户；框架维护前后曾观察到 Shell 重建，不据此宣布同进程热升级或完整 M5 通过。

第三阶段实机通过 owner 归属拒绝、两个插件独立固定/取消固定、详情打开时注销并关闭页面、重装新身份和原固定选择恢复，以及收起时等待、展开后四个原生集合确认移除。另一插件及实际代理保持不变，管理操作不需要示例 Provider 常驻。定向安全测试还模拟变更响应丢失，验证只读查询回执、没有重复提交。更新脚本在发出变更前标记事务已开始，未知返回也进入实际状态检查与安全回滚。

补充恢复检查模拟完成结果的 ACK 丢失：重注册补送冻结的同一状态及 revision，回执解决后强制发布更高版本；不会因为重新读取本机状态而让旧 completionState 覆盖新注册版本。Broker 长期不可达且没有正在执行的操作时，按需 helper 使用本地约 60 秒期限退出。启动期间的所有状态读取跳过 `daemon.lock` 探测，避免探测句柄与子进程的独占锁竞争；托盘也在启停确认后保存服务意图。以上故障检查使用隔离数据和模拟传输，没有自动启停现用代理。
