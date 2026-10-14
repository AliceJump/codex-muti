using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexPool.Daemon;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;
using Brush = System.Windows.Media.SolidColorBrush;
using SystemColors = System.Windows.SystemColors;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Orientation = System.Windows.Controls.Orientation;

namespace CodexPool.Floating;

public sealed record WindowPreferences(int X = 80, int Y = 80, int Mode = 0, int Theme = 0, bool Pinned = true);
public partial class FloatingWindow : Window
{
    private readonly string directory = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPool");
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly StableTrayIcon? tray;
    private GlassTrayMenu? glassMenu;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private WindowPreferences preferences = new();
    private JsonElement? snapshot;
    private bool exiting, busy, backdrop, connected;
    private readonly bool widget;
    private readonly string preferencesFile;
    private Forms.Screen? flyoutScreen;
    private System.Drawing.Rectangle? flyoutAnchor;
    private bool positioningFlyout;
    private string? trayStateKey;
    private string? lastActiveAccountId;
    private bool switchNotifyReady;
    private long dismissedAt;
    private System.Drawing.Point dismissedPoint;
    private nint handle;
    private readonly ScaleTransform revealScale = new(1, 1);
    private readonly TranslateTransform revealOffset = new();
    private int shapeGeneration;
    private bool changingShape;
    private bool preparingShape;
    private bool pendingState;
    private bool backdropSuspended;
    private IDisposable? shapeFrames;
    private readonly Dictionary<string, AccountElements> accountElements = new();
    private IEnumerable<FrameworkElement> SharedParts => accountElements.Values.SelectMany(x => x.Parts)
        .Concat(new FrameworkElement[] { Header, Footer, WidgetSummary });
    public FloatingWindow(bool widget = false)
    {
        this.widget = widget;
        preferencesFile = widget ? "widget.json" : "window.json";
        InitializeComponent();
        var reveal = new TransformGroup(); reveal.Children.Add(revealScale); reveal.Children.Add(revealOffset);
        Surface.RenderTransform = reveal;
        if (widget) preferences = new(120, 120, Pinned: false);
        try { preferences = JsonSerializer.Deserialize<WindowPreferences>(File.ReadAllText(Path.Combine(directory, preferencesFile))) ?? preferences; } catch { }
        preferences = preferences with { Mode = Math.Clamp(preferences.Mode, 0, 2), Theme = Math.Clamp(preferences.Theme, 0, 2) };
        Topmost = widget || preferences.Pinned;
        Title = widget ? "Codex Pool · 托盘面板" : "Codex Pool";
        WidgetSummary.Visibility = widget ? Visibility.Visible : Visibility.Collapsed;
        WidgetActions.Visibility = widget ? Visibility.Visible : Visibility.Collapsed;
        PinButton.Visibility = Visibility.Collapsed;
        DismissButton.Content = widget ? "—" : "×";
        DismissButton.ToolTip = widget ? "隐藏到账户托盘" : "关闭悬浮窗（代理继续运行）";
        HeaderTitle.Text = "Codex Pool";
        PinButton.Content = Topmost ? "●" : "○";
        var shortcut = widget ? "Ctrl+Alt+W" : "Ctrl+Alt+C";
        // One tray icon only: owned by --tray / widget. Floating window reuses that entry via menu.
        if (widget)
        {
            tray = new StableTrayIcon(CreateIcon(), "Codex Pool · 账户状态 · " + shortcut);
            UpdateTrayQuota(null);
            tray.ContextMenuRequested += (_, e) => Dispatcher.Invoke(() => ShowGlassTrayMenu(e));
            tray.MouseClick += (_, e) =>
            {
                if (e.Button != Forms.MouseButtons.Left) return;
                Dispatcher.Invoke(() =>
                {
                    // Explorer can take focus before delivering the icon click. That same click
                    // should dismiss the visible flyout, rather than reopen it after Deactivated.
                    var cursor = Forms.Cursor.Position;
                    if (Environment.TickCount64 - dismissedAt < 500 && Math.Abs(cursor.X - dismissedPoint.X) <= 2 && Math.Abs(cursor.Y - dismissedPoint.Y) <= 2) return;
                    glassMenu?.Close();
                    ToggleVisibility(new System.Drawing.Point(e.X, e.Y));
                });
            };
        }
        else tray = null;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.M && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            { CycleMode(this, new RoutedEventArgs()); e.Handled = true; }
        };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle); source?.AddHook(WindowMessage);
            if (source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            var acrylic = 3; backdrop = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
                && DwmSetWindowAttribute(handle, 38, ref acrylic, 4) == 0;
            var corners = 2; _ = DwmSetWindowAttribute(handle, 33, ref corners, 4);
            var registered = RegisterHotKey(handle, 1, 0x0001 | 0x0002 | 0x4000, widget ? 0x57u : 0x43u);
            if (!registered) SetNotice(shortcut + " 已被其他应用使用。" + (widget ? "可从托盘显示窗口。" : "可再次点击悬浮窗快捷方式显示。"));
            ApplyTheme(); if (!widget) RestorePosition(); _ = WatchAsync(); timer.Start();
        };
        if (widget)
        {
            Deactivated += (_, _) => { dismissedAt = Environment.TickCount64; dismissedPoint = Forms.Cursor.Position; Hide(); };
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Hide(); e.Handled = true; } };
            SizeChanged += (_, _) => { if (IsVisible && !changingShape) PositionFlyout(); };
        }
        timer.Tick += (_, _) => { SavePosition(); if (snapshot is { } s) Render(s); };
        Closing += OnClosing;
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        ApplyTheme();
    }
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => { ApplyTheme(); if (snapshot is { } state) Render(state); });
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == 1) { ToggleVisibility(); handled = true; }
        if (message is 0x007e or 0x02e0) Dispatcher.BeginInvoke(ClampPosition);
        return 0;
    }
    private void ApplyTheme()
    {
        var light = preferences.Theme == 1 || preferences.Theme == 0 && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int value && value == 1;
        var highContrast = SystemParameters.HighContrast;
        Resources["ForegroundBrush"] = highContrast ? SystemColors.WindowTextBrush : new Brush(light ? MediaColor.FromRgb(26, 38, 53) : MediaColor.FromRgb(237, 242, 249));
        Resources["SurfaceBrush"] = highContrast ? SystemColors.WindowBrush : new Brush(light ? MediaColor.FromArgb(backdrop ? (byte)190 : (byte)255, 245, 248, 253) : MediaColor.FromArgb(backdrop ? (byte)185 : (byte)255, 24, 30, 39));
        Resources["EdgeBrush"] = new Brush(light ? MediaColor.FromArgb(70, 130, 145, 165) : MediaColor.FromArgb(70, 170, 187, 210));
        var dark = light ? 0 : 1; if (handle != 0) _ = DwmSetWindowAttribute(handle, 20, ref dark, 4);
    }
    private async Task WatchAsync()
    {
        try { await ProxyStartup.EnsureRunningAsync(lifetime.Token); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { Connection.Text = "代理启动未完成 · 正在重新连接"; }
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
                    await Dispatcher.InvokeAsync(() => { connected = true; Connection.Text = "● 服务在线 · Web 已启用"; Render(state); });
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
        connected = false; Connection.Text = "服务离线 · 正在重新连接"; Accounts.IsEnabled = false;
        if (snapshot is { } state) Render(state);
        else if (widget) UpdateTrayQuota(null);
    }
    internal async void ShowFromLauncher()
    {
        if (widget) ShowFlyout(); else { Show(); ClampPosition(); Activate(); }
        try { await ProxyStartup.EnsureRunningAsync(lifetime.Token); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { SetNotice("代理未能启动，请检查本地服务。"); }
    }
    private void Render(JsonElement state)
    {
        if (changingShape && IsVisible) { snapshot = state; pendingState = true; return; }
        if (changingShape) StopShapeAnimation();
        foreach (var elements in accountElements.Values) elements.Detach();
        if (widget) UpdateTrayQuota(connected ? state : null);
        NotifyActiveSwitch(state);
        snapshot = state; Accounts.IsEnabled = !busy && connected; Accounts.Children.Clear();
        var capsule = preferences.Mode == 2;
        if (!capsule) SetLayoutWidth(widget ? 380 : 336);
        Header.Visibility = capsule ? Visibility.Collapsed : Visibility.Visible;
        Surface.Padding = capsule ? new Thickness(6, 5, 6, 5) : new Thickness(18, 14, 18, 14);
        Surface.CornerRadius = new CornerRadius(capsule ? 20 : 16);
        Surface.ToolTip = capsule ? (connected ? "额度为剩余百分比" : "服务离线 · 正在重新连接")
            + (string.IsNullOrEmpty(Notice.Text) ? "" : "\n" + Notice.Text)
            + "\nCtrl+M 切换布局；拖动空白区域移动悬浮窗" : null;
        Footer.Visibility = capsule ? Visibility.Collapsed : Visibility.Visible;
        if (capsule) Notice.Visibility = Visibility.Collapsed;
        else if (!string.IsNullOrEmpty(Notice.Text)) Notice.Visibility = Visibility.Visible;
        WidgetSummary.Visibility = widget && !capsule ? Visibility.Visible : Visibility.Collapsed;
        AutoStatus.Text = state.GetProperty("automaticSwitching").GetBoolean() ? "自动切换 · 开启" : "自动切换 · 关闭";
        if (widget)
        {
            var activeAccount = state.GetProperty("accounts").EnumerateArray().FirstOrDefault(x => x.GetProperty("active").GetBoolean());
            WidgetActive.Text = activeAccount.ValueKind == JsonValueKind.Object ? activeAccount.GetProperty("name").GetString() : "尚未选择账户";
            var enabled = state.GetProperty("accounts").EnumerateArray().Count(x => x.GetProperty("enabled").GetBoolean());
            var running = state.GetProperty("inFlightRequests").EnumerateArray().Sum(x => x.GetProperty("value").GetInt32());
            WidgetDetail.Text = enabled + " 个启用账户 · " + (running == 0 ? "当前没有进行中的请求" : running + " 个请求处理中");
        }
        var edge = (System.Windows.Media.Brush)Resources["EdgeBrush"];
        var accent = MediaColor.FromRgb(103, 162, 243);
        var wrap = new WrapPanel { ItemHeight = double.NaN };
        if (capsule)
        {
            Accounts.Children.Add(wrap);
        }
        foreach (var account in state.GetProperty("accounts").EnumerateArray())
        {
            if ((widget || capsule) && !account.GetProperty("enabled").GetBoolean() && !account.GetProperty("active").GetBoolean()) continue;
            var id = account.GetProperty("id").GetString()!; var active = account.GetProperty("active").GetBoolean();
            var name = account.GetProperty("name").GetString() ?? "Account"; var auth = account.GetProperty("auth").GetString();
            if (!accountElements.TryGetValue(id, out var shared))
            {
                shared = new AccountElements(); accountElements[id] = shared;
                var accountId = id;
                shared.Card.MouseLeftButtonUp += async (_, e) =>
                {
                    if (busy || !connected || snapshot is not { } current) return;
                    var selected = current.GetProperty("accounts").EnumerateArray().FirstOrDefault(x => x.GetProperty("id").GetString() == accountId);
                    if (selected.ValueKind != JsonValueKind.Object || selected.GetProperty("active").GetBoolean()) return;
                    e.Handled = true; await ActivateAsync(accountId);
                };
            }
            var usage = state.GetProperty("usage").EnumerateArray().FirstOrDefault(x => x.GetProperty("accountId").GetString() == id);
            var quotaWindows = QuotaDisplay.Windows(usage);
            var keys = quotaWindows.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in shared.Windows.Keys.Where(key => !keys.Contains(key)).ToArray()) shared.Windows.Remove(key);
            var canActivate = account.GetProperty("enabled").GetBoolean() && !account.GetProperty("paused").GetBoolean() && !account.GetProperty("drain").GetBoolean()
                && account.GetProperty("state").GetString() != "cooldown" && auth is not ("reauth_required" or "refresh_pending");
            if (capsule)
            {
                var status = !connected ? "离线" : account.GetProperty("paused").GetBoolean() ? "暂停"
                    : account.GetProperty("drain").GetBoolean() ? "排空"
                    : auth == "reauth_required" ? "需登录" : auth == "refresh_pending" ? "认证中"
                    : !canActivate ? AccountState(account.GetProperty("state").GetString()) : null;
                wrap.Children.Add(CapsuleChip(name, quotaWindows, QuotaDisplay.State(usage), active, canActivate, status, accent, edge, shared));
                continue;
            }
            var body = new StackPanel { Margin = new Thickness(0, 0, active ? 18 : 0, 0) };
            shared.Name.Text = name; shared.Name.FontSize = 13; shared.Name.MaxWidth = double.PositiveInfinity;
            shared.Name.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            shared.Name.Foreground = (System.Windows.Media.Brush)Resources["ForegroundBrush"];
            shared.Name.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(shared.Name);
            if (preferences.Mode == 0)
            {
                var status = account.GetProperty("state").GetString();
                var plan = account.GetProperty("plan").ValueKind == JsonValueKind.String ? account.GetProperty("plan").GetString() : "Plan 待确认";
                shared.Plan.Text = plan + " · " + AccountState(status); shared.Plan.FontSize = 11; shared.Plan.Opacity = .55;
                shared.Plan.Foreground = (System.Windows.Media.Brush)Resources["ForegroundBrush"];
                shared.Plan.Margin = new Thickness(0, 4, 0, 2); body.Children.Add(shared.Plan);
            }
            foreach (var window in quotaWindows) body.Children.Add(QuotaBar(window, shared.Window(window)));
            if (quotaWindows.Length == 0 || usage.TryGetProperty("credits", out var creditState) && creditState.ValueKind == JsonValueKind.Object)
                body.Children.Add(Label(QuotaDisplay.State(usage), 10, .6, new Thickness(0, 7, 0, 0)));
            if (preferences.Mode == 0 && state.TryGetProperty("tokenStatistics", out var statistics)
                && statistics.TryGetProperty("today", out var today) && today.TryGetProperty("accounts", out var tokenAccounts))
            {
                var tokenAccount = tokenAccounts.EnumerateArray().FirstOrDefault(x => x.GetProperty("accountId").GetString() == id);
                var totals = tokenAccount.ValueKind == JsonValueKind.Object ? tokenAccount.GetProperty("totals") : default;
                var requests = totals.ValueKind == JsonValueKind.Object ? totals.GetProperty("requests").GetInt64() : 0;
                var reported = totals.ValueKind == JsonValueKind.Object ? totals.GetProperty("reportedRequests").GetInt64() : 0;
                var amount = requests > 0 && reported == 0 ? "未报告" : totals.ValueKind == JsonValueKind.Object
                    ? totals.GetProperty("totalTokens").GetDecimal().ToString("N0") : "0";
                shared.Tokens.Text = "今日 " + amount + " Token · " + requests + " 个请求";
                shared.Tokens.ToolTip = "只统计本代理请求，上游已报告 " + reported + "/" + requests + " 个请求；详细统计见 Web 面板。";
                shared.Tokens.FontSize = 10; shared.Tokens.Opacity = .55;
                shared.Tokens.Foreground = (System.Windows.Media.Brush)Resources["ForegroundBrush"];
                shared.Tokens.Margin = new Thickness(0, 7, 0, 0); body.Children.Add(shared.Tokens);
            }
            if (preferences.Mode == 0 && usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("updatedAt", out var updated))
            {
                shared.Updated.Text = "更新 " + updated.GetDateTimeOffset().ToLocalTime().ToString("HH:mm:ss")
                    + (DateTimeOffset.UtcNow - updated.GetDateTimeOffset() > TimeSpan.FromMinutes(2) ? " · 已过期" : "");
                shared.Updated.FontSize = 10; shared.Updated.Opacity = .45;
                shared.Updated.Foreground = (System.Windows.Media.Brush)Resources["ForegroundBrush"];
                shared.Updated.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(shared.Updated);
            }
            var grid = new Grid();
            grid.Children.Add(body);
            if (active)
            {
                grid.Children.Add(new TextBlock
                {
                    Text = "✓",
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new Brush(accent),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -1, 0, 0)
                });
            }
            var card = shared.Card;
            card.Child = grid; card.Padding = new Thickness(12, 10, 12, 10); card.Margin = new Thickness(0, 0, 0, 10);
            card.CornerRadius = new CornerRadius(12); card.BorderThickness = new Thickness(1.2);
            card.BorderBrush = active ? new Brush(MediaColor.FromArgb(200, accent.R, accent.G, accent.B)) : edge;
            card.Background = new Brush(active ? MediaColor.FromArgb(48, accent.R, accent.G, accent.B) : MediaColor.FromArgb(32, 135, 150, 170));
            card.Cursor = canActivate || active ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
            card.IsEnabled = canActivate; card.Opacity = canActivate || active ? 1 : .55;
            Accounts.Children.Add(card);
        }
        if (capsule)
        {
            if (wrap.Children.Count == 0) wrap.Children.Add(Label("无可用账户", 12, .7, new Thickness(8, 4, 8, 4)));
            var screen = widget ? flyoutScreen ?? Forms.Screen.FromPoint(Forms.Cursor.Position) : Forms.Screen.FromHandle(handle);
            var workWidth = screen.WorkingArea.Width / VisualTreeHelper.GetDpi(this).DpiScaleX;
            var maxWidth = Math.Min(600, workWidth);
            var chromeWidth = Surface.Padding.Left + Surface.Padding.Right + 4;
            wrap.Measure(new System.Windows.Size(Math.Max(0, maxWidth - chromeWidth), double.PositiveInfinity));
            var capsuleWidth = Math.Ceiling(wrap.DesiredSize.Width + chromeWidth);
            SetLayoutWidth(Math.Min(maxWidth, capsuleWidth));
        }
        else if (Accounts.Children.Count == 0) Accounts.Children.Add(Label("没有启用的账户。请在 Web 面板添加或启用。", 12, .7, new Thickness(0, 12, 0, 12)));
        var accountIds = state.GetProperty("accounts").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToHashSet();
        foreach (var id in accountElements.Keys.Where(x => !accountIds.Contains(x)).ToArray()) accountElements.Remove(id);
    }
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
    private static string AccountState(string? state) => state switch
    {
        "active" => "使用中", "standby" => "待命", "paused" => "已暂停", "draining" => "排空中", "disabled" => "已禁用", "cooldown" => "冷却中", _ => "待确认"
    };
    private Border CapsuleChip(string name, DisplayQuotaWindow[] windows, string quotaState, bool active, bool canActivate, string? status, MediaColor accent, System.Windows.Media.Brush edge, AccountElements shared)
    {
        var detail = status ?? quotaState;
        var tooltip = name + "\n" + (windows.Length == 0 ? quotaState : string.Join("\n", windows.Select(w => w.Label + " " + QuotaDisplay.Percent(w) + " · " + ResetLeft(w.Window))))
            + (status is null ? "" : "\n" + status)
            + (string.IsNullOrEmpty(Notice.Text) ? "" : "\n" + Notice.Text) + "\nCtrl+M 切换布局";
        var chip = CapsuleLayout.Account(name, detail, tooltip, active, canActivate && connected,
            (System.Windows.Media.Brush)Resources["ForegroundBrush"], new Brush(accent), edge, shared,
            status is null ? windows : null);
        return chip;
    }
    private static MediaColor QuotaTone(double? remaining) => remaining switch
    {
        null => MediaColor.FromRgb(103, 162, 243),
        < 15 => MediaColor.FromRgb(255, 112, 105),
        < 30 => MediaColor.FromRgb(255, 198, 95),
        _ => MediaColor.FromRgb(103, 162, 243)
    };
    private static string ResetLeft(JsonElement window)
    {
        if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("resetAt", out var reset)
            || reset.ValueKind != JsonValueKind.String || !reset.TryGetDateTimeOffset(out var at)) return "重置时间未知";
        var span = at - DateTimeOffset.UtcNow;
        if (span <= TimeSpan.Zero) return "等待新额度";
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(0, (int)span.TotalMinutes)}m";
    }
    private static TextBlock Label(string? value, double size, double opacity, Thickness margin) => new() { Text = value, FontSize = size, Opacity = opacity, Margin = margin };
    private StackPanel QuotaBar(DisplayQuotaWindow window, WindowElements row)
    {
        var foreground = (System.Windows.Media.Brush)Resources["ForegroundBrush"];
        var heading = row.Heading; var value = row.Value; var reset = row.Reset; var track = row.Track;
        var root = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new()); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Text = window.Label; heading.ToolTip = window.Label; heading.MaxWidth = 120; heading.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.FontSize = 11; heading.Opacity = .65; heading.Foreground = foreground; heading.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(heading);
        var amount = new StackPanel { Orientation = Orientation.Horizontal };
        amount.Children.Add(Label("剩 ", 11, 1, new Thickness()));
        value.Text = QuotaDisplay.Percent(window); value.FontSize = 11; value.Opacity = 1; value.Foreground = foreground;
        amount.Children.Add(value); Grid.SetColumn(amount, 1); head.Children.Add(amount);
        reset.Text = window.Remaining is null ? "额度待确认" : ResetLeft(window.Window); reset.FontSize = 10; reset.Opacity = .55; reset.Foreground = foreground;
        Grid.SetColumn(reset, 2); head.Children.Add(reset); root.Children.Add(head);
        track.Height = 6; track.Minimum = 0; track.Maximum = 100; track.Value = window.Remaining ?? 0;
        track.Margin = new Thickness(0, 5, 0, 0); track.Foreground = new Brush(QuotaTone(window.Remaining));
        track.Background = new Brush(MediaColor.FromArgb(45, 135, 150, 170)); track.BorderThickness = new Thickness(0);
        root.Children.Add(track); return root;
    }
    private async Task ActivateAsync(string id)
    {
        busy = true; Accounts.IsEnabled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.PostAsJsonAsync("http://127.0.0.1:3210/accounts/" + id + "/activate", new { }, timeout.Token);
            if (!response.IsSuccessStatusCode) { SetNotice("账户切换未完成。请在 Dashboard 检查账户状态。"); }
            else { Notice.Text = ""; Notice.Visibility = Visibility.Collapsed; }
            using var status = await http.GetAsync("http://127.0.0.1:3210/status", timeout.Token); status.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(timeout.Token)); Render(json.RootElement.Clone());
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { SetNotice("服务暂时不可用。账户选择尚未确认。"); if (snapshot is { } state) Render(state); }
        finally { busy = false; Accounts.IsEnabled = connected; }
    }
    private async Task RefreshQuotaAsync(bool notify)
    {
        if (busy) return;
        busy = true; Accounts.IsEnabled = false;
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
        finally { busy = false; Accounts.IsEnabled = connected; }
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
    private void SetNotice(string value)
    {
        Notice.Text = value;
        Notice.Visibility = preferences.Mode == 2 ? Visibility.Collapsed : Visibility.Visible;
        if (preferences.Mode == 2) Surface.ToolTip = value + "\nCtrl+M 展开查看";
    }
    private void DragCapsule(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (preferences.Mode == 2 && ReferenceEquals(e.OriginalSource, Surface)) DragHeader(sender, e);
    }
    private void DragHeader(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (!widget && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove(); }
    private void SetLayoutWidth(double value)
    {
        if (preparingShape) Surface.Width = value;
        else Width = value;
    }
    private void CycleMode(object sender, RoutedEventArgs e)
    {
        if (changingShape) return;
        var previousWidth = ActualWidth; var previousHeight = ActualHeight;
        var previous = LayoutMotion.Capture(Surface, SharedParts);
        var cards = CardMotion.Capture(Surface, accountElements.Values.Select(x => x.Card));
        var animate = IsVisible && previousWidth > 0 && previousHeight > 0 && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        preferences = preferences with { Mode = (preferences.Mode + 1) % 3 };
        if (snapshot is { } state)
        {
            if (animate)
            {
                // Measure the final content before animating the native bounds.
                // Keep its layout fixed so text does not reflow on every resize frame.
                SizeToContent = SizeToContent.Manual; Width = previousWidth; Height = previousHeight;
                preparingShape = true;
                try { Render(state); } finally { preparingShape = false; }
                var targetWidth = Surface.Width;
                Surface.Measure(new System.Windows.Size(targetWidth, double.PositiveInfinity));
                var targetHeight = Surface.DesiredSize.Height;
                LayoutContent.Width = Math.Max(0, targetWidth - Surface.Padding.Left - Surface.Padding.Right - Surface.BorderThickness.Left - Surface.BorderThickness.Right);
                LayoutContent.Height = Math.Max(0, targetHeight - Surface.Padding.Top - Surface.Padding.Bottom - Surface.BorderThickness.Top - Surface.BorderThickness.Bottom);
                LayoutContent.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                LayoutContent.VerticalAlignment = VerticalAlignment.Top;
                Surface.Measure(new System.Windows.Size(targetWidth, targetHeight));
                Surface.Arrange(new System.Windows.Rect(0, 0, targetWidth, targetHeight));
                CardMotion.Play(Surface, MotionBackdrops, cards, new Duration(TimeSpan.FromMilliseconds(380)));
                LayoutMotion.Play(Surface, MotionLayer, previous, new Duration(TimeSpan.FromMilliseconds(380)), SharedParts);
                AnimateShape(previousWidth, previousHeight, targetWidth, targetHeight);
            }
            else { Render(state); UpdateLayout(); }
        }
        SavePosition();
    }
    private void StopShapeAnimation()
    {
        shapeGeneration++; changingShape = false;
        shapeFrames?.Dispose(); shapeFrames = null;
        MotionHost.Width = double.NaN; MotionHost.Height = double.NaN;
        MotionHost.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        MotionHost.VerticalAlignment = VerticalAlignment.Stretch;
        Surface.BeginAnimation(WidthProperty, null); Surface.BeginAnimation(HeightProperty, null);
        Surface.Width = double.NaN; Surface.Height = double.NaN;
        Surface.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        Surface.VerticalAlignment = VerticalAlignment.Stretch;
        LayoutContent.Width = double.NaN; LayoutContent.Height = double.NaN;
        LayoutContent.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        LayoutContent.VerticalAlignment = VerticalAlignment.Stretch;
        MotionLayer.Width = double.NaN; MotionLayer.Height = double.NaN;
        MotionLayer.Children.Clear(); CardMotion.Clear(MotionBackdrops);
        foreach (var part in SharedParts) part.RenderTransform = Transform.Identity;
        SizeToContent = SizeToContent.Height;
        if (backdropSuspended)
        {
            backdropSuspended = false;
            var acrylic = 3; _ = DwmSetWindowAttribute(handle, 38, ref acrylic, 4); ApplyTheme();
        }
    }
    private ClockGroup AnimateShape(double fromWidth, double fromHeight, double toWidth, double toHeight)
    {
        var generation = shapeGeneration; changingShape = true; pendingState = false;
        if (backdrop && handle != 0)
        {
            // Keep a solid surface during native resize to avoid acrylic redraw flashes.
            var none = 1; _ = DwmSetWindowAttribute(handle, 38, ref none, 4);
            backdropSuspended = true;
            if (Resources["SurfaceBrush"] is Brush background)
                Resources["SurfaceBrush"] = new Brush(MediaColor.FromRgb(background.Color.R, background.Color.G, background.Color.B));
        }
        SizeToContent = SizeToContent.Manual;
        Width = fromWidth; Height = fromHeight;
        // Native WM_SIZE must not change the coordinate system of the content.
        MotionHost.Width = Math.Max(fromWidth, toWidth); MotionHost.Height = Math.Max(fromHeight, toHeight);
        MotionHost.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        MotionHost.VerticalAlignment = VerticalAlignment.Top;
        Surface.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        Surface.VerticalAlignment = VerticalAlignment.Top;
        Surface.Width = toWidth; Surface.Height = toHeight;
        MotionLayer.Width = toWidth; MotionLayer.Height = toHeight;
        var duration = new Duration(TimeSpan.FromMilliseconds(380));
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var timeline = new ParallelTimeline { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
        timeline.Children.Add(new DoubleAnimation(fromWidth, toWidth, duration) { EasingFunction = ease });
        timeline.Children.Add(new DoubleAnimation(fromHeight, toHeight, duration) { EasingFunction = ease });
        var clock = (ClockGroup)timeline.CreateClock();
        clock.Completed += (_, _) =>
        {
            if (generation != shapeGeneration) return;
            Width = toWidth; Height = toHeight;
            StopShapeAnimation();
            if (pendingState && snapshot is { } latest) Render(latest);
            pendingState = false;
            UpdateLayout(); ClampPosition();
        };
        shapeFrames = ShapeMotion.Apply(this, Surface, clock, widget ? ShapePosition : null);
        return clock;
    }
    private void CycleTheme(object sender, RoutedEventArgs e) { preferences = preferences with { Theme = (preferences.Theme + 1) % 3 }; ApplyTheme(); if (snapshot is { } state) Render(state); SavePosition(); }
    private void DismissWindow(object sender, RoutedEventArgs e) { if (widget) { SavePosition(); Hide(); } else Close(); }
    private async void OpenDashboard(object sender, RoutedEventArgs e) => await DashboardAsync();
    private void OpenFloating(object sender, RoutedEventArgs e) => LaunchFloating();
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
            ("打开悬浮窗", LaunchFloating),
            ("打开 Dashboard", () => _ = DashboardAsync()),
            ("刷新额度", () => _ = RefreshQuotaAsync(notify: true)),
            ("任务栏图标显示设置", () =>
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }
                catch { SetNotice("请打开 Windows 设置 → 个性化 → 任务栏 → 其他系统托盘图标。"); }
            }),
            ("退出托盘面板", () => { exiting = true; Close(); }),
        };
        glassMenu = new GlassTrayMenu(items, () => glassMenu = null, preferences.Theme);
        glassMenu.ShowNear(cursor, bounds);
    }

    private void LaunchFloating()
    {
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Floating", "CodexPool.Floating.exe"));
        if (!File.Exists(executable)) executable = Environment.ProcessPath!;
        try { Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! }); if (widget) Hide(); }
        catch { SetNotice("悬浮窗启动失败，请检查安装文件。"); }
    }
    private void TogglePin(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost; preferences = preferences with { Pinned = Topmost }; PinButton.Content = Topmost ? "●" : "○"; SavePosition();
    }
    private void ToggleVisibility(System.Drawing.Point? clicked = null) { if (IsVisible) { SavePosition(); Hide(); } else if (widget) ShowFlyout(clicked); else { Show(); ClampPosition(); Activate(); } }
    private void ShowFlyout(System.Drawing.Point? clicked = null)
    {
        var opening = !IsVisible;
        if (changingShape) { StopShapeAnimation(); if (snapshot is { } latest) Render(latest); }
        if (tray?.TryGetBounds(out var iconBounds) == true && (clicked is null || iconBounds.Contains(clicked.Value))) flyoutAnchor = iconBounds;
        else flyoutAnchor = new System.Drawing.Rectangle(clicked ?? Forms.Cursor.Position, System.Drawing.Size.Empty);
        flyoutScreen = Forms.Screen.FromPoint(new System.Drawing.Point(flyoutAnchor.Value.Left + flyoutAnchor.Value.Width / 2, flyoutAnchor.Value.Top + flyoutAnchor.Value.Height / 2));
        Show(); UpdateLayout(); PositionFlyout(); Activate();
        if (opening) AnimateReveal();
    }
    private void AnimateReveal()
    {
        // Honour Windows' accessibility preference. Animate the content, never the hit-test HWND.
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;
        Surface.RenderTransformOrigin = new System.Windows.Point(1, 1);
        if (tray?.TryGetBounds(out var iconRect) == true && GetWindowRect(handle, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
            Surface.RenderTransformOrigin = new System.Windows.Point(
                Math.Clamp((iconRect.Left + iconRect.Width / 2d - rect.Left) / (rect.Right - rect.Left), 0, 1),
                Math.Clamp((iconRect.Top + iconRect.Height / 2d - rect.Top) / (rect.Bottom - rect.Top), 0, 1));
        var capsule = preferences.Mode == 2;
        var duration = TimeSpan.FromMilliseconds(capsule ? 220 : 170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DoubleAnimation Reveal(double from, double to) => new(from, to, new Duration(duration))
            { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        revealScale.BeginAnimation(ScaleTransform.ScaleXProperty, Reveal(capsule ? .25 : .97, 1));
        revealScale.BeginAnimation(ScaleTransform.ScaleYProperty, Reveal(capsule ? .65 : .97, 1));
        revealOffset.BeginAnimation(TranslateTransform.YProperty, Reveal(capsule ? 4 : 12, 0));
        BeginAnimation(OpacityProperty, Reveal(0, 1));
    }
    private void PositionFlyout()
    {
        if (!widget || changingShape || positioningFlyout || handle == 0 || !IsVisible) return;
        positioningFlyout = true;
        try
        {
            var area = (flyoutScreen ?? Forms.Screen.FromPoint(Forms.Cursor.Position)).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            AccountScroll.MaxHeight = Math.Max(80, area.Height / dpi.DpiScaleY - 260);
            UpdateLayout();
            if (!GetWindowRect(handle, out var rect)) return;
            var gap = (int)Math.Round(12 * dpi.DpiScaleX);
            var anchor = flyoutAnchor ?? new System.Drawing.Rectangle(Forms.Cursor.Position, System.Drawing.Size.Empty);
            var position = FlyoutPlacement.Above(area, anchor, rect.Right - rect.Left, rect.Bottom - rect.Top, gap);
            _ = SetWindowPos(handle, 0, position.X, position.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        }
        finally { positioningFlyout = false; }
    }
    private System.Drawing.Point ShapePosition(System.Drawing.Size size)
    {
        var area = (flyoutScreen ?? Forms.Screen.FromPoint(Forms.Cursor.Position)).WorkingArea;
        var anchor = flyoutAnchor ?? new System.Drawing.Rectangle(Forms.Cursor.Position, System.Drawing.Size.Empty);
        return FlyoutPlacement.Above(area, anchor, size.Width, size.Height, (int)Math.Round(12 * VisualTreeHelper.GetDpi(this).DpiScaleX));
    }
    private void RestorePosition() { if (handle == 0) return; _ = SetWindowPos(handle, 0, preferences.X, preferences.Y, 0, 0, 0x0001 | 0x0004 | 0x0010); ClampPosition(); }
    private void ClampPosition()
    {
        if (widget) { PositionFlyout(); return; }
        if (handle == 0 || !GetWindowRect(handle, out var rect)) return;
        var area = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this); AccountScroll.MaxHeight = Math.Max(80, area.Height / dpi.DpiScaleY - 160);
        var x = Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - (rect.Right - rect.Left)));
        var y = Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - (rect.Bottom - rect.Top)));
        _ = SetWindowPos(handle, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }
    private void SavePosition()
    {
        if (handle == 0 || !GetWindowRect(handle, out var rect)) return;
        if (!widget) preferences = preferences with { X = rect.Left, Y = rect.Top };
        try { Directory.CreateDirectory(directory); var path = Path.Combine(directory, preferencesFile); File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(preferences)); File.Move(path + ".tmp", path, true); } catch { }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePosition(); if (widget && !exiting) { e.Cancel = true; Hide(); return; }
        lifetime.Cancel(); timer.Stop(); SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        _ = UnregisterHotKey(handle, 1);
        tray?.Dispose();
        http.Dispose();
        System.Windows.Application.Current.Shutdown();
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
