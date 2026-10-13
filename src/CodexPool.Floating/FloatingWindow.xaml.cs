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
    private readonly Forms.NotifyIcon? tray;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private WindowPreferences preferences = new();
    private JsonElement? snapshot;
    private bool exiting, busy, backdrop, connected;
    private readonly bool widget;
    private readonly string preferencesFile;
    private Forms.Screen? flyoutScreen;
    private bool positioningFlyout;
    private string? trayStateKey;
    private string? lastActiveAccountId;
    private bool switchNotifyReady;
    private long dismissedAt;
    private System.Drawing.Point dismissedPoint;
    private nint handle;
    public FloatingWindow(bool widget = false)
    {
        this.widget = widget;
        preferencesFile = widget ? "widget.json" : "window.json";
        InitializeComponent();
        if (widget) preferences = new(120, 120, Pinned: false);
        try { preferences = JsonSerializer.Deserialize<WindowPreferences>(File.ReadAllText(Path.Combine(directory, preferencesFile))) ?? preferences; } catch { }
        preferences = preferences with { Mode = Math.Clamp(preferences.Mode, 0, 2), Theme = Math.Clamp(preferences.Theme, 0, 2) };
        Topmost = widget || preferences.Pinned;
        Title = widget ? "Codex Pool · 托盘面板" : "Codex Pool";
        WidgetSummary.Visibility = widget ? Visibility.Visible : Visibility.Collapsed;
        WidgetActions.Visibility = widget ? Visibility.Visible : Visibility.Collapsed;
        PinButton.Visibility = Visibility.Collapsed;
        HeaderTitle.Text = "Codex Pool";
        PinButton.Content = Topmost ? "●" : "○";
        var shortcut = widget ? "Ctrl+Alt+W" : "Ctrl+Alt+C";
        // One tray icon only: owned by --tray / widget. Floating window reuses that entry via menu.
        if (widget)
        {
            tray = new Forms.NotifyIcon { Text = "Codex Pool · 账户状态 · " + shortcut, Icon = CreateIcon(), Visible = true };
            UpdateTrayQuota(null);
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示账户面板", null, (_, _) => Dispatcher.Invoke(ToggleVisibility));
            menu.Items.Add("打开悬浮窗", null, (_, _) => Dispatcher.Invoke(LaunchFloating));
            menu.Items.Add("打开 Dashboard", null, (_, _) => Dispatcher.Invoke(async () => await DashboardAsync()));
            menu.Items.Add("刷新额度", null, (_, _) => Dispatcher.Invoke(async () => await RefreshQuotaAsync(notify: true)));
            menu.Items.Add("退出托盘面板", null, (_, _) => Dispatcher.Invoke(() => { exiting = true; Close(); }));
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (_, e) =>
            {
                if (e.Button != Forms.MouseButtons.Left) return;
                Dispatcher.Invoke(() =>
                {
                    // Explorer can take focus before delivering the icon click. That same click
                    // should dismiss the visible flyout, rather than reopen it after Deactivated.
                    var cursor = Forms.Cursor.Position;
                    if (Environment.TickCount64 - dismissedAt < 500 && Math.Abs(cursor.X - dismissedPoint.X) <= 2 && Math.Abs(cursor.Y - dismissedPoint.Y) <= 2) return;
                    ToggleVisibility();
                });
            };
        }
        else tray = null;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle); source?.AddHook(WindowMessage);
            if (source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            var acrylic = 3; backdrop = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
                && DwmSetWindowAttribute(handle, 38, ref acrylic, 4) == 0;
            var corners = 2; _ = DwmSetWindowAttribute(handle, 33, ref corners, 4);
            var registered = RegisterHotKey(handle, 1, 0x0001 | 0x0002 | 0x4000, widget ? 0x57u : 0x43u);
            if (!registered) SetNotice(shortcut + " 已被其他应用使用。可从托盘显示窗口。");
            ApplyTheme(); if (!widget) RestorePosition(); _ = WatchAsync(); timer.Start();
        };
        if (widget)
        {
            Deactivated += (_, _) => { dismissedAt = Environment.TickCount64; dismissedPoint = Forms.Cursor.Position; Hide(); };
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Hide(); e.Handled = true; } };
            SizeChanged += (_, _) => { if (IsVisible) PositionFlyout(); };
        }
        timer.Tick += (_, _) => { SavePosition(); if (snapshot is { } s) Render(s); };
        Closing += OnClosing;
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        ApplyTheme();
    }
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(ApplyTheme);
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
        if (widget) UpdateTrayQuota(null);
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
        if (widget) UpdateTrayQuota(connected ? state : null);
        NotifyActiveSwitch(state);
        snapshot = state; Accounts.IsEnabled = !busy && connected; Accounts.Children.Clear();
        var capsule = preferences.Mode == 2; Width = capsule ? 420 : widget ? 380 : 336;
        Header.Visibility = capsule ? Visibility.Collapsed : Visibility.Visible;
        Surface.Padding = capsule ? new Thickness(14, 10, 8, 10) : new Thickness(18, 14, 18, 14);
        Footer.Visibility = capsule && !widget ? Visibility.Collapsed : Visibility.Visible;
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
            // Slim chrome above chips: drag + mode cycle stay out of the account row.
            var chrome = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
            var expand = new System.Windows.Controls.Button { Content = "◫", Padding = new Thickness(7, 2, 7, 2), ToolTip = "展开", VerticalAlignment = VerticalAlignment.Center };
            expand.Click += CycleMode;
            DockPanel.SetDock(expand, Dock.Right);
            chrome.Children.Add(expand);
            var grip = Label("⠿  胶囊", 12, .5, new Thickness(0, 0, 10, 0));
            grip.Cursor = System.Windows.Input.Cursors.SizeAll;
            grip.MouseLeftButtonDown += DragHeader;
            grip.VerticalAlignment = VerticalAlignment.Center;
            chrome.Children.Add(grip);
            Accounts.Children.Add(chrome);
            Accounts.Children.Add(wrap);
        }
        foreach (var account in state.GetProperty("accounts").EnumerateArray())
        {
            if (widget && !account.GetProperty("enabled").GetBoolean() && !account.GetProperty("active").GetBoolean()) continue;
            var id = account.GetProperty("id").GetString()!; var active = account.GetProperty("active").GetBoolean();
            var name = account.GetProperty("name").GetString() ?? "Account"; var auth = account.GetProperty("auth").GetString();
            var usage = state.GetProperty("usage").EnumerateArray().FirstOrDefault(x => x.GetProperty("accountId").GetString() == id);
            JsonElement Window(string kind) => usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("windows", out var windows)
                ? windows.EnumerateArray().FirstOrDefault(x => x.GetProperty("kind").GetString() == kind) : default;
            var five = Window("5h"); var week = Window("weekly");
            var canActivate = account.GetProperty("enabled").GetBoolean() && !account.GetProperty("paused").GetBoolean() && !account.GetProperty("drain").GetBoolean()
                && account.GetProperty("state").GetString() != "cooldown" && auth is not ("reauth_required" or "refresh_pending");
            if (capsule)
            {
                wrap.Children.Add(CapsuleChip(id, name, five, week, active, canActivate, accent, edge));
                continue;
            }
            var body = new StackPanel { Margin = new Thickness(0, 0, active ? 18 : 0, 0) };
            body.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (preferences.Mode == 0)
            {
                var status = account.GetProperty("state").GetString();
                var plan = account.GetProperty("plan").ValueKind == JsonValueKind.String ? account.GetProperty("plan").GetString() : "Plan 待确认";
                body.Children.Add(Label(plan + " · " + AccountState(status), 11, .55, new Thickness(0, 4, 0, 2)));
            }
            body.Children.Add(QuotaBar("5h", five));
            body.Children.Add(QuotaBar("Week", week));
            if (preferences.Mode == 0 && usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("updatedAt", out var updated))
                body.Children.Add(Label("更新 " + updated.GetDateTimeOffset().ToLocalTime().ToString("HH:mm:ss")
                    + (DateTimeOffset.UtcNow - updated.GetDateTimeOffset() > TimeSpan.FromMinutes(2) ? " · 已过期" : ""), 10, .45, new Thickness(0, 6, 0, 0)));
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
            var card = new Border
            {
                Child = grid,
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.2),
                BorderBrush = active ? new Brush(MediaColor.FromArgb(200, accent.R, accent.G, accent.B)) : edge,
                Background = new Brush(active ? MediaColor.FromArgb(48, accent.R, accent.G, accent.B) : MediaColor.FromArgb(32, 135, 150, 170)),
                Cursor = canActivate || active ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
                Opacity = canActivate || active ? 1 : 0.55
            };
            if (canActivate && !active)
            {
                var accountId = id;
                card.MouseLeftButtonUp += async (_, e) =>
                {
                    if (busy) return;
                    e.Handled = true;
                    await ActivateAsync(accountId);
                };
            }
            Accounts.Children.Add(card);
        }
