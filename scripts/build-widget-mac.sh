#!/bin/bash
set -euo pipefail
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo '原生 Mac 小组件需要在 macOS 14+ 上构建；Windows 请使用 build-widget.ps1。' >&2
  exit 1
fi
task_root="$(cd "$(dirname "$0")/.." && pwd)"
task_bundle="$task_root/.local/release/Mac/Codex Pool Widget.app"
mkdir -p "$task_bundle/Contents/MacOS"
xcrun swiftc -parse-as-library -swift-version 5 -target "$(uname -m)-apple-macosx14.0" \
  -framework SwiftUI -framework AppKit \
  "$task_root/src/CodexPool.Widget.Mac/CodexPoolWidget.swift" \
  -o "$task_bundle/Contents/MacOS/CodexPoolWidget"
cat > "$task_bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>CodexPoolWidget</string>
<key>CFBundleIdentifier</key><string>local.codexpool.widget</string>
<key>CFBundleName</key><string>Codex Pool 小组件</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>0.1.0</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSAppTransportSecurity</key><dict><key>NSAllowsLocalNetworking</key><true/></dict>
</dict></plist>
PLIST
codesign --force --sign - "$task_bundle"
echo "Mac 小组件已生成：$task_bundle"
echo '预览：open 生成的 app --args --preview。真实连接需先完成 Mac Daemon 安全存储适配。'
