using CodexPool.Floating;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var failed = 0;
        void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + " " + ex.GetType().Name + ": " + ex.Message); Console.WriteLine(ex.StackTrace); }
        }
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
        Run("Unavailable capsule visibly disables both current and standby accounts", () =>
        {
            foreach (var active in new[] { false, true })
            {
                var chip = CapsuleLayout.Account("账户", "冷却中", "冷却中", active, false, Brushes.Black, Brushes.Blue, Brushes.Gray);
                Check(!chip.IsEnabled && chip.Opacity < .6);
                Check(ToolTipService.GetShowOnDisabled(chip));
                var text = (TextBlock)((StackPanel)chip.Child).Children[0];
                Check(text.Foreground == SystemColors.GrayTextBrush);
                Check(chip.Background != Brushes.Blue);
            }
            var ready = CapsuleLayout.Account("账户", "5h 20%", "待命", false, true, Brushes.Black, Brushes.Blue, Brushes.Gray);
            Check(ready.IsEnabled && ready.Opacity == 1);
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
            var packed = ((900 & 0xffff) << 16) | (-1200 & 0xffff);
            _ = SendMessage(tray.Handle, 0x8001, packed, (1 << 16) | 0x400);
            _ = SendMessage(tray.Handle, 0x8001, packed, (1 << 16) | 0x401);
            Check(clicks.Count == 2 && clicks.All(x => x == (-1200, 900)));
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
        Run("Shape transition freezes content coordinates while the surface animates", () =>
        {
            var previous = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR");
            Environment.SetEnvironmentVariable("CODEX_POOL_DATA_DIR", System.IO.Path.GetFullPath(".research/ui-animation-fixture"));
            try
            {
                var window = new FloatingWindow(); // Not shown: SourceInitialized/network startup never runs.
                var method = typeof(FloatingWindow).GetMethod("AnimateShape", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
                var clock = (ClockGroup)method.Invoke(window, new object[] { 206d, 53d, 380d, 330d })!;
                Check(window.SizeToContent == SizeToContent.Manual);
                var surface = (Border)window.FindName("Surface");
                clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(190), TimeSeekOrigin.BeginTime);
                var motionHost = (Grid)window.FindName("MotionHost");
                Check(motionHost.Width == 380 && motionHost.Height == 330);
                Check(!System.Windows.DependencyPropertyHelper.GetValueSource(window, Window.WidthProperty).IsAnimated);
                Check(surface.Width > 206 && surface.Width < 380 && surface.Height > 53 && surface.Height < 330);
                clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(381), TimeSeekOrigin.BeginTime);
                Pump(30);
                Check(window.SizeToContent == SizeToContent.Height && window.Width == 380);
            }
            finally { Environment.SetEnvironmentVariable("CODEX_POOL_DATA_DIR", previous); }
        });
        Run("Native frame follows the surface on intermediate expansion and shrink frames", () =>
        {
            foreach (var expand in new[] { true, false })
            {
                var from = expand ? new Size(206, 53) : new Size(380, 330);
                var to = expand ? new Size(380, 330) : new Size(206, 53);
                var inner = new Border { Width = 180, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                var surface = new Border { Child = inner, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                var fixedRoot = new Grid { Width = 380, Height = 330, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                fixedRoot.Children.Add(surface);
                var host = new Window { Width = from.Width, Height = from.Height, Content = fixedRoot, Opacity = 0,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.Manual };
                try
                {
                    host.Show();
                    var nativeSizes = new List<(int Width, int Height)>();
                    var nativeHandle = new System.Windows.Interop.WindowInteropHelper(host).Handle;
                    System.Windows.Interop.HwndSource.FromHwnd(nativeHandle).AddHook((nint _, int message, nint wParam, nint lParam, ref bool handled) =>
                    {
                        if (message == 5) nativeSizes.Add(((int)((long)lParam & 0xffff), (int)(((long)lParam >> 16) & 0xffff)));
                        return 0;
                    });
                    var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
                    var anchorX = work.Left + work.Width / 2; var anchorBottom = work.Bottom - 20;
                    var timeline = new ParallelTimeline { Duration = TimeSpan.FromMilliseconds(380), FillBehavior = FillBehavior.HoldEnd };
                    timeline.Children.Add(new DoubleAnimation(from.Width, to.Width, timeline.Duration));
                    timeline.Children.Add(new DoubleAnimation(from.Height, to.Height, timeline.Duration));
                    var clock = (ClockGroup)timeline.CreateClock(); using var frames = ShapeMotion.Apply(host, surface, clock,
                        size => new System.Drawing.Point(anchorX - size.Width / 2, anchorBottom - size.Height));
                    var elapsed = 0;
                    foreach (var milliseconds in new[] { 95, 95, 95, 120 })
                    {
                        Pump(milliseconds); elapsed += milliseconds; host.UpdateLayout();
                        Check(GetWindowRect(new System.Windows.Interop.WindowInteropHelper(host).Handle, out var rect));
                        var dpi = VisualTreeHelper.GetDpi(host);
                        if (Math.Abs(rect.Right - rect.Left - surface.Width * dpi.DpiScaleX) > 2
                            || Math.Abs(rect.Bottom - rect.Top - surface.Height * dpi.DpiScaleY) > 2)
                            throw new InvalidOperationException($"frame {elapsed}: native={rect.Right - rect.Left}x{rect.Bottom - rect.Top}, surface={surface.Width}x{surface.Height}, host={host.Width}x{host.Height}, dpi={dpi.DpiScaleX}x{dpi.DpiScaleY}");
                        Check(Math.Abs((rect.Left + rect.Right) / 2d - anchorX) <= 1 && Math.Abs(rect.Bottom - anchorBottom) <= 1);
                        Check(inner.TransformToAncestor(surface).Transform(new Point()) == new Point());
                    }
                    Check(nativeSizes.Count > 2);
                    for (var i = 1; i < nativeSizes.Count; i++)
                        Check(expand ? nativeSizes[i].Width >= nativeSizes[i - 1].Width && nativeSizes[i].Height >= nativeSizes[i - 1].Height
                            : nativeSizes[i].Width <= nativeSizes[i - 1].Width && nativeSizes[i].Height <= nativeSizes[i - 1].Height);
                }
                finally { host.Close(); }
            }
        });
        Run("Three layouts reuse account controls and hidden windows cancel unfinished motion", () =>
        {
            var previous = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR");
            Environment.SetEnvironmentVariable("CODEX_POOL_DATA_DIR", System.IO.Path.GetFullPath(".research/ui-layout-fixture"));
            try
            {
                var window = new FloatingWindow();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var render = typeof(FloatingWindow).GetMethod("Render", flags)!;
                typeof(FloatingWindow).GetField("connected", flags)!.SetValue(window, true);
                var state = JsonSerializer.SerializeToElement(new
                {
                    automaticSwitching = true, inFlightRequests = Array.Empty<object>(),
                    accounts = new[] {
                        new { id = "a", name = "yyds26309", active = true, enabled = true, paused = false, drain = false, state = "active", auth = "ready", plan = "Plus" },
                        new { id = "b", name = "hpst26309", active = false, enabled = true, paused = false, drain = false, state = "standby", auth = "ready", plan = "Plus" }
                    },
                    usage = new[] { new { accountId = "a", updatedAt = DateTimeOffset.UtcNow, windows = new[] {
                        new { kind = "5h", remainingPercent = 66, resetAt = DateTimeOffset.UtcNow.AddHours(2) },
                        new { kind = "weekly", remainingPercent = 95, resetAt = DateTimeOffset.UtcNow.AddDays(2) } } } }
                });
                AccountElements? original = null;
                for (var mode = 0; mode <= 2; mode++)
                {
                    typeof(FloatingWindow).GetField("preferences", flags)!.SetValue(window, new WindowPreferences(Mode: mode));
                    render.Invoke(window, new object[] { state });
                    var current = ((Dictionary<string, AccountElements>)typeof(FloatingWindow).GetField("accountElements", flags)!.GetValue(window)!)["a"];
                    original ??= current;
                    Check(ReferenceEquals(original, current) && ReferenceEquals(original.Card, current.Card));
                    var root = (Border)window.FindName("Surface");
                    root.Measure(new Size(window.Width, double.PositiveInfinity)); root.Arrange(new Rect(root.DesiredSize));
                    Check(root.IsAncestorOf(original.Name) && root.IsAncestorOf(original.FiveValue) && root.IsAncestorOf(original.WeekValue));
                    Check(original.FiveValue.Text == "66%" && original.WeekValue.Text == "95%");
                    if (mode == 2)
                    {
                        var all = (Dictionary<string, AccountElements>)typeof(FloatingWindow).GetField("accountElements", flags)!.GetValue(window)!;
                        var first = original.Card.TransformToAncestor(root).Transform(new Point());
                        var second = all["b"].Card.TransformToAncestor(root).Transform(new Point());
                        Check(Math.Abs(first.Y - second.Y) < .1 && second.X > first.X);
                    }
                }
                // Simulate a pending native transition; network/render must keep those clocks alive.
                typeof(FloatingWindow).GetField("changingShape", flags)!.SetValue(window, true);
                // Hidden windows cancel motion by design. The visible-path contract is covered by the live UI.
                render.Invoke(window, new object[] { state });
                Check(!(bool)typeof(FloatingWindow).GetField("changingShape", flags)!.GetValue(window)!);
            }
            finally { Environment.SetEnvironmentVariable("CODEX_POOL_DATA_DIR", previous); }
        });
        Run("Full account cards show reported token totals and distinguish missing usage", () =>
        {
            var previous = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR");
            Environment.SetEnvironmentVariable("CODEX_POOL_DATA_DIR", System.IO.Path.GetFullPath(".research/ui-statistics-fixture"));
            try
            {
                var window = new FloatingWindow();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var render = typeof(FloatingWindow).GetMethod("Render", flags)!;
                foreach (var reported in new[] { 0, 1 })
                {
                    var state = JsonSerializer.SerializeToElement(new
                    {
                        automaticSwitching = false, inFlightRequests = Array.Empty<object>(), usage = Array.Empty<object>(),
                        accounts = new[] { new { id = "a", name = "测试账户", active = true, enabled = true, paused = false, drain = false, state = "active", auth = "ready", plan = "Plus" } },
                        tokenStatistics = new { today = new { accounts = new[] { new { accountId = "a", totals = new { requests = 2, reportedRequests = reported, totalTokens = 150 } } } } }
                    });
                    render.Invoke(window, new object[] { state });
                    var current = ((Dictionary<string, AccountElements>)typeof(FloatingWindow).GetField("accountElements", flags)!.GetValue(window)!)["a"];
                    Check(current.Tokens.Text == "今日 " + (reported == 0 ? "未报告" : 150.ToString("N0")) + " Token · 2 个请求");
                    var surface = (Border)window.FindName("Surface");
                    surface.Measure(new Size(window.Width, double.PositiveInfinity)); surface.Arrange(new Rect(surface.DesiredSize));
                    Check(surface.IsAncestorOf(current.Tokens));
                    Check(current.Tokens.ToolTip!.ToString()!.Contains(reported + "/2"));
                }
                typeof(FloatingWindow).GetField("preferences", flags)!.SetValue(window, new WindowPreferences(Mode: 2));
                var last = (JsonElement?)typeof(FloatingWindow).GetField("snapshot", flags)!.GetValue(window);
                render.Invoke(window, new object[] { last!.Value });
                var elements = ((Dictionary<string, AccountElements>)typeof(FloatingWindow).GetField("accountElements", flags)!.GetValue(window)!)["a"];
                Check(!((Border)window.FindName("Surface")).IsAncestorOf(elements.Tokens));
            }
            finally { Environment.SetEnvironmentVariable("CODEX_POOL_DATA_DIR", previous); }
        });
        Run("Every layout treats stale or reset quota as unknown", () =>
        {
            var previous = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR");
            Environment.SetEnvironmentVariable("CODEX_POOL_DATA_DIR", System.IO.Path.GetFullPath(".research/ui-stale-quota-fixture"));
            try
            {
                var window = new FloatingWindow(); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var render = typeof(FloatingWindow).GetMethod("Render", flags)!;
                foreach (var stale in new[] { true, false })
                foreach (var mode in new[] { 0, 1, 2 })
                {
                    typeof(FloatingWindow).GetField("preferences", flags)!.SetValue(window, new WindowPreferences(Mode: mode));
                    var state = JsonSerializer.SerializeToElement(new
                    {
                        automaticSwitching = false, inFlightRequests = Array.Empty<object>(),
                        accounts = new[] { new { id = "a", name = "测试账户", active = true, enabled = true, paused = false, drain = false, state = "active", auth = "ready", plan = "Plus" } },
                        usage = new[] { new { accountId = "a", updatedAt = stale ? DateTimeOffset.UtcNow.AddMinutes(-3) : DateTimeOffset.UtcNow,
                            windows = new[] { new { kind = "5h", remainingPercent = 66, resetAt = stale ? DateTimeOffset.UtcNow.AddHours(2) : DateTimeOffset.UtcNow.AddSeconds(-1) } } } }
                    });
                    render.Invoke(window, new object[] { state });
                    var account = ((Dictionary<string, AccountElements>)typeof(FloatingWindow).GetField("accountElements", flags)!.GetValue(window)!)["a"];
                    Check(account.FiveValue.Text == "—");
                }
            }
            finally { Environment.SetEnvironmentVariable("CODEX_POOL_DATA_DIR", previous); }
        });
        Run("All three layouts render 7h, 9h and additional windows without fixed placeholders", () =>
        {
            var previous = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR");
            Environment.SetEnvironmentVariable("CODEX_POOL_DATA_DIR", System.IO.Path.GetFullPath(".research/ui-dynamic-windows"));
            try
            {
                var window = new FloatingWindow(); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                typeof(FloatingWindow).GetField("connected", flags)!.SetValue(window, true);
                var render = typeof(FloatingWindow).GetMethod("Render", flags)!;
                var state = JsonSerializer.SerializeToElement(new
                {
                    automaticSwitching = false, inFlightRequests = Array.Empty<object>(),
                    accounts = new[] { new { id = "a", name = "测试账户", active = true, enabled = true, paused = false, drain = false, state = "active", auth = "ready", plan = "Pro" } },
                    usage = new[] { new { accountId = "a", updatedAt = DateTimeOffset.UtcNow, windows = new[] {
                        new { kind = "primary_window", windowSeconds = 25200, limitId = "codex", remainingPercent = 66, resetAt = DateTimeOffset.UtcNow.AddHours(2) },
                        new { kind = "secondary_window", windowSeconds = 32400, limitId = "codex", remainingPercent = 95, resetAt = DateTimeOffset.UtcNow.AddHours(3) },
                        new { kind = "primary_window", windowSeconds = 3600, limitId = "additional_0", remainingPercent = 40, resetAt = DateTimeOffset.UtcNow.AddHours(1) } } } }
                });
                foreach (var mode in new[] { 0, 1, 2 })
                {
                    typeof(FloatingWindow).GetField("preferences", flags)!.SetValue(window, new WindowPreferences(Mode: mode)); render.Invoke(window, new object[] { state });
                    var account = ((Dictionary<string, AccountElements>)typeof(FloatingWindow).GetField("accountElements", flags)!.GetValue(window)!)["a"];
                    var surface = (Border)window.FindName("Surface"); surface.Measure(new Size(window.Width, double.PositiveInfinity)); surface.Arrange(new Rect(surface.DesiredSize));
                    Check(account.Windows.Count == 3 && account.Windows.Values.Select(x => x.Heading.Text).SequenceEqual(new[] { "7h", "9h", "附加额度 · 1h" }));
                    Check(account.Windows.Values.Select(x => x.Value.Text).SequenceEqual(new[] { "66%", "95%", "40%" }));
                    foreach (var row in account.Windows.Values)
                        if (!surface.IsAncestorOf(row.Value)) throw new InvalidOperationException("missing window in layout " + mode + ": " + row.Heading.Text);
                    if (surface.IsAncestorOf(account.FiveValue) || surface.IsAncestorOf(account.WeekValue)) throw new InvalidOperationException("fixed placeholder in layout " + mode);
                }
                var none = JsonSerializer.SerializeToElement(new { accountId = "a", updatedAt = DateTimeOffset.UtcNow, windows = Array.Empty<object>(),
                    windowState = "not_reported", credits = new { hasCredits = true, unlimited = false, balance = 12.5 } });
                Check(QuotaDisplay.Windows(none).Length == 0 && QuotaDisplay.State(none).StartsWith("Credits "));
                var optional = JsonSerializer.SerializeToElement(new { updatedAt = DateTimeOffset.UtcNow, windows = new[] { new { kind = "primary_window", windowSeconds = (int?)null, resetAt = (DateTimeOffset?)null, remainingPercent = 20 } } });
                Check(QuotaDisplay.Windows(optional).Single().Remaining is null && QuotaDisplay.Windows(optional).Single().Label == "额度窗口");
                var duplicates = JsonSerializer.SerializeToElement(new { updatedAt = DateTimeOffset.UtcNow, windows = new[] {
                    new { kind = "5h", windowSeconds = 18000, remainingPercent = 80, resetAt = DateTimeOffset.UtcNow.AddHours(1) },
                    new { kind = "5h", windowSeconds = 18000, remainingPercent = 60, resetAt = DateTimeOffset.UtcNow.AddHours(2) } } });
                var rows = QuotaDisplay.Windows(duplicates); var shared = new AccountElements();
                Check(!ReferenceEquals(shared.Window(rows[0]).Value, shared.Window(rows[1]).Value));
            }
            finally { Environment.SetEnvironmentVariable("CODEX_POOL_DATA_DIR", previous); }
        });
        Console.WriteLine($"{13 - failed}/13 passed; popup/frame fixtures shown briefly, no credentials, real tray registration or proxy processes");
        return failed == 0 ? 0 : 1;
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException(); }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out WindowRect rect);
}
