using CodexPool.Floating;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var filter = args.Length == 2 && args[0] == "--filter" ? args[1] : null;
        if (args.Length == 1 && args[0] == "--proxy-icon-check")
        {
            foreach (var guid in new[] { StableTrayIcon.Identity, ProxyTrayController.Identity })
            {
                var identifier = new PublishedIconIdentifier { Size = (uint)Marshal.SizeOf<PublishedIconIdentifier>(), Guid = guid };
                Check(Shell_NotifyIconGetRect(ref identifier, out _) == 0);
            }
            Console.WriteLine("PASS read-only Explorer check: account and proxy icons are both registered");
            return 0;
        }
        if (args.Length == 2 && args[0] == "--proxy-owner-check")
        {
            var identity = ProxyDaemonProcess.Capture(args[1]);
            Console.WriteLine($"PASS read-only proxy identity: PID={identity.Pid}, executable={System.IO.Path.GetFileName(identity.Path)}");
            return 0;
        }
        var application = new App(false); application.InitializeComponent(); application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var failed = 0; var total = 0;
        void Run(string name, Action test)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
            total++;
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + " " + ex.GetType().Name + ": " + ex.Message); Console.WriteLine(ex.StackTrace); }
        }
        Run("Proxy and account trays have independent Explorer identities", () =>
        {
            var calls = new List<StableTrayIcon.NotifyData>();
            bool Record(uint operation, ref StableTrayIcon.NotifyData data) { calls.Add(data); return true; }
            using var tray = new StableTrayIcon(QuotaTrayIcon.Create(50), "代理预览", Record, ProxyTrayController.Identity);
            Check(ProxyTrayController.Identity != StableTrayIcon.Identity);
            tray.Dispose();
            Check(calls.Count > 0 && calls.All(x => x.Guid == ProxyTrayController.Identity));
        });
        Run("Account switch toggle preserves unified and legacy non-default thresholds", () =>
        {
            var state = JsonSerializer.SerializeToElement(new { settings = new { autoSwitch = true, fiveHourThreshold = 98, weeklyThreshold = 92, windowThreshold = (double?)null } });
            var settings = AccountTrayMenu.ToggleSettings(state);
            Check(!settings.AutoSwitch && settings.FiveHourThreshold == 98 && settings.WeeklyThreshold == 92 && settings.WindowThreshold is null);
            state = JsonSerializer.SerializeToElement(new { settings = new { autoSwitch = false, fiveHourThreshold = 98, weeklyThreshold = 92, windowThreshold = 87d } });
            settings = AccountTrayMenu.ToggleSettings(state);
            Check(settings.AutoSwitch && settings.WindowThreshold == 87);
        });
        Run("Tray menus separate account controls and proxy lifecycle while linking both clients", () =>
        {
            var starts = 0; var stops = 0; var opens = 0; var exits = 0;
            IReadOnlyList<GlassMenuItem> Proxy(bool online, bool busy = false) => ProxyTrayController.BuildMenu(online, busy,
                () => starts++, () => stops++, () => opens++, () => exits++);
            var offline = Proxy(false); offline.Single(x => x.Label == "打开账户浮窗").Invoke!();
            offline.Single(x => x.Label == "启动代理").Invoke!();
            var online = Proxy(true); online.Single(x => x.Label == "关闭代理").Invoke!();
            online.Single(x => x.Label.StartsWith("退出代理托盘")).Invoke!();
            Check(starts == 1 && stops == 1 && opens == 1 && exits == 1);
            Check(online.All(x => x.Children is null && !x.Label.Contains("自动切换") && !x.Label.Contains("额度") && !x.Label.Contains("用量")));
            Check(Proxy(true, true).Where(x => !x.Label.StartsWith("退出")).All(x => !x.Enabled));
            var state = JsonSerializer.SerializeToElement(new { activeAccountId = (string?)null, automaticSwitching = true,
                accounts = Array.Empty<object>(), usage = Array.Empty<object>(), inFlightRequests = Array.Empty<object>() });
            var window = new FloatingWindow(false);
            try
            {
                var initial = window.BuildTrayMenu();
                Check(initial.Single(x => x.Label == "启动代理").Enabled);
                window.Preview(state, 0);
                var account = window.BuildTrayMenu();
                Check(account.Single(x => x.Label == "关闭自动切换").Enabled);
                Check(account.Single(x => x.Label == "打开账户管理与用量").Enabled);
                Check(account.Single(x => x.Label == "打开代理控制").Enabled);
                Check(account.All(x => x.Label != "关闭代理" && !x.Label.StartsWith("退出代理托盘")));
                window.Preview(state, 0, online: false);
                var disconnected = window.BuildTrayMenu();
                Check(disconnected.Single(x => x.Label == "启动代理").Enabled);
                Check(disconnected.Where(x => x.Label is "关闭自动切换" or "刷新额度" or "打开账户管理与用量").All(x => !x.Enabled));
            }
            finally { window.Close(); }
        });
        Run("Linked client launch uses isolated arguments and never starts the daemon when opening the account panel", () =>
        {
            var root = System.IO.Path.Combine(Environment.CurrentDirectory, ".research", "linked clients " + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "scripts"));
            foreach (var script in new[] { "open-widget.ps1", "start-proxy-tray.ps1" })
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "scripts", script), "# fixture");
            var account = DesktopClientLauncher.StartInfo(root, accountPanel: true);
            Check(!account.UseShellExecute && account.CreateNoWindow && account.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden);
            Check(account.ArgumentList.SequenceEqual(new[] { "-NoProfile", "-File", System.IO.Path.Combine(root, "scripts", "open-widget.ps1"), "-Show", "-NoDaemonStart", "-SkipProxyTray" }));
            var proxy = DesktopClientLauncher.StartInfo(root, accountPanel: false);
            Check(proxy.ArgumentList.SequenceEqual(new[] { "-NoProfile", "-File", System.IO.Path.Combine(root, "scripts", "start-proxy-tray.ps1") }));
            Check(proxy.WorkingDirectory == root);
        });
        Run("Portable package launches native clients without scripts and limits daemon control to its own installation", () =>
        {
            var root = System.IO.Path.Combine(Environment.CurrentDirectory, ".research", "portable package " + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "Daemon"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, PortableInstallation.Manifest), "{\"format\":1,\"version\":\"0.1.0-beta.1\"}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "CodexPool.exe"), "fixture-not-executable");
            System.IO.File.WriteAllText(PortableInstallation.Daemon(root), "fixture-not-executable");
            var account = DesktopClientLauncher.StartInfo(root, accountPanel: true);
            Check(account.FileName == System.IO.Path.Combine(root, "CodexPool.exe") && !account.UseShellExecute && account.CreateNoWindow);
            Check(account.ArgumentList.SequenceEqual(new[] { "--show", "--account-only" }));
            Check(account.Environment["CODEX_POOL_DAEMON_AUTOSTART"] == "0" && account.Environment["CODEX_POOL_DAEMON"] == PortableInstallation.Daemon(root));
            var proxy = DesktopClientLauncher.StartInfo(root, accountPanel: false);
            Check(proxy.ArgumentList.SequenceEqual(new[] { "--proxy-tray" }));
            Check(ProxyDaemonProcess.OwnedPath(PortableInstallation.Daemon(root), root));
            Check(!ProxyDaemonProcess.OwnedPath(System.IO.Path.Combine(root, "Other", "CodexPool.Daemon.exe"), root));
            Check(!ProxyDaemonProcess.OwnedPath(System.IO.Path.Combine(root, "Daemon", "..", "Foreign", "CodexPool.Daemon.exe"), root));
            System.IO.File.Delete(PortableInstallation.Daemon(root));
            var rejected = false; try { _ = DesktopClientLauncher.StartInfo(root, accountPanel: true); } catch (System.IO.IOException) { rejected = true; }
            Check(rejected);
        });
        Run("Proxy process control excludes Codex, foreign roots and path traversal", () =>
        {
            const string root = @"D:\items\codex-pool";
            Check(ProxyDaemonProcess.OwnedPath(root + @"\.local\builds\one\Daemon\CodexPool.Daemon.exe", root));
            Check(!ProxyDaemonProcess.OwnedPath(root + @"\.local\builds\codex.exe", root));
            Check(!ProxyDaemonProcess.OwnedPath(root + @"\.local-other\CodexPool.Daemon.exe", root));
            Check(!ProxyDaemonProcess.OwnedPath(root + @"\.local\..\foreign\CodexPool.Daemon.exe", root));
            Check(!ProxyDaemonProcess.OwnedPath(@"C:\other\.local\CodexPool.Daemon.exe", root));
            Check(!ProxyDaemonProcess.OwnedPath(root + @"\.local\CodexPool.Daemon.exe", ""));
        });
        Run("Either quota window exhausted marks the account, unknown alone does not", () =>
        {
            Check(QuotaIndicator.IsExhausted(0, 70));
            Check(QuotaIndicator.IsExhausted(80, 0));
            Check(QuotaIndicator.IsExhausted(null, 0));
            Check(QuotaIndicator.IsExhausted(0, null));
            Check(!QuotaIndicator.IsExhausted(null, null));
            Check(!QuotaIndicator.IsExhausted(40, 70));
        });
        Run("Full quota renders a full circle rather than an empty arc", () =>
        {
            var full = QuotaIndicator.Ring(100, 44, 3.5);
            var almost = QuotaIndicator.Ring(99, 44, 3.5);
            var partial = QuotaIndicator.Ring(50, 44, 3.5);
            var pen = new Pen(Brushes.Blue, 3.5);
            Check(full.Bounds.Width > 40 && full.Bounds.Height > 40);
            Check(full.GetWidenedPathGeometry(pen).GetArea() >= almost.GetWidenedPathGeometry(pen).GetArea());
            Check(partial.GetWidenedPathGeometry(pen).GetArea() < full.GetWidenedPathGeometry(pen).GetArea() * .6);
            Check(QuotaIndicator.Ring(0, 44, 3.5).IsEmpty());
            Check(QuotaIndicator.Ring(double.NaN, 44, 3.5).IsEmpty());
        });
        Run("Tray keeps short quota until weekly routing is actually limited", () =>
        {
            JsonElement Quota(double weekly, int shortSeconds = 18000, bool stale = false, bool continuation = false, bool reset = false, double shortRemaining = 80) => JsonSerializer.SerializeToElement(new
            {
                updatedAt = stale ? DateTimeOffset.UtcNow.AddMinutes(-3) : DateTimeOffset.UtcNow, allowed = continuation,
                windows = new[] {
                    new { kind = "primary_window", windowSeconds = shortSeconds, limitId = "codex", remainingPercent = shortRemaining, resetAt = DateTimeOffset.UtcNow.AddHours(2) },
                    new { kind = "secondary_window", windowSeconds = 604800, limitId = "codex", remainingPercent = weekly, resetAt = reset ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddDays(2) },
                    new { kind = "primary_window", windowSeconds = 3600, limitId = "additional_0", remainingPercent = 0d, resetAt = DateTimeOffset.UtcNow.AddHours(1) } }
            });
            var automatic = JsonSerializer.SerializeToElement(new { autoSwitch = true, windowThreshold = 95 });
            foreach (var seconds in new[] { 18000, 25200, 32400 })
                Check(QuotaDisplay.TrayWindow(Quota(20, seconds), automatic)!.Seconds == seconds);
            Check(QuotaDisplay.TrayWindow(Quota(4), automatic)!.Label == "周");
            Check(QuotaDisplay.TrayWindow(Quota(4))!.Seconds == 18000);
            Check(QuotaDisplay.TrayWindow(Quota(0))!.Label == "周");
            Check(QuotaDisplay.TrayWindow(Quota(0, continuation: true))!.Seconds == 18000);
            Check(QuotaDisplay.TrayWindow(Quota(0, continuation: true, shortRemaining: 0)) is null);
            Check(QuotaDisplay.TrayWindow(Quota(0, reset: true), automatic)!.Seconds == 18000);
            Check(QuotaDisplay.TrayWindow(Quota(0, stale: true), automatic)!.Remaining is null);
            Check(QuotaDisplay.Windows(Quota(20)).Length == 3); // Other layouts retain all windows.
        });
        Run("Tray GUID survives updates, version 4 callbacks and Explorer restart", () =>
        {
            var calls = new List<(uint Operation, StableTrayIcon.NotifyData Data)>();
            bool Record(uint operation, ref StableTrayIcon.NotifyData data) { calls.Add((operation, data)); return true; }
            using var tray = new StableTrayIcon(QuotaTrayIcon.Create(50), "预览", Record);
            var clicks = new List<(int X, int Y)>(); tray.MouseClick += (_, e) => clicks.Add((e.X, e.Y));
            var presses = 0; tray.LeftPress += (_, _) => presses++;
            var keyboardSelections = 0; tray.MouseClick += (_, e) => { if (e.Clicks == 0) keyboardSelections++; };
            var packed = ((900 & 0xffff) << 16) | (-1200 & 0xffff);
            _ = SendMessage(tray.Handle, 0x8001, packed, (1 << 16) | 0x201);
            _ = SendMessage(tray.Handle, 0x8001, packed, (1 << 16) | 0x203);
            _ = SendMessage(tray.Handle, 0x8001, packed, (1 << 16) | 0x400);
            _ = SendMessage(tray.Handle, 0x8001, packed, (1 << 16) | 0x401);
            Check(clicks.Count == 2 && clicks.All(x => x == (-1200, 900)));
            Check(presses == 2 && keyboardSelections == 1);
            tray.Text = "额度更新";
            tray.ShowBalloon("账户切换", "已切换");
            _ = SendMessage(tray.Handle, RegisterWindowMessage("TaskbarCreated"), 0, 0);
            tray.Dispose();
            Check(calls.Count(x => x.Operation == 0) == 2);
            Check(calls.Count(x => x.Operation == 4 && x.Data.Version == 4) == 2);
            Check(calls.All(x => x.Data.Guid == StableTrayIcon.Identity && (x.Data.Flags & 0x20) != 0));
            Check(calls.Last().Operation == 2);
            Check(Marshal.SizeOf<StableTrayIcon.NotifyData>() == (IntPtr.Size == 8 ? 976 : 956));
        });
        Run("Flyout centers above icon and stays in the clicked monitor work area", () =>
        {
            var work = new System.Drawing.Rectangle(0, 0, 1920, 1040);
            var icon = new System.Drawing.Rectangle(1400, 1050, 24, 24);
            var point = FlyoutPlacement.Above(work, icon, 206, 53, 12);
            Check(point.X + 103 == 1412 && point.Y + 53 + 12 == icon.Top);
            var edge = FlyoutPlacement.Above(work, new(1890, 1050, 24, 24), 380, 400, 12);
            Check(edge.X == 1540 && edge.Y + 400 <= work.Bottom);
            var other = FlyoutPlacement.Above(new(-1920, -100, 1920, 1040), new(-800, 950, 24, 24), 206, 53, 12);
            Check(other.X + 103 == -788 && other.Y + 53 == 938);
            var top = FlyoutPlacement.Above(work, new(500, 0, 24, 24), 380, 400, 12);
            Check(top.Y == 36);
            var overflow = FlyoutPlacement.Above(work, new(1600, 850, 24, 24), 206, 53, 12);
            Check(overflow.Y + 53 + 12 == 850);
        });
        Run("Account menu groups choices, marks current and excludes unavailable actions", () =>
        {
            var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N"); var c = Guid.NewGuid().ToString("N");
            var state = JsonSerializer.SerializeToElement(new { activeAccountId = a, accounts = new[] {
                new { id=a, name="当前账户", enabled=true, paused=false, drain=false },
                new { id=b, name="备用账户", enabled=true, paused=false, drain=false },
                new { id=c, name="暂停账户", enabled=true, paused=true, drain=false } } });
            string? activated = null;
            var item = AccountTrayMenu.Accounts(state, id => activated = id);
            Check(item.Label == "当前账户：当前账户" && item.Invoke is null && item.Children?.Count == 3);
            Check(item.Children![0].IsChecked && !item.Children[0].Enabled && item.Children[0].Invoke is null);
            Check(!item.Children[2].Enabled && item.Children[2].Invoke is null);
            item.Children[1].Invoke!(); Check(activated == b);
        });
        Run("Submenu prefers right, flips at the edge and clamps in negative-coordinate work areas", () =>
        {
            var work = new System.Drawing.Rectangle(0, 0, 1920, 1040);
            Check(FlyoutPlacement.Beside(work, new(900, 600, 190, 26), 172, 100, 0) == new System.Drawing.Point(1090, 600));
            Check(FlyoutPlacement.Beside(work, new(1700, 980, 190, 26), 172, 100, 0) == new System.Drawing.Point(1528, 940));
            Check(FlyoutPlacement.Beside(new(-1920, 0, 1920, 1040), new(-500, 500, 172, 26), 172, 100, 0) == new System.Drawing.Point(-328, 500));
        });
        Run("Compact hover submenu keeps its parent alive and closes the group before an account action", () =>
        {
            var closed = false; var invoked = false;
            GlassTrayMenu? child = null;
            var root = new GlassTrayMenu(new GlassMenuItem[] {
                new("当前账户：主账户", Children: new GlassMenuItem[] {
                    new("主账户", Enabled:false, IsChecked:true),
                    new("备用账户", () => { Check(closed && child is not null && !child.IsVisible); invoked=true; }) }),
                new("打开账户管理与用量", () => { }), new("关闭自动切换", () => { }), new("刷新额度", () => { }), new("显示账户面板", () => { }), new("退出账户面板", () => { }) }, () => closed=true);
            try
            {
                var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
                root.ShowNear(new(work.Left + 400, work.Top + 500), null);
                Check(root.ActualHeight < 190 && root.ActualWidth <= 260);
                var button = (Button)((StackPanel)((Border)root.Content).Child).Children[0];
                button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent=UIElement.MouseEnterEvent });
                child = root.Submenu;
                Check(child is not null && child.IsVisible && root.IsVisible && !closed);
                button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent=UIElement.MouseEnterEvent });
                Check(ReferenceEquals(root.Submenu, child));
                Check(GetWindowRect(new System.Windows.Interop.WindowInteropHelper(root).Handle, out var parentRect));
                Check(GetWindowRect(new System.Windows.Interop.WindowInteropHelper(child!).Handle, out var childRect));
                Check(childRect.Left == parentRect.Right);
                child!.Activate(); Pump(30);
                Check(root.IsVisible && child.IsVisible && !closed);
                var choice = (Button)((StackPanel)((Border)child.Content).Child).Children[1];
                choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(closed && invoked && !root.IsVisible && !child.IsVisible);
            }
            finally { root.Close(); }
        });
        Run("Glass menu is created hidden and every visible native position is anchored on the target monitor", () =>
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var work = screen.WorkingArea;
                var cursor = new System.Drawing.Point(work.Right - 20, work.Bottom - 20);
                var anchor = new System.Drawing.Rectangle(cursor.X - 12, cursor.Y - 12, 24, 24);
                var visible = new List<(WindowRect Rect, uint Dpi)>();
                var createdHidden = false;
                var menu = new GlassTrayMenu([("首次显示位置", () => { }), ("第二行菜单", () => { })]);
                menu.SourceInitialized += (_, _) =>
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(menu).Handle;
                    createdHidden = !IsWindowVisible(handle);
                    System.Windows.Interop.HwndSource.FromHwnd(handle)!.AddHook((nint window, int message, nint wParam, nint lParam, ref bool handled) =>
                    {
                        if (message is 0x0047 or 0x0018 && IsWindowVisible(window) && GetWindowRect(window, out var rect))
                            visible.Add((rect, GetDpiForWindow(window)));
                        return 0;
                    });
                };
                try
                {
                    menu.ShowNear(cursor, anchor); Pump(50);
                    Check(createdHidden && visible.Count > 0);
                    Check(GetWindowRect(new System.Windows.Interop.WindowInteropHelper(menu).Handle, out var finalRect));
                    Check(visible.All(x => x.Rect.Left == finalRect.Left && x.Rect.Top == finalRect.Top
                        && x.Rect.Right == finalRect.Right && x.Rect.Bottom == finalRect.Bottom));
                    foreach (var (rect, dpi) in visible)
                    {
                        var point = FlyoutPlacement.Above(work, anchor, rect.Right - rect.Left, rect.Bottom - rect.Top, (int)Math.Round(8 * dpi / 96d));
                        Check(rect.Left == point.X && rect.Top == point.Y);
                    }
                }
                finally { menu.Close(); }
            }
        });
        Run("Glass menu uses native pixels on each monitor and closes before invoking an action", () =>
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var work = screen.WorkingArea;
                var closed = false;
                var invoked = false;
                var menu = new GlassTrayMenu([("测试菜单", () => { Check(closed); invoked = true; })], () => closed = true, theme: 1);
                try
                {
                    var cursor = new System.Drawing.Point(work.Right - 20, work.Bottom - 20);
                    menu.ShowNear(cursor, new System.Drawing.Rectangle(cursor.X - 12, cursor.Y - 12, 24, 24));
                    Check(GetWindowRect(new System.Windows.Interop.WindowInteropHelper(menu).Handle, out var rect));
                    Check(rect.Left >= work.Left && rect.Top >= work.Top && rect.Right <= work.Right && rect.Bottom <= work.Bottom);
                    if (!SystemParameters.HighContrast)
                        Check(((SolidColorBrush)menu.Foreground).Color == Color.FromRgb(26, 38, 53));
                    var button = (Button)((StackPanel)((Border)menu.Content).Child).Children[0];
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(closed && invoked);
                }
                finally { menu.Close(); }
            }
        });
        SceneTests.Run(Run);
        Console.WriteLine($"{total - failed}/{total} UI tests passed; isolated native windows, no credentials or proxy processes");
        application.Shutdown();
        return failed == 0 && total > 0 ? 0 : 1;
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException(); }
    [StructLayout(LayoutKind.Sequential)] private struct PublishedIconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref PublishedIconIdentifier identity, out WindowRect rect);
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out WindowRect rect);
}
