using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CodexPool.Daemon;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace CodexPool.Floating;

internal sealed class ProxyTrayController : IDisposable
{
    internal static readonly Guid Identity = new("814c1565-8e11-4a4e-94e4-a36d9b7b019f");
    private const string Endpoint = "http://127.0.0.1:3210";
    private readonly Dispatcher dispatcher;
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly StableTrayIcon tray;
    private readonly string root = Environment.GetEnvironmentVariable("CODEX_POOL_ROOT") ?? "";
    private readonly string directory = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPool");
    private GlassTrayMenu? menu;
    private JsonElement? state;
    private bool busy, refreshing, disposed;

    internal ProxyTrayController(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        using var source = Application.GetResourceStream(new Uri("pack://application:,,,/CodexPool.Floating;component/Assets/codex-pool.ico"))!.Stream;
        tray = new StableTrayIcon(new System.Drawing.Icon(source), "Codex Pool · 代理状态", identity: Identity);
        tray.ContextMenuRequested += (_, _) => dispatcher.Invoke(ShowMenu);
        tray.MouseClick += (_, _) => dispatcher.Invoke(ShowMenu);
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
        _ = RefreshAsync();
    }

    private void Authorize() => http.DefaultRequestHeaders.Authorization = new("Bearer", Vault.ReadLocalKey(directory));
    private async Task<JsonElement> ReadStatusAsync()
    {
        Authorize();
        using var response = await http.GetAsync(Endpoint + "/status", lifetime.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(lifetime.Token));
        return json.RootElement.Clone();
    }
    private async Task RefreshAsync()
    {
        if (refreshing || busy || disposed) return;
        refreshing = true;
        try
        {
            state = await ReadStatusAsync();
        }
        catch { state = null; }
        finally { refreshing = false; UpdateTip(); }
    }
    private void UpdateTip()
    {
        if (disposed) return;
        tray.Text = busy ? "Codex Pool · 代理操作中" : state is null ? "Codex Pool · 代理离线" : "Codex Pool · 代理在线";
    }

    internal void ShowMenu()
    {
        if (disposed) return;
        menu?.Close();
        var items = BuildMenu(state is not null, busy, () => Run(StartAsync), () => Run(StopAsync),
            () => Run(() => DesktopClientLauncher.OpenAsync(accountPanel: true, lifetime.Token)), () => Application.Current.Shutdown());
        menu = new GlassTrayMenu(items, () => menu = null) { Title = "Codex Pool · 代理控制" };
        var cursor = System.Windows.Forms.Cursor.Position;
        menu.ShowNear(cursor, tray.TryGetBounds(out var bounds) ? bounds : null);
    }

    internal static IReadOnlyList<GlassMenuItem> BuildMenu(bool online, bool busy, Action start, Action stop, Action openPanel, Action exit) =>
        [new(busy ? "正在操作代理…" : online ? "代理在线 · 127.0.0.1:3210" : "代理离线", Enabled: false),
         new("打开账户浮窗", openPanel, Enabled: !busy),
         new(online ? "关闭代理" : "启动代理", busy ? null : online ? stop : start, Enabled: !busy),
         new("退出代理托盘（代理继续运行）", exit)];

    private async void Run(Func<Task> action)
    {
        if (busy || disposed) return;
        busy = true; UpdateTip();
        try { await action(); }
        catch (OperationCanceledException) when (disposed) { }
        catch { if (!disposed) tray.ShowBalloon("代理操作未完成", "请检查代理状态，账户与用量操作请从数字额度图标打开；操作未自动重试。"); }
        finally { busy = false; await RefreshAsync(); }
    }
    private async Task StopAsync()
    {
        var current = await ReadStatusAsync();
        var identity = ProxyDaemonProcess.Capture(root);
        var requests = current.GetProperty("inFlightRequests").EnumerateArray().Sum(x => x.GetProperty("value").GetInt32());
        if (requests > 0 && MessageBox.Show($"代理正在处理 {requests} 个请求。关闭会停止新请求，进行中的请求可能中断。确定关闭代理？", "关闭 Codex Pool 代理", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        using var response = await http.PostAsJsonAsync(Endpoint + "/control/shutdown", new { }, lifetime.Token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            // A legacy proxy can only be force-stopped with explicit UI confirmation.
            if (MessageBox.Show("当前代理版本不支持平滑关闭。确定直接关闭这个本仓库的代理进程？", "关闭旧版代理", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await ProxyDaemonProcess.StopLegacyAsync(identity, root);
        }
        else { response.EnsureSuccessStatusCode(); await ProxyDaemonProcess.WaitForExitAsync(identity); }
        state = null; tray.ShowBalloon("代理已关闭", "代理托盘保留，可右键重新启动。Codex Desktop 和账户面板未关闭。");
    }
    private async Task StartAsync()
    {
        // An explicit menu action is the only restart path. Polling never starts a daemon.
        if (state is not null) return;
        string path;
        var pointer = Path.Combine(root, ".local", "current-build.json");
        if (PortableInstallation.Exists(root)) path = PortableInstallation.Daemon(root);
        else if (File.Exists(pointer))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(pointer));
            path = Path.Combine(json.RootElement.GetProperty("path").GetString()!, "Daemon", "CodexPool.Daemon.exe");
        }
        else path = Environment.GetEnvironmentVariable("CODEX_POOL_DAEMON") ?? "";
        if (!File.Exists(path) || !ProxyDaemonProcess.OwnedPath(path, root)) throw new InvalidOperationException();
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(path)! };
        start.ArgumentList.Add("serve"); start.ArgumentList.Add("--live-boundary-probe");
        using var child = Process.Start(start) ?? throw new InvalidOperationException();
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(200, lifetime.Token);
            try { state = await ReadStatusAsync(); return; } catch { if (child.HasExited) throw; }
        }
        throw new TimeoutException();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); lifetime.Cancel(); menu?.Close(); tray.Dispose(); http.Dispose(); lifetime.Dispose();
    }
}
