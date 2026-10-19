# Windows 打包与自动发布

`.github/workflows/windows.yml` 对 PR、master 推送和手动运行执行构建与打包；只有推送 `v*` 标签时才创建 GitHub Release。标签版本必须等于 `VERSION`，且标签提交必须已经合入 master。工作流不会将未通过测试的包发布。

当前发布 Windows 11 x64 自包含便携 ZIP，入口为 CodexPool.exe，附带独立 Daemon、完整 .NET 运行时、使用说明和 SHA256SUMS.txt。不包含源码、研究资料、账户、用户历史、官方 Codex CLI／Desktop。发行版客户端直接启动原生 EXE，不依赖仓库脚本或 `.local` 指针。

本地打包与离线检查：

```powershell
./scripts/package-windows.ps1
./scripts/test-package.ps1 -Archive .local/packages/CodexPool-0.1.0-win-x64.zip -ExpectedVersion 0.1.0
```

修改 `VERSION` 和 `docs/release-notes.md`，提交 PR 并合入 master，然后给该合入提交打相同版本标签：

```powershell
git fetch origin
git tag v0.1.0 origin/master
git push origin v0.1.0
```

包含连字符的版本自动标记为预发布，其余为正式版并设为 Latest。工作流先上传测试过的 ZIP 和校验和到草稿 Release，全部成功后再公开。发布作业只授予 `contents: write`，构建作业保持只读；Actions 固定到已核对的提交 SHA。所有运行保留可下载的 `windows-package` artifact 14 天。

检查范围包括 Core、安全测试（在 CI 上独占本地端口）、菜单和发行布局用例、启动／部署隔离脚本，以及 ZIP 解压后的真实自包含 GUI 和 Daemon 离线启动。GUI 自检不会启动托盘、代理、浏览器、官方 CLI 或创建用户数据。动画与快速点击的完整原生 UI 套件仍有已知时序波动，未作为发布门禁，不能将这里的定向用例解释为完整桌面验收。

正式版和预发布均保留真实长会话、多账户完整故障矩阵及桌面验收限制。没有代码签名证书时不签名，不宣称已签名。升级不自动停止现有服务或覆盖正在运行的文件，使用者按包内说明正常退出后完整替换。
