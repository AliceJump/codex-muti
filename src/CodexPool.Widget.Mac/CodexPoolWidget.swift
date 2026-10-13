import AppKit
import SwiftUI

// UI client only. OAuth, scheduling and model requests remain in the daemon.
struct UsageWindow: Decodable, Identifiable {
    let kind: String
    let usedPercent: Double
    let resetAt: Date
    var id: String { kind }
}
struct AccountUsage: Decodable {
    let accountId: String
    let windows: [UsageWindow]?
    let updatedAt: Date?
}
struct PoolAccount: Decodable, Identifiable {
    let id: String
    let name: String
    let active: Bool
    let enabled: Bool
    let paused: Bool
    let drain: Bool
    let state: String
    let plan: String?
    let auth: String
    var selectable: Bool {
        enabled && !paused && !drain && state != "cooldown" && auth == "ready"
    }
}
struct PoolStatus: Decodable {
    let automaticSwitching: Bool
    let accounts: [PoolAccount]
    let usage: [AccountUsage]
    static var preview: PoolStatus {
        let reset = Date().addingTimeInterval(3600)
        return PoolStatus(automaticSwitching: true, accounts: [
            PoolAccount(id: "preview-a", name: "Account A", active: true, enabled: true, paused: false, drain: false, state: "active", plan: "Plus", auth: "ready"),
            PoolAccount(id: "preview-b", name: "Account B", active: false, enabled: true, paused: false, drain: false, state: "standby", plan: "Plus", auth: "ready")
        ], usage: [
            AccountUsage(accountId: "preview-a", windows: [UsageWindow(kind: "5h", usedPercent: 91, resetAt: reset), UsageWindow(kind: "weekly", usedPercent: 52, resetAt: reset.addingTimeInterval(345600))], updatedAt: Date()),
            AccountUsage(accountId: "preview-b", windows: [UsageWindow(kind: "5h", usedPercent: 19, resetAt: reset.addingTimeInterval(7200)), UsageWindow(kind: "weekly", usedPercent: 31, resetAt: reset.addingTimeInterval(432000))], updatedAt: Date())
        ])
    }
}

// Retrieve the local control key through a private pipe; never store it in preferences,
// put it on a command line, print it, or read/copy an OAuth grant.
enum CredentialPipe {
    static func read() -> String? {
        let configured = ProcessInfo.processInfo.environment["CODEX_POOL_DAEMON"]
        let adjacent = Bundle.main.bundleURL.deletingLastPathComponent().appendingPathComponent("Daemon/CodexPool.Daemon").path
        let executable = configured ?? adjacent
        guard executable.hasPrefix("/"), FileManager.default.isExecutableFile(atPath: executable) else { return nil }
        let process = Process(), pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = ["model-credential"]
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return nil }
        let deadline = Date().addingTimeInterval(5)
        while process.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.05) }
        guard !process.isRunning else { process.terminate(); return nil }
        guard process.terminationStatus == 0,
              let data = try? pipe.fileHandleForReading.read(upToCount: 256),
              let value = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              value.count == 64, value.allSatisfy({ $0.isHexDigit }) else { return nil }
        return value
    }
}

final class LocalTransport: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    // Fail closed on redirects so local credentials cannot leave loopback.
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}

