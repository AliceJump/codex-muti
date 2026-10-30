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
.\scripts\Deploy-QuickSettings.ps1 -Force -FrameworkRoot D:\items\project\github_project\quick-settings-extensions
```

脚本只替换注册 helper，并调用 QSE 的通用框架部署/安装 API；保留现用代理进程。框架停用不终止业务进程。宿主 Shell 挂起时可能需要保持 Win+A 展开以完成适配器卸载；尚未作为无交互正式安装器交付。

`proxy-service-intent.json` 仅保存已确认的服务运行意图，供新版本桌面启动器避免在用户关闭后再次隐式启动。启动继承此前认证服务的启动模式，不为使开关可用而新增迁移门禁绕过。账户切换仍受现有 Daemon 的资格、额度和生产门禁校验；失败保留原账户。

额度使用本机 `status.usage` 的真实缓存窗口，未知显示未知，过期值标记缓存。底部“刷新状态”只重读本机状态，不等于上游额度刷新。自动切换设置仍由既有控制台管理，当前二级页没有额外阈值编辑器。

主磁贴图标显示当前账户额度，沿用任务栏的窗口选择、百分比、绿/黄/红/灰状态与耗尽叉号；账户切换后的状态更新会同时更新图标和磁贴下方的账户名。额度选择和图标数字/颜色模型位于 Core，任务栏与注册 helper 共用，避免两处规则漂移。未知或过期不当作 0%。没有选中账户或服务未启动时显示对应状态；添加菜单中的应用名称保持 Codex Pool。

Shell 收到的 `tileLabel` 与 `quotaIcon` 是有长度、数值范围和颜色白名单限制的声明数据；注册名称和动作声明仍保持稳定。图标使用 Windows XAML 内置形状绘制，不传文件路径、远程地址或可执行 UI。

测试：

```powershell
dotnet run --project tests/CodexPool.Tests -c Release
dotnet run --project tests/CodexPool.SecurityTests -c Release -- --quick-settings-only
```

针对性测试使用隔离数据和随机 loopback 端口，不启动或停止当前端口 3210 的代理；覆盖设置原子更新、门禁、服务意图、未知状态、账户行投影和不可用状态。实际账户切换需用户手动验收，不能把模拟数据测试计为真实切换通过。
