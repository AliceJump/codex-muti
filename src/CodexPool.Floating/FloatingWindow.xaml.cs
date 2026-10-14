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

namespace CodexPool.Floating;

public sealed record WindowPreferences(int X = 80, int Y = 80, int Mode = 0, int Theme = 0, bool Pinned = false, int PositionVersion = 0);
public partial class FloatingWindow : Window
{
    private readonly string directory = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPool");
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly StableTrayIcon? tray;
    private GlassTrayMenu? glassMenu;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer trayDismissTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private WindowPreferences preferences = new();
    private JsonElement? snapshot;
    private bool exiting, busy, connected;
    private readonly bool startServices;
    private const string PreferencesFile = "widget.json";
    private Forms.Screen? flyoutScreen;
    private System.Drawing.Rectangle? flyoutAnchor;
    private bool positioningFlyout;
    private string? trayStateKey;
    private string? lastActiveAccountId;
    private bool switchNotifyReady;
    private bool trayClickCloses;
    private nint handle;
    internal bool Pinned => preferences.Pinned;
    public FloatingWindow() : this(true) { }
    internal FloatingWindow(bool startServices)
    {
        this.startServices = startServices;
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
        trayDismissTimer.Tick += (_, _) =>
        {
            if ((GetAsyncKeyState(1) & 0x8000) != 0) return;
            trayDismissTimer.Stop();
            if (!Pinned && !IsActive) { SavePosition(); Hide(); }
        };
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
        var light = preferences.Theme == 1 || preferences.Theme == 0 && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int value && value == 1;
        Scene.SetTheme(light, SystemParameters.HighContrast);
    }
    private async Task WatchAsync()
    {
        try { await ProxyStartup.EnsureRunningAsync(lifetime.Token); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { SetNotice("代理启动未完成 · 正在重新连接"); }
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                http.DefaultRequestHeaders.Authorization = new("Bearer", Vault.ReadLocalKey(directory));
                using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:3210/events");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token); response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(lifetime.Token));
                while (await reader.ReadLineAsync(lifetime.Token) is { } line)
                {
                    if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                    using var document = JsonDocument.Parse(line[6..]); var state = document.RootElement.Clone();
                    await Dispatcher.InvokeAsync(() => { connected = true; Render(state); });
                }
                if (!lifetime.IsCancellationRequested) await Dispatcher.InvokeAsync(SetOffline);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException or OperationCanceledException)
            { if (!lifetime.IsCancellationRequested) await Dispatcher.InvokeAsync(SetOffline); }
            if (!lifetime.IsCancellationRequested) try { await Task.Delay(1500, lifetime.Token); } catch (OperationCanceledException) { }
        }
    }
    private void SetOffline()
    {
        connected = false;
        if (snapshot is { } state) Render(state);
        else { UpdateTrayQuota(null); Scene.SetState([], preferences.Mode, false, false, IsVisible); }
    }
    internal async void ShowFromLauncher()
    {
        if (Pinned) ShowPinned(); else ShowFlyout();
        if (!startServices) return;
        try { await ProxyStartup.EnsureRunningAsync(lifetime.Token); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { SetNotice("代理未能启动，请检查本地服务。"); }
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
        tray.Icon = icon; tray.Text = tooltip; trayStateKey = key;
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
            using var response = await http.PostAsJsonAsync("http://127.0.0.1:3210/accounts/" + id + "/activate", new { }, timeout.Token);
            if (!response.IsSuccessStatusCode) { SetNotice("账户切换未完成。请在 Dashboard 检查账户状态。"); }
            else { Scene.SetNotice(""); }
            using var status = await http.GetAsync("http://127.0.0.1:3210/status", timeout.Token); status.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(timeout.Token)); Render(json.RootElement.Clone());
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { SetNotice("服务暂时不可用。账户选择尚未确认。"); if (snapshot is { } state) Render(state); }
        finally { busy = false; Scene.SetBusy(false); }
    }
    private async Task RefreshQuotaAsync(bool notify)
    {
        if (busy) return;
        busy = true; Scene.SetBusy(true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var usage = await http.GetAsync("http://127.0.0.1:3210/usage", timeout.Token); usage.EnsureSuccessStatusCode();
            using var status = await http.GetAsync("http://127.0.0.1:3210/status", timeout.Token); status.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(timeout.Token)); Render(json.RootElement.Clone());
            if (notify)
            {
                SetNotice("额度已更新");
                NotifyTray("额度更新", "额度已更新");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        { SetNotice("额度刷新失败。请检查本地服务。"); }
        finally { busy = false; Scene.SetBusy(false); }
    }
    private void NotifyTray(string title, string text)
    {
        if (tray is null) return;
        try
        {
            tray.ShowBalloon(title, text);
        }
        catch { }
    }
    private async Task DashboardAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await http.PostAsync("http://127.0.0.1:3210/dashboard/session", null, timeout.Token); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token)); var url = json.RootElement.GetProperty("url").GetString()!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "127.0.0.1" || uri.Port != 3210 || uri.Scheme != "http" || !uri.AbsolutePath.StartsWith("/dashboard/open/", StringComparison.Ordinal)) return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { SetNotice("无法打开 Dashboard，请先启动 Proxy。"); }
    }
    private void SetNotice(string value) => Scene.SetNotice(value);
    private void CycleMode(object sender, RoutedEventArgs e)
    {
        preferences = preferences with { Mode = (preferences.Mode + 1) % 3 };
        if (snapshot is { } state) Render(state);
        SavePosition();
    }
    private void CycleTheme(object sender, RoutedEventArgs e) { preferences = preferences with { Theme = (preferences.Theme + 1) % 3 }; ApplyTheme(); if (snapshot is { } state) Render(state); SavePosition(); }
    private void DismissWindow(object sender, RoutedEventArgs e) { SavePosition(); Hide(); }
    private async void OpenDashboard(object sender, RoutedEventArgs e) => await DashboardAsync();
    private async void RefreshQuota(object sender, RoutedEventArgs e) => await RefreshQuotaAsync(notify: true);

    private void ShowGlassTrayMenu(Forms.MouseEventArgs e)
    {
        glassMenu?.Close();
        System.Drawing.Rectangle? bounds = null;
        if (tray?.TryGetBounds(out var rect) == true) bounds = rect;
        var cursor = Forms.Cursor.Position;
        var items = new (string, Action)[]
        {
            ("显示账户面板", () => ToggleVisibility()),
            (Pinned ? "取消固定面板" : "固定面板", () => { if (!IsVisible) ShowFromLauncher(); TogglePinned(); }),
            ("打开 Dashboard", () => _ = DashboardAsync()),
            ("刷新额度", () => _ = RefreshQuotaAsync(notify: true)),
            ("任务栏图标显示设置", () =>
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }
                catch { SetNotice("请打开 Windows 设置 → 个性化 → 任务栏 → 其他系统托盘图标。"); }
            }),
            ("退出托盘面板", () => { exiting = true; Close(); }),
        };
        var menuItems = items.Select(x => new GlassMenuItem(x.Item1, x.Item2)).ToList();
        if (connected && snapshot is { } state)
            menuItems.Insert(0, ProxyTrayController.AccountMenu(state, id => { if (startServices && !busy) _ = ActivateAsync(id); }));
        glassMenu = new GlassTrayMenu(menuItems, () => glassMenu = null, preferences.Theme);
        glassMenu.ShowNear(cursor, bounds);
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
    internal void HandleTrayClick(System.Drawing.Point point, bool keyboard = false)
    {
        trayDismissTimer.Stop(); glassMenu?.Close();
        var close = !keyboard && trayClickCloses; trayClickCloses = false;
        if (close) { SavePosition(); Hide(); } else ToggleVisibility(point);
    }
    private void ToggleVisibility(System.Drawing.Point? clicked = null) { if (IsVisible) { SavePosition(); Hide(); } else if (Pinned) ShowPinned(); else ShowFlyout(clicked); }
    private void ShowPinned()
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        Scene.PrepareEntrance(); Show(); ClampPosition(); Scene.BeginEntrance(); Activate();
    }
    private void ShowFlyout(System.Drawing.Point? clicked = null)
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        if (tray?.TryGetBounds(out var iconBounds) == true && (clicked is null || iconBounds.Contains(clicked.Value))) flyoutAnchor = iconBounds;
        else flyoutAnchor = new System.Drawing.Rectangle(clicked ?? Forms.Cursor.Position, System.Drawing.Size.Empty);
        flyoutScreen = Forms.Screen.FromPoint(new System.Drawing.Point(flyoutAnchor.Value.Left + flyoutAnchor.Value.Width / 2, flyoutAnchor.Value.Top + flyoutAnchor.Value.Height / 2));
        ConfigureHost(); PositionFlyout(); Scene.PrepareEntrance(); Show(); Scene.BeginEntrance(); Activate();
        if (snapshot is { } state) Render(state);
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
        Scene.Stop(); lifetime.Cancel(); timer.Stop(); trayDismissTimer.Stop(); SystemEvents.UserPreferenceChanged -= PreferenceChanged;
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