@MainActor final class WidgetStore: ObservableObject {
    @Published var status = PoolStatus.preview
    @Published var preview = true
    @Published var connected = false
    @Published var busy = false
    @Published var message = "预览数据 · 未连接代理"
    private var key: String?
    private var watcher: Task<Void, Never>?
    private let transport = LocalTransport()
    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 5
        config.connectionProxyDictionary = [:]
        return URLSession(configuration: config, delegate: transport, delegateQueue: nil)
    }()
    private let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { value in
            let text = try value.singleValueContainer().decode(String.self)
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            if let date = formatter.date(from: text) { return date }
            formatter.formatOptions = [.withInternetDateTime]
            guard let date = formatter.date(from: text) else { throw URLError(.cannotParseResponse) }
            return date
        }
        return decoder
    }()
    func start() {
        guard watcher == nil else { return }
        watcher = Task { await watch() }
    }
    private func watch() async {
        guard !CommandLine.arguments.contains("--preview") else { return }
        key = await Task.detached(priority: .utility) { CredentialPipe.read() }.value
        guard key != nil else {
            message = "预览数据 · Mac 代理适配尚未就绪"
            return
        }
        preview = false
        status = PoolStatus(automaticSwitching: false, accounts: [], usage: [])
        while !Task.isCancelled {
            await refresh()
            do { try await Task.sleep(nanoseconds: 3_000_000_000) } catch { return }
        }
    }
    private func request(_ path: String, post: Bool = false) async throws -> Data {
        guard let key else { throw URLError(.userAuthenticationRequired) }
        var request = URLRequest(url: URL(string: "http://127.0.0.1:3210" + path)!)
        request.httpMethod = post ? "POST" : "GET"
        request.setValue("Bearer " + key, forHTTPHeaderField: "Authorization")
        let (data, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse, response.statusCode == 200, data.count <= 2_097_152 else { throw URLError(.badServerResponse) }
        return data
    }
    func refresh() async {
        guard !preview, !busy else { return }
        do {
            status = try decoder.decode(PoolStatus.self, from: await request("/status"))
            connected = true; message = "服务在线 · Web 已启用"
        } catch { connected = false; message = "服务离线 · 等待重新连接" }
    }
    func activate(_ account: PoolAccount) async {
        guard !preview, connected, !busy, account.selectable, !account.active else { return }
        busy = true
        do { _ = try await request("/accounts/" + account.id + "/activate", post: true) }
        catch { message = "账户切换未确认，请在 Web 面板检查" }
        busy = false
        await refresh()
    }
    func dashboard() async {
        guard !preview else { return }
        do {
            let data = try await request("/dashboard/session", post: true)
            struct Ticket: Decodable { let url: String }
            let ticket = try decoder.decode(Ticket.self, from: data)
            guard let url = URL(string: ticket.url), url.scheme == "http", url.host == "127.0.0.1", url.port == 3210,
                  url.user == nil, url.password == nil, url.query == nil, url.fragment == nil,
                  url.path.hasPrefix("/dashboard/open/") else { return }
            NSWorkspace.shared.open(url)
        } catch { message = "Web 面板打开失败，请检查代理状态" }
    }
}