if (Accounts.Children.Count == 0) Accounts.Children.Add(Label("没有启用的账户。请在 Web 面板添加或启用。", 12, .7, new Thickness(0, 12, 0, 12)));
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
                var fresh = quota.ValueKind == JsonValueKind.Object && quota.TryGetProperty("updatedAt", out var updated)
                    && updated.TryGetDateTimeOffset(out var date) && DateTimeOffset.UtcNow - date <= TimeSpan.FromMinutes(2)
                    && date <= DateTimeOffset.UtcNow.AddSeconds(30);
                JsonElement Window(string kind) => fresh && quota.TryGetProperty("windows", out var windows)
                    ? windows.EnumerateArray().FirstOrDefault(x => x.GetProperty("kind").GetString() == kind
                        && x.GetProperty("resetAt").GetDateTimeOffset() > DateTimeOffset.UtcNow) : default;
                var five = Window("5h"); var week = Window("weekly");
                var fiveRemaining = RemainingValue(five);
                var weekRemaining = RemainingValue(week);
                // Icon number: 5h remaining when known, else weekly. Exhausted binding window → X overlay.
                remaining = fiveRemaining ?? weekRemaining;
                exhausted = IsExhausted(weekRemaining) || (weekRemaining is null && IsExhausted(fiveRemaining));
                tooltip = name + "\n5h " + RemainingLabel(five) + " · " + ResetLeft(five)
                    + "\n周 " + RemainingLabel(week) + " · " + ResetLeft(week);
                if (!fresh) tooltip = name + "\n额度未知或已过期\n打开面板查看服务状态";
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
    private static bool IsExhausted(double? remaining) => remaining is { } value && value <= 0.5;
    private static double? RemainingValue(JsonElement window)
    {
        if (window.ValueKind != JsonValueKind.Object) return null;
        if (window.TryGetProperty("remainingPercent", out var remaining) && remaining.TryGetDouble(out var value) && double.IsFinite(value))
            return Math.Clamp(value, 0, 100);
        if (window.TryGetProperty("usedPercent", out var used) && used.TryGetDouble(out var usedValue) && double.IsFinite(usedValue))
            return Math.Clamp(100 - usedValue, 0, 100);
        return null;
    }
    private static string AccountState(string? state) => state switch
    {
        "active" => "使用中", "standby" => "待命", "paused" => "已暂停", "draining" => "排空中", "disabled" => "已禁用", "cooldown" => "冷却中", _ => "待确认"
    };
    private Border CapsuleChip(string id, string name, JsonElement five, JsonElement week, bool active, bool canActivate, MediaColor accent, System.Windows.Media.Brush edge)
    {
        var column = new StackPanel { Width = 108 };
        var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        titleRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = name,
            FontSize = 11,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = active ? new Brush(MediaColor.FromRgb(255, 255, 255)) : (System.Windows.Media.Brush)Resources["ForegroundBrush"]
        };
        titleRow.Children.Add(title);
        if (active)
        {
            var mark = new TextBlock
            {
                Text = "✓",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new Brush(MediaColor.FromRgb(255, 255, 255))
            };
            Grid.SetColumn(mark, 1);
            titleRow.Children.Add(mark);
        }
        column.Children.Add(titleRow);
        var gauges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        gauges.Children.Add(MiniGauge("5h", five, active));
        gauges.Children.Add(MiniGauge("W", week, active));
        column.Children.Add(gauges);
        var chip = new Border
        {
            Child = column,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = active ? new Brush(MediaColor.FromArgb(220, accent.R, accent.G, accent.B)) : edge,
            Background = active ? new Brush(accent) : new Brush(MediaColor.FromArgb(40, 135, 150, 170)),
            Cursor = canActivate || active ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
            Opacity = canActivate || active ? 1 : 0.55
        };
        if (canActivate && !active)
        {
            var accountId = id;
            chip.MouseLeftButtonUp += async (_, e) =>
            {
                if (busy) return;
                e.Handled = true;
                await ActivateAsync(accountId);
            };
        }
        return chip;
    }
    private FrameworkElement MiniGauge(string label, JsonElement window, bool activeOnChip)
    {
        var remaining = RemainingValue(window);
        var value = remaining ?? 0;
        var tone = remaining is null
            ? (activeOnChip ? MediaColor.FromRgb(255, 255, 255) : MediaColor.FromRgb(150, 161, 177))
            : activeOnChip && remaining >= 30
                ? MediaColor.FromRgb(255, 255, 255)
                : QuotaTone(remaining);
        const double size = 44;
        const double thickness = 3.5;
        var root = new Grid { Width = size + 8, Height = size + 18, Margin = new Thickness(2, 0, 2, 0) };
        var track = new System.Windows.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            StrokeThickness = thickness,
            Stroke = new Brush(activeOnChip ? MediaColor.FromArgb(90, 255, 255, 255) : MediaColor.FromArgb(55, 135, 150, 170)),
            Fill = System.Windows.Media.Brushes.Transparent,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        root.Children.Add(track);
        if (remaining is not null && value > 0.5)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                var radius = (size - thickness) / 2;
                var center = new System.Windows.Point(size / 2, size / 2);
                var startAngle = new System.Windows.Point(center.X, center.Y - radius);
                var sweep = Math.Clamp(value, 0, 100) / 100.0 * 360.0;
                var rad = (sweep - 90) * Math.PI / 180.0;
                var endAngle = new System.Windows.Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
                ctx.BeginFigure(startAngle, false, false);
                ctx.ArcTo(endAngle, new System.Windows.Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            root.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geo,
                Stroke = new Brush(tone),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = size,
                Height = size,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top
            });
        }
        var centerLabel = new TextBlock
        {
            Text = remaining is { } r ? r.ToString("0") : "—",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = activeOnChip ? new Brush(MediaColor.FromRgb(255, 255, 255)) : (System.Windows.Media.Brush)Resources["ForegroundBrush"]
        };
        root.Children.Add(centerLabel);
        var caption = new TextBlock
        {
            Text = label,
            FontSize = 9,
            Opacity = 0.7,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Foreground = activeOnChip ? new Brush(MediaColor.FromRgb(255, 255, 255)) : (System.Windows.Media.Brush)Resources["ForegroundBrush"]
        };
        root.Children.Add(caption);
        return root;
    }

    private static string RemainingLabel(JsonElement window)
    {
        var remaining = RemainingValue(window);
        return remaining is { } value ? "剩 " + value.ToString("0") + "%" : "—";
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
        if (window.ValueKind != JsonValueKind.Object) return "待确认";
        var span = window.GetProperty("resetAt").GetDateTimeOffset() - DateTimeOffset.UtcNow;
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(0, (int)span.TotalMinutes)}m";
    }
    private static TextBlock Label(string? value, double size, double opacity, Thickness margin) => new() { Text = value, FontSize = size, Opacity = opacity, Margin = margin };
    private static StackPanel QuotaBar(string title, JsonElement window)
    {
        var root = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new() { Width = new GridLength(42) });
        head.ColumnDefinitions.Add(new());
        head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var titleLabel = Label(title, 11, .65, new Thickness());
        head.Children.Add(titleLabel);
        var remainingLabel = Label(RemainingLabel(window), 11, 1, new Thickness());
        Grid.SetColumn(remainingLabel, 1);
        head.Children.Add(remainingLabel);
        var reset = Label(ResetLeft(window), 10, .55, new Thickness());
        Grid.SetColumn(reset, 2);
        head.Children.Add(reset);
        root.Children.Add(head);
        var remaining = RemainingValue(window) ?? 0;
        var tone = QuotaTone(RemainingValue(window));
        root.Children.Add(new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 100,
            Value = remaining,
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = new Brush(tone),
            Background = new Brush(MediaColor.FromArgb(45, 135, 150, 170)),
            BorderThickness = new Thickness(0)
        });
        return root;
    }
    private async Task ActivateAsync(string id)
    {
        busy = true; Accounts.IsEnabled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.PostAsJsonAsync("http://127.0.0.1:3210/accounts/" + id + "/activate", new { }, timeout.Token);
            if (!response.IsSuccessStatusCode) { SetNotice("账户切换未完成。请在 Dashboard 检查账户状态。"); }
            else Notice.Visibility = Visibility.Collapsed;
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
            tray.BalloonTipTitle = title;
            tray.BalloonTipText = text;
            tray.BalloonTipIcon = Forms.ToolTipIcon.Info;
            tray.ShowBalloonTip(2500);
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
    private void SetNotice(string value) { Notice.Text = value; Notice.Visibility = Visibility.Visible; }
    private void DragHeader(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (!widget && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove(); }
    private void CycleMode(object sender, RoutedEventArgs e) { preferences = preferences with { Mode = (preferences.Mode + 1) % 3 }; if (snapshot is { } state) Render(state); SavePosition(); }
    private void CycleTheme(object sender, RoutedEventArgs e) { preferences = preferences with { Theme = (preferences.Theme + 1) % 3 }; ApplyTheme(); SavePosition(); }
    private void HideWindow(object sender, RoutedEventArgs e) { SavePosition(); Hide(); }
    private async void OpenDashboard(object sender, RoutedEventArgs e) => await DashboardAsync();
    private void OpenFloating(object sender, RoutedEventArgs e) => LaunchFloating();
    private async void RefreshQuota(object sender, RoutedEventArgs e) => await RefreshQuotaAsync(notify: true);
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
    private void ToggleVisibility() { if (IsVisible) { SavePosition(); Hide(); } else if (widget) ShowFlyout(); else { Show(); ClampPosition(); Activate(); } }
    private void ShowFlyout()
    {
        flyoutScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        Show(); UpdateLayout(); PositionFlyout(); Activate();
    }
    private void PositionFlyout()
    {
        if (!widget || positioningFlyout || handle == 0 || !IsVisible) return;
        positioningFlyout = true;
        try
        {
            var area = (flyoutScreen ?? Forms.Screen.FromPoint(Forms.Cursor.Position)).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            AccountScroll.MaxHeight = Math.Max(80, area.Height / dpi.DpiScaleY - 260);
            UpdateLayout();
            if (!GetWindowRect(handle, out var rect)) return;
            var gap = (int)Math.Round(12 * dpi.DpiScaleX);
            var x = Math.Max(area.Left, area.Right - (rect.Right - rect.Left) - gap);
            var y = Math.Max(area.Top, area.Bottom - (rect.Bottom - rect.Top) - gap);
            _ = SetWindowPos(handle, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        }
        finally { positioningFlyout = false; }
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
        SavePosition(); if (!exiting) { e.Cancel = true; Hide(); return; }
        lifetime.Cancel(); timer.Stop(); SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        _ = UnregisterHotKey(handle, 1);
        if (tray is not null) { tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose(); }
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
