using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexPool.Daemon;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

using CodexPool.Core;

namespace CodexPool.Floating;

public sealed record WindowPreferences(int X = 80, int Y = 80, int Mode = 0, int Theme = 0, bool Pinned = false, int PositionVersion = 0);
public partial class FloatingWindow : Window
{
    private readonly string directory = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPool");
    private readonly CancellationTokenSource lifetime = new();
    private readonly LocalControlClient http;
    private readonly StableTrayIcon? tray;
    private GlassTrayMenu? glassMenu;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer trayDismissTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private WindowPreferences preferences = new();
    private JsonElement? snapshot;
    private bool exiting, busy, connected;
    private bool lightTheme;
    private readonly bool startServices;
    private const string PreferencesFile = "widget.json";
    private Forms.Screen? flyoutScreen;
    private System.Drawing.Rectangle? flyoutAnchor;
    private bool positioningFlyout;
    private string? trayStateKey;
    private string? lastActiveAccountId;
    private bool switchNotifyReady;
    private bool trayClickCloses;
    private EventHandler? entranceReveal;
    private bool nativeEntranceEnabled;
    private bool entranceCloaked;
    private nint handle;
    internal bool Pinned => preferences.Pinned;
    public FloatingWindow() : this(true) { }
    internal FloatingWindow(bool startServices, HttpMessageHandler? handler = null, Func<string>? readKey = null, StableTrayIcon? previewTray = null)
    {
        this.startServices = startServices;
        tray = previewTray;
        http = new LocalControlClient(directory, handler, readKey);
        InitializeComponent();
        if (startServices)
            try { preferences = JsonSerializer.Deserialize<WindowPreferences>(File.ReadAllText(Path.Combine(directory, PreferencesFile))) ?? preferences; } catch { }
        preferences = preferences with { Mode = Math.Clamp(preferences.Mode, 0, 2), Theme = Math.Clamp(preferences.Theme, 0, 2) };
        Topmost = true;
        Title = "Codex Pool · 账户面板";
        Scene.SetPinned(Pinned);
        Scene.ActivateAccount = async id => { if (startServices && !busy && connected) await ActivateAsync(id); };
        Scene.InvokeAction = async action =>
        {
            if (!startServices && action is "web" or "refresh") { SetNotice("独立预览不连接代理。"); return; }
            switch (action)
            {
                case "mode": CycleMode(this, new RoutedEventArgs()); break;
                case "theme": CycleTheme(this, new RoutedEventArgs()); break;
                case "close": DismissWindow(this, new RoutedEventArgs()); break;
                case "web": await DashboardAsync(); break;
                case "refresh": await RefreshQuotaAsync(true); break;
                case "pin": TogglePinned(); break;
            }
        };
        Scene.DragRequested = () => { if (Pinned) { DragMove(); ConstrainPinnedScene(IsVisible); SavePosition(); } };
        Scene.TouchDragRequested = delta => { if (Pinned) { Left += delta.X; Top += delta.Y; } };
        Scene.TouchDragCompleted = () => { if (Pinned) { ConstrainPinnedScene(IsVisible); SavePosition(); } };
        Scene.ContextRequested = point =>
        {
            var screen = Scene.PointToScreen(point);
            ShowSceneMenu(new System.Drawing.Point((int)Math.Round(screen.X), (int)Math.Round(screen.Y)));
        };
        if (startServices)
        {
            tray = new StableTrayIcon(CreateIcon(), "Codex Pool · 账户状态 · Ctrl+Alt+W");
            UpdateTrayQuota(null);
            tray.ContextMenuRequested += (_, e) => Dispatcher.Invoke(() => ShowGlassTrayMenu(e));
            tray.LeftPress += (_, _) => Dispatcher.Invoke(HandleTrayPress);
            tray.MouseClick += (_, e) =>
            {
                if (e.Button != Forms.MouseButtons.Left) return;
                Dispatcher.Invoke(() => HandleTrayClick(new System.Drawing.Point(e.X, e.Y), keyboard: e.Clicks == 0));
            };
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.M && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            { CycleMode(this, new RoutedEventArgs()); e.Handled = true; }
            if (e.Key == System.Windows.Input.Key.Escape) { SavePosition(); Hide(); }
        };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
            ConfigureHost();
            if (!startServices) return;
            var registered = RegisterHotKey(handle, 1, 0x0001 | 0x0002 | 0x4000, 0x57u);
            if (!registered) SetNotice("快捷键已被其他应用使用，可从托盘或启动入口显示窗口。");
            if (Pinned) RestorePosition(); _ = WatchAsync(); timer.Start();
        };
        Deactivated += (_, _) => DismissOnDeactivation(IsTrayPress());
        IsVisibleChanged += (_, _) => { if (!IsVisible) { CancelEntranceReveal(); SetEntranceCloak(false); } };
        trayDismissTimer.Tick += (_, _) => CompleteTrayPress((GetAsyncKeyState(1) & 0x8000) != 0, IsActive);
        timer.Tick += (_, _) => { SavePosition(); if (snapshot is { } state) Render(state); };
        Closing += OnClosing;
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        ApplyTheme();
    }
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => { ApplyTheme(); if (snapshot is { } state) Render(state); });
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == 1) { ToggleVisibility(); handled = true; }
        if (message is 0x007e or 0x02e0) Dispatcher.BeginInvoke(() => { Scene.Stop(); ConfigureHost(); ClampPosition(); });
        return 0;
    }
    private void ApplyTheme()
    {
        lightTheme = preferences.Theme == 1 || preferences.Theme == 0 && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int value && value == 1;
        Scene.SetTheme(lightTheme, SystemParameters.HighContrast);
    }
    private async Task WatchAsync()
    {
        try { await ProxyStartup.EnsureRunningAsync(lifetime.Token); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { SetNotice("代理启动未完成 · 正在重新连接"); }
        while (!lifetime.IsCancellationRequested)
        {
            await RefreshStatusAsync();
            if (!lifetime.IsCancellationRequested) try { await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token); } catch (OperationCanceledException) { }
        }
    }
    internal async Task RefreshStatusAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await http.GetAsync("/status", timeout.Token); response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (!lifetime.IsCancellationRequested) { connected = true; Render(document.RootElement.Clone()); }
        }
        catch (Exception ex) when (LocalControlClient.IsUnavailable(ex))
        { if (!lifetime.IsCancellationRequested) SetOffline(); }
    }
    internal void InitializeHidden() => _ = new WindowInteropHelper(this).EnsureHandle();
    internal async void OpenDashboardOnStartup()
    {
        // Initial status polling also waits for daemon startup; it never shows the card.
        for (var i = 0; i < 50 && !connected && !lifetime.IsCancellationRequested; i++)
            try { await Task.Delay(200, lifetime.Token); } catch (OperationCanceledException) { return; }
        if (!lifetime.IsCancellationRequested) await DashboardAsync();
    }
    private void SetOffline()
    {
        connected = false;
        if (snapshot is { } state) Render(state);
        else { UpdateTrayQuota(null); Scene.SetState([], preferences.Mode, false, false, IsVisible); }
    }
    internal void ShowFromLauncher()
    {
        if (IsVisible) { Activate(); return; }
        if (Pinned) ShowPinned(); else ShowFlyout();
    }
    private void Render(JsonElement state)
    {
        snapshot = state;
        UpdateTrayQuota(connected ? state : null);
        NotifyActiveSwitch(state);
        Scene.SetState(PoolPresentation.Accounts(state, connected), preferences.Mode, connected,
            state.GetProperty("automaticSwitching").GetBoolean(), IsVisible,
            state.GetProperty("inFlightRequests").EnumerateArray().Sum(x => x.GetProperty("value").GetInt32()));
        Scene.SetBusy(busy);
        ConstrainPinnedScene(IsVisible);
    }
    internal void Preview(JsonElement state, int mode, bool online = true)
    { connected = online; preferences = preferences with { Mode = mode }; Render(state); }
    private void NotifyActiveSwitch(JsonElement state)
    {
        var active = state.GetProperty("accounts").EnumerateArray().FirstOrDefault(x => x.GetProperty("active").GetBoolean());
        var id = active.ValueKind == JsonValueKind.Object ? active.GetProperty("id").GetString() : null;
        var name = active.ValueKind == JsonValueKind.Object ? active.GetProperty("name").GetString() ?? "账户" : null;
        if (!switchNotifyReady)
        {
            lastActiveAccountId = id;
            switchNotifyReady = true;
            return;
        }
        if (id is null || id == lastActiveAccountId) return;
        lastActiveAccountId = id;
        var message = "已切换到 " + name;
        SetNotice(message);
        NotifyTray("账户切换", message);
    }
    private void UpdateTrayQuota(JsonElement? state)
    {
        if (tray is null) return;
        double? remaining = null;
        var exhausted = false;
        var tooltip = "Codex Pool\n服务离线 · 额度未知";
        if (state is { } snapshot)
        {
            var account = snapshot.GetProperty("accounts").EnumerateArray().FirstOrDefault(x => x.GetProperty("active").GetBoolean());
            if (account.ValueKind == JsonValueKind.Object)
            {
                var id = account.GetProperty("id").GetString();
                var name = account.GetProperty("name").GetString() ?? "Codex Pool";
                if (name.Length > 20) name = name[..10] + "…" + name[^9..];
                var quota = snapshot.GetProperty("usage").EnumerateArray().FirstOrDefault(x => x.GetProperty("accountId").GetString() == id);
                snapshot.TryGetProperty("settings", out var settings);
                var window = QuotaDisplay.TrayWindow(quota, settings);
                remaining = window?.Remaining;
                exhausted = window?.Remaining is <= 0;
                tooltip = name + (window is null ? "\n" + QuotaDisplay.State(quota)
                    : "\n" + window.Label + " " + QuotaDisplay.Percent(window) + " · " + ResetLeft(window.Window));
            }
            else tooltip = "Codex Pool\n尚未选择账户";
        }
        if (tooltip.Length > 63) tooltip = tooltip[..63];
        var key = tooltip + "|" + remaining?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" + exhausted;
        if (key == trayStateKey) return;
        var icon = QuotaTrayIcon.Create(remaining, exhausted);
        var previous = tray.Icon;
        // Commit the display key only after Explorer accepts both values. A failed
        // publication must be retried even when the next status snapshot is equal.
        trayStateKey = tray.Update(icon, tooltip) ? key : null;
        previous?.Dispose();
    }
    private static string ResetLeft(JsonElement window)
    {
        if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("resetAt", out var reset)
            || reset.ValueKind != JsonValueKind.String || !reset.TryGetDateTimeOffset(out var at)) return "重置时间未知";
        var span = at - DateTimeOffset.UtcNow;
        if (span <= TimeSpan.Zero) return "等待新额度";
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(0, (int)span.TotalMinutes)}m";
    }
    private async Task ActivateAsync(string id)
    {
        busy = true; Scene.SetBusy(true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.PostAsync("/accounts/" + id + "/activate", new { }, timeout.Token);
            if (!response.IsSuccessStatusCode) { OperationNotice("账户切换未完成。请在 Dashboard 检查账户状态。"); }
            else { Scene.SetNotice(""); }
            using var status = await http.GetAsync("http://127.0.0.1:3210/status", timeout.Token); status.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(timeout.Token)); Render(json.RootElement.Clone());
        }
        catch (Exception ex) when (LocalControlClient.IsUnavailable(ex)) { OperationNotice("服务暂时不可用。账户选择尚未确认。"); if (snapshot is { } state) Render(state); }
        finally { busy = false; Scene.SetBusy(false); }
    }
    internal async Task RefreshQuotaAsync(bool notify)
    {
        if (busy) return;
        busy = true; Scene.SetBusy(true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var usage = await http.GetAsync("http://127.0.0.1:3210/usage", timeout.Token); usage.EnsureSuccessStatusCode();
            using var status = await http.GetAsync("http://127.0.0.1:3210/status", timeout.Token); status.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(timeout.Token)); connected = true; Render(json.RootElement.Clone());
            if (notify)
            {
                var message = QuotaRefreshNotice(json.RootElement);
                SetNotice(message);
                NotifyTray("额度更新", message);
            }
        }
        catch (Exception ex) when (LocalControlClient.IsUnavailable(ex))
        { OperationNotice("额度刷新失败。请检查本地服务或在 Dashboard 查看状态。"); }
        finally { busy = false; Scene.SetBusy(false); }
    }
    internal static string QuotaRefreshNotice(JsonElement state) => state.TryGetProperty("quotaRefreshErrors", out var errors)
        && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
        ? $"{errors.GetArrayLength()} 个账户额度更新失败，保留历史缓存。"
            + string.Join("；", errors.EnumerateArray().Select(e => CodexPool.Core.QuotaLabels.Failure(PoolPresentation.String(e, "code"))).Distinct()) : "额度已更新";
    private void NotifyTray(string title, string text)
    {
        if (tray is null) return;
        try
        {
            tray.ShowBalloon(title, text);
        }
        catch { }
    }
    private Task DashboardAsync()
    {
        try
        {
            Process.Start(new ProcessStartInfo("http://127.0.0.1:3210/") { UseShellExecute = true });
        }
        catch { OperationNotice("无法打开 Dashboard，请检查本地服务。"); }
        return Task.CompletedTask;
    }
    private async Task ToggleSwitchAsync()
    {
        if (!startServices || busy || !connected) return;
        busy = true; Scene.SetBusy(true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var status = await http.GetAsync("http://127.0.0.1:3210/status", timeout.Token); status.EnsureSuccessStatusCode();
            using var current = JsonDocument.Parse(await status.Content.ReadAsStringAsync(timeout.Token));
            var settings = AccountTrayMenu.ToggleSettings(current.RootElement);
            using var response = await http.PostAsync("/settings", settings, timeout.Token); response.EnsureSuccessStatusCode();
            SetNotice(settings.AutoSwitch ? "自动切换已开启" : "自动切换已关闭");
        }
        catch (Exception ex) when (LocalControlClient.IsUnavailable(ex))
        { OperationNotice("自动切换设置未确认，请在账户管理页检查。"); }
        finally { busy = false; Scene.SetBusy(false); }
    }
    private async Task OpenProxyAsync()
    {
        if (!startServices || busy) return;
        busy = true; Scene.SetBusy(true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await ProxyStartup.EnsureRunningAsync(timeout.Token, explicitStart: true);
            SetNotice("代理已就绪");
            await DesktopClientLauncher.OpenAsync(accountPanel: false, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        { OperationNotice("请检查代理状态及本地构建；代理启动与控制图标可能有一项尚未就绪。"); }
        finally { busy = false; Scene.SetBusy(false); }
    }
    private void SetNotice(string value) => Scene.SetNotice(value);
    private void OperationNotice(string value)
    {
        if (lifetime.IsCancellationRequested) return;
        SetNotice(value);
        if (!IsVisible) NotifyTray("Codex Pool", value);
    }
    private void CycleMode(object sender, RoutedEventArgs e)
    {
        preferences = preferences with { Mode = (preferences.Mode + 1) % 3 };
        if (snapshot is { } state) Render(state);
        SavePosition();
    }
    private void CycleTheme(object sender, RoutedEventArgs e) { preferences = preferences with { Theme = lightTheme ? 2 : 1 }; ApplyTheme(); if (snapshot is { } state) Render(state); SavePosition(); }
    private void DismissWindow(object sender, RoutedEventArgs e) { SavePosition(); Hide(); }
    private async void OpenDashboard(object sender, RoutedEventArgs e) => await DashboardAsync();
    private async void RefreshQuota(object sender, RoutedEventArgs e) => await RefreshQuotaAsync(notify: true);

    private void ShowGlassTrayMenu(Forms.MouseEventArgs e)
    {
        glassMenu?.Close();
        System.Drawing.Rectangle? bounds = null;
        if (tray?.TryGetBounds(out var rect) == true) bounds = rect;
        var cursor = Forms.Cursor.Position;
        glassMenu = new GlassTrayMenu(BuildTrayMenu(), () => glassMenu = null, preferences.Theme) { Title = "Codex Pool · 账户与浮窗" };
        glassMenu.ShowNear(cursor, bounds);
    }

    private void ShowSceneMenu(System.Drawing.Point point)
    {
        glassMenu?.Close();
        glassMenu = new GlassTrayMenu(BuildTrayMenu(), () => glassMenu = null, preferences.Theme);
        glassMenu.ShowNear(point, null);
    }

    internal IReadOnlyList<GlassMenuItem> BuildTrayMenu()
    {
        var items = new List<GlassMenuItem>
        {
            new(IsVisible ? "隐藏账户面板" : "显示账户面板", () => ToggleVisibility()),
            new(Pinned ? "取消固定面板" : "固定面板", () => { if (!IsVisible) ShowFromLauncher(); TogglePinned(); }),
            new("打开账户管理与用量", () => _ = DashboardAsync(), Enabled: !busy),
            new("刷新额度", () => _ = RefreshQuotaAsync(notify: true), Enabled: !busy),
            new(connected ? "打开代理控制" : "启动代理", () => _ = OpenProxyAsync(), Enabled: !busy),
            new("任务栏图标显示设置", () =>
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }
                catch { SetNotice("请打开 Windows 设置 → 个性化 → 任务栏 → 其他系统托盘图标。"); }
            }),
            new("退出账户面板（代理继续运行）", () => { exiting = true; Close(); }),
        };
        if (snapshot is { } state)
        {
            items.Insert(0, AccountTrayMenu.Accounts(state, id => { if (startServices && !busy && connected) _ = ActivateAsync(id); }, connected && !busy));
            items.Insert(1, new(state.GetProperty("automaticSwitching").GetBoolean() ? "关闭自动切换" : "开启自动切换",
                () => _ = ToggleSwitchAsync(), Enabled: connected && !busy));
        }
        return items;
    }

    internal void TogglePinned()
    {
        if (Pinned) SavePosition();
        preferences = preferences with { Pinned = !Pinned };
        Scene.SetPinned(Pinned);
        if (Pinned) { flyoutScreen = null; ConstrainPinnedScene(IsVisible); SavePosition(); }
        else { ShowFlyout(); SavePosition(); }
    }
    private bool IsTrayPress() => (GetAsyncKeyState(1) & 0x8000) != 0
        && tray?.TryGetBounds(out var bounds) == true && bounds.Contains(Forms.Cursor.Position);
    internal void DismissOnDeactivation(bool trayPress = false)
    {
        if (Pinned) return;
        // The same tray press deactivates the popup before NIN_SELECT arrives.
        // Let that selection close it once, instead of hiding and reopening it.
        if (trayPress) { trayClickCloses = true; trayDismissTimer.Start(); return; }
        trayDismissTimer.Stop(); SavePosition(); Hide();
    }
    internal void HandleTrayPress() => trayClickCloses = IsVisible;
    internal void CompleteTrayPress(bool leftPressed, bool active)
    {
        if (leftPressed) return;
        trayDismissTimer.Stop();
        if (!Pinned && !active) { SavePosition(); Hide(); }
    }
    internal void HandleTrayClick(System.Drawing.Point point, bool keyboard = false)
    {
        trayDismissTimer.Stop(); glassMenu?.Close();
        var close = !keyboard && trayClickCloses; trayClickCloses = false;
        if (close || IsVisible) { SavePosition(); Hide(); }
        else ShowFromLauncher();
    }
    private void ToggleVisibility() { if (IsVisible) { SavePosition(); Hide(); } else ShowFromLauncher(); }
    private void ShowPinned()
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        var reveal = PrepareNativeEntrance();
        Show(); ClampPosition(); BeginNativeEntrance(reveal); Activate();
    }
    private void ShowFlyout()
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        // All entry points use the same icon anchor, independent of Explorer's click coordinates.
        if (tray?.TryGetBounds(out var iconBounds) == true) flyoutAnchor = iconBounds;
        else flyoutAnchor = new System.Drawing.Rectangle(Forms.Cursor.Position, System.Drawing.Size.Empty);
        flyoutScreen = Forms.Screen.FromPoint(new System.Drawing.Point(flyoutAnchor.Value.Left + flyoutAnchor.Value.Width / 2, flyoutAnchor.Value.Top + flyoutAnchor.Value.Height / 2));
        ConfigureHost(); PositionFlyout();
        var reveal = PrepareNativeEntrance();
        Show(); BeginNativeEntrance(reveal); Activate();
    }
    private bool PrepareNativeEntrance()
    {
        var opening = !IsVisible;
        Scene.PrepareEntrance();
        // Showing a reused layered HWND can expose its previous bitmap before
        // WPF submits the new (transparent) entrance frame.
        // Scene-only preview tests deliberately keep the host invisible. Once an
        // actual host has entered, remember that across an interrupted open.
        if (opening && Opacity > 0) nativeEntranceEnabled = true;
        var nativeHost = startServices || nativeEntranceEnabled;
        var guard = opening && Scene.EntranceProgress == 0 && nativeHost;
        if (guard)
        {
            // Window.Opacity is submitted by WPF asynchronously. Cloak the HWND
            // before Show so DWM cannot expose the previously cached bitmap.
            SetEntranceCloak(true);
            Opacity = 0;
        }
        else if (opening && nativeHost) Opacity = 1; // No fade under high contrast / reduced motion.
        return guard;
    }
    private void BeginNativeEntrance(bool guard)
    {
        if (!guard) { if (entranceReveal is null) Scene.BeginEntrance(); return; }
        CancelEntranceReveal();
        var frames = 0;
        TimeSpan? lastFrame = null;
        EventHandler? onFrame = null;
        onFrame = (_, args) =>
        {
            if (!IsVisible) { CancelEntranceReveal(); return; }
            var frameTime = ((RenderingEventArgs)args).RenderingTime;
            if (lastFrame == frameTime) return;
            lastFrame = frameTime;
            // Rendering fires before each frame is submitted. Leave one complete
            // transparent host frame in place before revealing the native window.
            if (++frames < 2) return;
            CancelEntranceReveal();
            Opacity = 1;
            Scene.BeginEntrance();
            SetEntranceCloak(false);
        };
        entranceReveal = onFrame;
        CompositionTarget.Rendering += onFrame;
    }
    private void SetEntranceCloak(bool value)
    {
        if (entranceCloaked == value || handle == 0) return;
        var cloak = value ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 13 /* DWMWA_CLOAK */, ref cloak, sizeof(int)) == 0)
            entranceCloaked = value;
    }
    private void CancelEntranceReveal()
    {
        if (entranceReveal is not { } handler) return;
        CompositionTarget.Rendering -= handler;
        entranceReveal = null;
    }
    private void ConfigureHost()
    {
        // Stable invisible host covers the largest supported scene on this monitor.
        // The visible border, tint and content are all rendered inside PoolScene.
        var screen = flyoutScreen ?? (handle != 0 ? Forms.Screen.FromHandle(handle) : Forms.Screen.PrimaryScreen!);
        var dpi = VisualTreeHelper.GetDpi(this); var area = screen.WorkingArea;
        Width = Math.Min(620, area.Width / dpi.DpiScaleX); Height = Math.Min(760, Math.Max(100, area.Height / dpi.DpiScaleY - 24));
        UpdateLayout();
        if (snapshot is { } state) Render(state);
        else Scene.SetState([], preferences.Mode, connected, false, IsVisible);
    }
    private void PositionFlyout()
    {
        if (Pinned || positioningFlyout || handle == 0) return;
        positioningFlyout = true;
        try
        {
            var area = (flyoutScreen ?? Forms.Screen.FromPoint(Forms.Cursor.Position)).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            var anchor = flyoutAnchor ?? new System.Drawing.Rectangle(Forms.Cursor.Position, System.Drawing.Size.Empty);
            Scene.AtTop = anchor.Top - area.Top < area.Bottom - anchor.Bottom;
            if (!GetWindowRect(handle, out var rect)) return;
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            var center = anchor.Left + anchor.Width / 2;
            var x = Math.Clamp(center - width / 2, area.Left, Math.Max(area.Left, area.Right - width));
            var gap = (int)Math.Round(12 * dpi.DpiScaleY);
            var y = Scene.AtTop ? Math.Min(area.Bottom - height, anchor.Bottom + gap - (int)(10 * dpi.DpiScaleY))
                : Math.Max(area.Top, anchor.Top - gap - height + (int)(10 * dpi.DpiScaleY));
            Scene.HorizontalAnchor = Math.Clamp((center - x) / (double)Math.Max(1, width), 0, 1);
            _ = SetWindowPos(handle, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
            Scene.ResetHostOffset();
        }
        finally { positioningFlyout = false; }
    }
    private void RestorePosition()
    {
        if (handle == 0) return;
        UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(this); var bounds = Scene.SurfaceBounds;
        var left = preferences.X - (int)Math.Round((preferences.PositionVersion == 2 ? bounds.Left + bounds.Width / 2 : bounds.Left) * dpi.DpiScaleX);
        var bottom = preferences.PositionVersion >= 1 ? preferences.Y : preferences.Y + (int)Math.Round(bounds.Height * dpi.DpiScaleY);
        var top = bottom - (int)Math.Round(bounds.Bottom * dpi.DpiScaleY);
        _ = SetWindowPos(handle, 0, left, top, 0, 0, 0x0001 | 0x0004 | 0x0010); ClampPosition();
    }
    private void ClampPosition()
    {
        if (!Pinned) { PositionFlyout(); return; }
        ConstrainPinnedScene(false);
    }
    private void ConstrainPinnedScene(bool animate)
    {
        if (!Pinned || Scene.ActualWidth <= 0 || Scene.ActualHeight <= 0) return;
        if (handle == 0 || !GetWindowRect(handle, out var rect)) return;
        var dpi = VisualTreeHelper.GetDpi(this); var visible = Scene.SurfaceBounds;
        var visibleRect = new System.Drawing.Rectangle(rect.Left + (int)(visible.Left * dpi.DpiScaleX), rect.Top + (int)(visible.Top * dpi.DpiScaleY),
            (int)(visible.Width * dpi.DpiScaleX), (int)(visible.Height * dpi.DpiScaleY));
        var area = Forms.Screen.FromRectangle(visibleRect).WorkingArea;
        var target = Scene.TargetSurfaceBounds;
        var left = rect.Left + target.Left * dpi.DpiScaleX; var top = rect.Top + target.Top * dpi.DpiScaleY;
        var x = rect.Left + (int)Math.Round(Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - target.Width * dpi.DpiScaleX)) - left);
        var y = rect.Top + (int)Math.Round(Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - target.Height * dpi.DpiScaleY)) - top);
        if (x == rect.Left && y == rect.Top) return;
        if (SetWindowPos(handle, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010) && GetWindowRect(handle, out var moved))
            Scene.RebaseHost((moved.Left - rect.Left) / dpi.DpiScaleX, (moved.Top - rect.Top) / dpi.DpiScaleY, animate);
    }
    private void SavePosition()
    {
        if (!startServices || handle == 0 || !GetWindowRect(handle, out var rect)) return;
        if (Pinned)
        {
            var dpi = VisualTreeHelper.GetDpi(this); var bounds = Scene.TargetSurfaceBounds;
            preferences = preferences with { X = rect.Left + (int)Math.Round((bounds.Left + bounds.Width / 2) * dpi.DpiScaleX),
                Y = rect.Top + (int)Math.Round(bounds.Bottom * dpi.DpiScaleY), PositionVersion = 2 };
        }
        try { Directory.CreateDirectory(directory); var path = Path.Combine(directory, PreferencesFile); File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(preferences)); File.Move(path + ".tmp", path, true); } catch { }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePosition(); if (startServices && !exiting) { e.Cancel = true; Hide(); return; }
        CancelEntranceReveal(); SetEntranceCloak(false); Scene.Stop(); lifetime.Cancel(); timer.Stop(); trayDismissTimer.Stop(); SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        _ = UnregisterHotKey(handle, 1);
        tray?.Dispose();
        http.Dispose();
        if (startServices) System.Windows.Application.Current.Shutdown();
    }
    private static System.Drawing.Icon CreateIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(24, 33, 48));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        using var stroke = new System.Drawing.Pen(System.Drawing.Color.FromArgb(142, 186, 255), 3.5f)
            { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        graphics.DrawArc(stroke, 7, 7, 18, 18, 45, 270);
        var iconHandle = bitmap.GetHicon();
        try { using var borrowed = System.Drawing.Icon.FromHandle(iconHandle); return (System.Drawing.Icon)borrowed.Clone(); }
        finally { _ = DestroyIcon(iconHandle); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
