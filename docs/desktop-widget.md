# 托盘状态面板（第一版）

状态面板位于右下角 Codex Pool 托盘图标，单击展开毛玻璃卡片。它只展示代理状态和额度、发送账户激活请求，以及打开悬浮窗或 Web，不管理 Codex 会话或执行工具。

## Windows 11

```powershell
.\scripts\build-and-start.ps1
# 或分开执行：
.\scripts\build-local.ps1
.\scripts\start-local.ps1
# 同时打开浏览器：
.\scripts\start-local.ps1 -Dashboard
```

`build-local.ps1` 将 Daemon、Floating、Widget 发布到独立的 `.local/builds/<版本目录>`，不会覆盖正在运行的文件，也不停止或重启进程。`start-local.ps1` 使用最后一次成功构建；已有代理时保留原进程，只启动托盘客户端。让新代理代码生效，需要你先结束模型请求、自己退出旧代理，再运行启动脚本。旧托盘界面可从菜单“退出托盘面板”退出，代理继续运行。

原来的固定目录构建入口也保留：

```powershell
.\scripts\build-widget.ps1
.\scripts\install-widget-shortcut.ps1
.\scripts\open-widget.ps1
```

第一次完整安装先运行 `scripts/build-release.ps1`，生成相邻的 Daemon。之后只更新小组件可用 `build-widget.ps1`，不会发布或重启正在工作的代理。

- 桌面启动入口：**Codex Pool 小组件**。启动后只出现托盘图标；单击图标展开面板，再次单击、按 Escape 或点击外部收起。没有代理则启动相邻的 Daemon；已有代理继续工作。
- Web 服务随 Daemon 默认启用，地址 `http://127.0.0.1:3210`；不会每次打开小组件都弹浏览器。**打开 Web** 使用单次安全票据建立浏览器会话。
- 默认展示当前账户、启用账户数、进行中的请求数、每账户 5h/周额度、重置倒计时、最近额度更新时间、自动切换状态。
- 托盘小图标显示当前账户 **5h 剩余百分比**（例如 `12` 表示剩余 12%）；5h 未知时显示已知的周剩余量。剩余低于 30% 时黄色、低于 15% 时红色，任一额度窗口耗尽时显示 **X**。悬停显示账户名称、两个窗口剩余和重置倒计时；离线、数据过期或窗口已重置但未刷新时显示灰色 `—`，不伪装成 0%。
- Radio 只修改下一次请求的 sticky active account。当前请求继续使用原账户。
- **打开悬浮窗**显示原悬浮窗；小组件与悬浮窗可以同时运行，两个界面通过同一个 `/events` 实时同步。
- 面板展开时临时置顶，定位到托盘操作所在显示器右下角工作区，不覆盖任务栏。支持完整/紧凑/胶囊、系统/浅色/深色；不会变成拖动常驻的桌面窗口。
- 胶囊只显示可见账户的名称、单选状态和一行 5h/周剩余额度；点击账户切换，自动切换时选中状态同步。暂停、排空或离线用短状态提示替代额度。胶囊没有展开、刷新、Web、关闭等按钮，也不显示圆环、标题或底部操作栏；宽度按账户内容收缩，完整名称和重置时间可悬停查看。已禁用的非当前账户不占位置。
- 窗口获得焦点时按 `Ctrl+M` 循环切换完整/紧凑/胶囊模式。独立悬浮窗的胶囊可拖动边缘空白区域；显隐快捷键和 Alt+F4 仍然可用。
- `Ctrl+Alt+W` 显示/隐藏小组件；原悬浮窗仍使用 `Ctrl+Alt+C`。重复打开只唤醒已有界面。
- 隐藏保留托盘图标，托盘“退出托盘面板”只退出客户端。代理、Web 服务和其他客户端继续运行。
- 只有托盘面板进程创建托盘图标；独立悬浮窗的 **×** 或 Alt+F4 真正退出该 UI，不停止代理，也不结束托盘面板。再次启动快捷方式或使用托盘“打开悬浮窗”可打开新窗口。
- `widget.json` 单独保存布局和主题，原浮窗的 `window.json` 保留独立设置。
- 小组件不显示已禁用的历史账户；这些账户仍可在 Web 面板管理。未知额度显示 `—`，离线时禁止账户切换，保留的快照由离线状态提示。

托盘图标可能收进右下角 **^** 的隐藏图标区域，可在 Windows 任务栏设置中设为常显。本面板使用独立托盘入口，不嵌入 Wi-Fi/音量快捷设置，也不是 Windows Widgets 面板提供程序。微软的 [NotificationIcon 示例](https://learn.microsoft.com/en-us/windows/win32/shell/samples-notificationicon)支持这种 rich flyout 形式。

## macOS 客户端原型

原生源码位于 `src/CodexPool.Widget.Mac/CodexPoolWidget.swift`，使用 SwiftUI、AppKit 原生背景材质和菜单栏入口，最低系统 macOS 14。菜单栏图标弹出账户状态卡片，并提供浮窗和 Web 入口。状态更新任务独立于窗口生命周期。

在 Mac 上构建并预览：

```bash
bash scripts/build-widget-mac.sh
open '.local/release/Mac/Codex Pool Widget.app' --args --preview
```

需要 Xcode Command Line Tools。脚本生成当前 Mac 架构的 `.app`，使用本机 ad-hoc 签名，尚未提供公证或发行签名。

**当前交付是 Mac 客户端源码与预览入口，未通过 Mac 编译或真机验收；现有 Daemon 仍依赖 Windows DPAPI，不能直接在 Mac 上运行。** 完整 Mac 使用需要后续独立实现 Keychain 凭据存储、跨平台 Daemon 打包和真实账户验收。预览数据有明确标记，不能当成真实额度。

客户端已预留仅连接 `127.0.0.1:3210` 的 Local API 对接。未来 Mac Daemon 与 `.app` 相邻放置于 `Mac/Daemon/CodexPool.Daemon`，或设置 `CODEX_POOL_DAEMON` 为本机可执行文件绝对路径。客户端通过 Daemon `model-credential` 的私有 stdout 管道读取本地控制 key，不读取 OAuth、不会记录 key、不写入偏好文件；请求禁用代理与重定向。真实连接每 3 秒更新状态，离线时禁止切换。点击切换由 Daemon 负责 sticky 调度。

Windows 无法直接运行 SwiftUI/AppKit 原生 `.app`。可以在当前 Windows 小组件上确认同类卡片布局和操作流程；Mac 材质、菜单栏、多显示器和系统行为需要真实 Mac 测试。当前原型也没有注册为 macOS WidgetKit 桌面组件。

## 验收边界

Windows：构建、真实代理状态/额度显示、与原浮窗共存、隐藏启动、快捷键展开、启动器单实例唤醒、打开浮窗后收起已验证。原代理持续运行，未更改额度阈值或 Codex 配置。手动 Radio 切换/自动切换同步、真实托盘鼠标操作、多显示器和长期离线重连还需进一步验收；账户迁移仍以独立真实验收报告为准。

Mac：当前 Windows 环境仅能审查源码和构建脚本，原生编译、材质与生命周期未验证。
