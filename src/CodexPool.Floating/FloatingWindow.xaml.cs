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

namespace CodexPool.Floating;

public sealed record WindowPreferences(int X = 80, int Y = 80, int Mode = 0, int Theme = 0);
public partial class FloatingWindow : Window
{
    private readonly string directory = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPool");
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private WindowPreferences preferences = new();
    private JsonElement? snapshot;
    private bool exiting, busy, backdrop;
    private nint handle;
    public FloatingWindow()
    {
        InitializeComponent();
        try { preferences = JsonSerializer.Deserialize<WindowPreferences>(File.ReadAllText(Path.Combine(directory, "window.json"))) ?? new(); } catch { }
        preferences = preferences with { Mode = Math.Clamp(preferences.Mode, 0, 2), Theme = Math.Clamp(preferences.Theme, 0, 2) };
        tray = new Forms.NotifyIcon { Text = "Codex Pool · Ctrl+Alt+C", Icon = CreateIcon(), Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => Dispatcher.Invoke(ToggleVisibility));
        menu.Items.Add("重置位置", null, (_, _) => Dispatcher.Invoke(() => { preferences = preferences with { X = 80, Y = 80 }; RestorePosition(); Show(); }));
        menu.Items.Add("打开 Dashboard", null, (_, _) => Dispatcher.Invoke(async () => await DashboardAsync()));
        menu.Items.Add("退出悬浮窗", null, (_, _) => Dispatcher.Invoke(() => { exiting = true; Close(); }));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(ToggleVisibility);
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle); source?.AddHook(WindowMessage);
            if (source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            var acrylic = 3; backdrop = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
                && DwmSetWindowAttribute(handle, 38, ref acrylic, 4) == 0;
            var corners = 2; _ = DwmSetWindowAttribute(handle, 33, ref corners, 4);
            var registered = RegisterHotKey(handle, 1, 0x0001 | 0x0002 | 0x4000, 0x43);
            if (!registered) SetNotice("Ctrl+Alt+C 已被其他应用使用。可从托盘显示窗口。");
            ApplyTheme(); RestorePosition(); _ = WatchAsync(); timer.Start();
        };
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
                    await Dispatcher.InvokeAsync(() => { Connection.Text = "服务在线 · 仅影响下次请求"; Render(state); });
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException or OperationCanceledException)
            { if (!lifetime.IsCancellationRequested) await Dispatcher.InvokeAsync(() => { Connection.Text = "服务离线 · 正在重新连接"; Accounts.IsEnabled = false; }); }
            if (!lifetime.IsCancellationRequested) try { await Task.Delay(1500, lifetime.Token); } catch (OperationCanceledException) { }
        }
    }
    private void Render(JsonElement state)
    {
        snapshot = state; Accounts.IsEnabled = !busy; Accounts.Children.Clear();
        var capsule = preferences.Mode == 2; Width = capsule ? 360 : 336;
        Header.Visibility = capsule ? Visibility.Collapsed : Visibility.Visible;
        Surface.Padding = capsule ? new Thickness(14, 10, 8, 10) : new Thickness(18, 14, 18, 14);
        Footer.Visibility = capsule ? Visibility.Collapsed : Visibility.Visible;
        AutoStatus.Text = state.GetProperty("automaticSwitching").GetBoolean() ? "Auto Switch · ON" : "Auto Switch · OFF";
        var wrap = new WrapPanel();
        if (capsule)
        {
            var grip = Label("⠿", 14, .45, new Thickness(0, 0, 10, 0)); grip.Cursor = System.Windows.Input.Cursors.SizeAll; grip.MouseLeftButtonDown += DragHeader;
            wrap.Children.Add(grip); Accounts.Children.Add(wrap);
        }
        foreach (var account in state.GetProperty("accounts").EnumerateArray())
        {
            var id = account.GetProperty("id").GetString()!; var active = account.GetProperty("active").GetBoolean();
            var name = account.GetProperty("name").GetString() ?? "Account"; var auth = account.GetProperty("auth").GetString();
            var usage = state.GetProperty("usage").EnumerateArray().FirstOrDefault(x => x.GetProperty("accountId").GetString() == id);
            JsonElement Window(string kind) => usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("windows", out var windows)
                ? windows.EnumerateArray().FirstOrDefault(x => x.GetProperty("kind").GetString() == kind) : default;
            var five = Window("5h"); var week = Window("weekly");
            var radio = new System.Windows.Controls.RadioButton { GroupName = "Accounts", IsChecked = active, Content = capsule ? name + " " + Percent(five) : name,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, capsule ? 14 : 0, 0),
                IsEnabled = account.GetProperty("enabled").GetBoolean() && !account.GetProperty("paused").GetBoolean() && !account.GetProperty("drain").GetBoolean()
                    && account.GetProperty("state").GetString() != "cooldown" && auth is not ("reauth_required" or "refresh_pending") };
            radio.Checked += async (_, _) => { if (!busy && !active) await ActivateAsync(id); };
            if (capsule) { wrap.Children.Add(radio); continue; }
            var card = new StackPanel(); card.Children.Add(radio);
            if (preferences.Mode == 0)
            {
                var status = account.GetProperty("state").GetString();
                var plan = account.GetProperty("plan").ValueKind == JsonValueKind.String ? account.GetProperty("plan").GetString() : "Plan 待确认";
                card.Children.Add(Label(plan + " · " + status, 11, .55, new Thickness(24, 5, 0, 10)));
            }
            card.Children.Add(QuotaRow("5h", five)); card.Children.Add(QuotaRow("Week", week));
            if (preferences.Mode == 0 && usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("updatedAt", out var updated))
                card.Children.Add(Label("更新 " + updated.GetDateTimeOffset().ToLocalTime().ToString("HH:mm:ss")
                    + (DateTimeOffset.UtcNow - updated.GetDateTimeOffset() > TimeSpan.FromMinutes(2) ? " · 已过期" : ""), 10, .45, new Thickness(24, 5, 0, 0)));
            Accounts.Children.Add(new Border { Child = card, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(10),
                Background = new Brush(active ? MediaColor.FromArgb(35, 103, 162, 243) : MediaColor.FromArgb(12, 135, 150, 170)) });
        }
        if (capsule) { var expand = new System.Windows.Controls.Button { Content = "◫", Padding = new Thickness(7, 2, 7, 2), ToolTip = "展开" }; expand.Click += CycleMode; wrap.Children.Add(expand); }
        if (!state.GetProperty("accounts").EnumerateArray().Any()) Accounts.Children.Add(Label("还没有账户。请在 Dashboard 添加。", 12, .7, new Thickness(0, 12, 0, 12)));
    }
    private static string Percent(JsonElement window) => window.ValueKind == JsonValueKind.Object ? window.GetProperty("usedPercent").GetDouble().ToString("0") + "%" : "—";
    private static TextBlock Label(string? value, double size, double opacity, Thickness margin) => new() { Text = value, FontSize = size, Opacity = opacity, Margin = margin };
    private static Grid QuotaRow(string title, JsonElement window)
    {
        var grid = new Grid { Margin = new Thickness(24, 4, 0, 4) }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(48) }); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(Label(title, 12, .6, new Thickness()));
        var used = Label(Percent(window), 12, 1, new Thickness()); Grid.SetColumn(used, 1); grid.Children.Add(used);
        var left = "待确认";
        if (window.ValueKind == JsonValueKind.Object)
        {
            var span = window.GetProperty("resetAt").GetDateTimeOffset() - DateTimeOffset.UtcNow;
            left = span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(0, (int)span.TotalMinutes)}m";
        }
        var reset = Label(left, 11, .55, new Thickness()); Grid.SetColumn(reset, 2); grid.Children.Add(reset); return grid;
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
        finally { busy = false; Accounts.IsEnabled = true; }
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
    private void DragHeader(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove(); }
    private void CycleMode(object sender, RoutedEventArgs e) { preferences = preferences with { Mode = (preferences.Mode + 1) % 3 }; if (snapshot is { } state) Render(state); SavePosition(); }
    private void CycleTheme(object sender, RoutedEventArgs e) { preferences = preferences with { Theme = (preferences.Theme + 1) % 3 }; ApplyTheme(); SavePosition(); }
    private void HideWindow(object sender, RoutedEventArgs e) { SavePosition(); Hide(); }
    private async void OpenDashboard(object sender, RoutedEventArgs e) => await DashboardAsync();
    private void ToggleVisibility() { if (IsVisible) { SavePosition(); Hide(); } else { Show(); ClampPosition(); Activate(); } }
    private void RestorePosition() { if (handle == 0) return; _ = SetWindowPos(handle, 0, preferences.X, preferences.Y, 0, 0, 0x0001 | 0x0004 | 0x0010); ClampPosition(); }
    private void ClampPosition()
    {
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
        preferences = preferences with { X = rect.Left, Y = rect.Top };
        try { Directory.CreateDirectory(directory); var path = Path.Combine(directory, "window.json"); File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(preferences)); File.Move(path + ".tmp", path, true); } catch { }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePosition(); if (!exiting) { e.Cancel = true; Hide(); return; }
        lifetime.Cancel(); timer.Stop(); SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        _ = UnregisterHotKey(handle, 1); tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose(); http.Dispose();
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