struct Backdrop: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .hudWindow; view.blendingMode = .behindWindow; view.state = .active
        return view
    }
    func updateNSView(_ view: NSVisualEffectView, context: Context) { }
}
struct WindowSetup: NSViewRepresentable {
    let pinned: Bool
    let name: String
    func makeNSView(context: Context) -> NSView { NSView() }
    func updateNSView(_ view: NSView, context: Context) {
        DispatchQueue.main.async {
            guard let window = view.window else { return }
            window.level = pinned ? .floating : .normal
            window.isOpaque = false; window.backgroundColor = .clear
            window.isMovableByWindowBackground = true
            window.setFrameAutosaveName(name)
        }
    }
}
struct WidgetView: View {
    @ObservedObject var store: WidgetStore
    @AppStorage("widget.pinned") private var pinned = false
    @AppStorage("widget.compact") private var compact = false
    @Environment(\.openWindow) private var openWindow
    var floating = false
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text(floating ? "Codex Pool · 悬浮窗" : "Codex Pool · 小组件").font(.headline)
                Spacer()
                Button { compact.toggle() } label: { Image(systemName: "rectangle.compress.vertical") }
                Button { pinned.toggle() } label: { Image(systemName: pinned ? "pin.fill" : "pin") }
            }.buttonStyle(.plain)
            Label(store.message, systemImage: store.preview ? "eye" : store.connected ? "circle.fill" : "wifi.slash")
                .font(.caption).foregroundStyle(store.connected ? Color.green : Color.secondary)
            if !compact {
                VStack(alignment: .leading, spacing: 4) {
                    Text("当前账户").font(.caption).foregroundStyle(.secondary)
                    Text(store.status.accounts.first(where: { $0.active })?.name ?? "尚未选择账户").font(.title2.bold())
                }.frame(maxWidth: .infinity, alignment: .leading).padding(12).background(.blue.opacity(0.08), in: RoundedRectangle(cornerRadius: 12))
            }
            ScrollView {
                VStack(spacing: 10) {
                    ForEach(store.status.accounts.filter { $0.enabled || $0.active }) { account in
                        accountCard(account)
                    }
                    if store.status.accounts.isEmpty { Text("尚无账户，请在 Web 面板添加").foregroundStyle(.secondary) }
                }
            }.frame(maxHeight: compact ? 190 : 360)
            HStack {
                Text(store.status.automaticSwitching ? "自动切换 · 开启" : "自动切换 · 关闭").font(.caption).foregroundStyle(.secondary)
                Spacer()
                if store.preview { Text("示例数据").font(.caption).foregroundStyle(.orange) }
            }
            HStack {
                Button(floating ? "打开小组件" : "打开悬浮窗") { openWindow(id: floating ? "widget" : "floating") }
                Spacer()
                Button("打开 Web ↗") { Task { await store.dashboard() } }.disabled(store.preview || !store.connected)
            }.controlSize(.large)
        }.padding(18).frame(width: 360).background(Backdrop()).clipShape(RoundedRectangle(cornerRadius: 18))
            .background(WindowSetup(pinned: floating || pinned, name: floating ? "CodexPoolFloating" : "CodexPoolWidget"))
    }
    private func accountCard(_ account: PoolAccount) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Button { Task { await store.activate(account) } } label: {
                HStack {
                    Image(systemName: account.active ? "largecircle.fill.circle" : "circle")
                    Text(account.name).fontWeight(account.active ? .semibold : .regular)
                    Spacer()
                    Text(account.active ? "使用中" : account.paused ? "已暂停" : "待命").font(.caption).foregroundStyle(.secondary)
                }
            }.buttonStyle(.plain).disabled(store.preview || !store.connected || store.busy || !account.selectable)
            if !compact { Text(account.plan ?? "Plan 待确认").font(.caption2).foregroundStyle(.secondary) }
            ForEach(store.status.usage.first(where: { $0.accountId == account.id })?.windows ?? []) { window in
                VStack(spacing: 4) {
                    HStack {
                        Text(window.kind == "5h" ? "5h" : "周额度")
                        Spacer()
                        Text("\(Int(window.usedPercent))% 已用")
                        Text(window.resetAt, style: .relative).foregroundStyle(.secondary)
                    }.font(.caption)
                    ProgressView(value: min(100, max(0, window.usedPercent)), total: 100).tint(window.usedPercent >= 95 ? .orange : .blue)
                }
            }
            if !compact, let date = store.status.usage.first(where: { $0.accountId == account.id })?.updatedAt {
                HStack { Text("更新"); Text(date, style: .time); if Date().timeIntervalSince(date) > 120 { Text("已过期") } }
                    .font(.caption2).foregroundStyle(.secondary)
            }
        }.padding(12).background(account.active ? .blue.opacity(0.12) : .gray.opacity(0.06), in: RoundedRectangle(cornerRadius: 12))
    }
}

@main @MainActor struct CodexPoolWidgetApp: App {
    @StateObject private var store = WidgetStore()
    var body: some Scene {
        Window("Codex Pool · 小组件", id: "widget") {
            WidgetView(store: store).task { store.start() }
        }.windowStyle(.hiddenTitleBar).windowResizability(.contentSize)
        Window("Codex Pool · 悬浮窗", id: "floating") {
            WidgetView(store: store, floating: true).task { store.start() }
        }.windowStyle(.hiddenTitleBar).windowResizability(.contentSize)
        MenuBarExtra("Codex Pool", systemImage: "circle.grid.2x2") {
            VStack(spacing: 0) {
                WidgetView(store: store).task { store.start() }
                Button("退出界面") { NSApp.terminate(nil) }.buttonStyle(.plain).padding(.bottom, 12)
            }
        }.menuBarExtraStyle(.window)
    }
}
struct MenuView: View {
    @ObservedObject var store: WidgetStore
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        Button("显示小组件") { openWindow(id: "widget"); NSApp.activate(ignoringOtherApps: true) }
        Button("显示悬浮窗") { openWindow(id: "floating"); NSApp.activate(ignoringOtherApps: true) }
        Button("打开 Web 面板") { Task { await store.dashboard() } }.disabled(store.preview || !store.connected)
        Divider()
        Button("退出界面") { NSApp.terminate(nil) }
    }
}
