using CodexPool.Floating;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class SceneTests
{
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException(); }
    internal static JsonElement State(bool stale = false, bool reset = false, int count = 2, bool reported = true) => JsonSerializer.SerializeToElement(new
    {
        automaticSwitching = true, inFlightRequests = Array.Empty<object>(),
        accounts = Enumerable.Range(0, count).Select(i => new { id = "a" + i, name = i == 0 ? "很长的主力账户名称用于验证显示和工具提示" : "备用账户" + i,
            active = i == 0, enabled = true, paused = i == 2, drain = false, state = i == 0 ? "active" : i == 2 ? "paused" : "standby", auth = "ready", plan = "Plus" }).ToArray(),
        usage = Enumerable.Range(0, count).Select(i => new { accountId = "a" + i, updatedAt = stale ? DateTimeOffset.UtcNow.AddMinutes(-3) : DateTimeOffset.UtcNow,
            windows = new[] { new { kind = "primary_window", windowSeconds = 25200, limitId = "codex", remainingPercent = 66d, resetAt = reset ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddHours(2) },
                new { kind = "secondary_window", windowSeconds = 604800, limitId = "codex", remainingPercent = 90d, resetAt = DateTimeOffset.UtcNow.AddDays(2) },
                new { kind = "primary_window", windowSeconds = 3600, limitId = "additional_0", remainingPercent = 40d, resetAt = DateTimeOffset.UtcNow.AddHours(1) } } }).ToArray(),
        tokenStatistics = new { today = new { accounts = new[] { new { accountId = "a0", totals = new { requests = 2, reportedRequests = reported ? 2 : 0, totalTokens = 1200 } } } } }
    });
    internal static void Run(Action<string, Action> run)
    {
        run("Continuous spring retarget preserves position and velocity through all mode directions", () =>
        {
            var spring = new MorphSpring(100, 60); spring.Aim(430, 390);
            for (var i = 0; i < 20; i++) spring.Step(1d / 144);
            var value = spring.Value.ToArray(); var velocity = spring.Velocity.ToArray(); spring.Aim(100, 60);
            Check(value.SequenceEqual(spring.Value) && velocity.SequenceEqual(spring.Velocity));
            for (var i = 0; i < 250; i++) spring.Step(1d / 144);
            Check(spring.Settled);
        });
        run("Presentation preserves arbitrary windows, missing usage, stale/reset quotas and token coverage", () =>
        {
            var data = PoolPresentation.Accounts(State(count: 3), true);
            Check(data[0].Windows.Select(w => w.Label).SequenceEqual(new[] { "7h", "周", "附加额度 · 1h" }));
            Check(data[0].Tokens.Contains("1,200") && !data[2].Selectable);
            Check(PoolPresentation.Accounts(State(reported: false), true)[0].Tokens.Contains("未报告"));
            Check(PoolPresentation.Accounts(State(stale: true), true)[0].Windows.All(w => w.Remaining is null));
            Check(PoolPresentation.Accounts(State(reset: true), true)[0].Windows[0].Remaining is null);
            Check(PoolPresentation.Accounts(State(), false).All(a => !a.Selectable && a.Status == "离线"));
        });
        run("Native host remains fixed while all six transitions retarget and account hit targets stay accessible", () =>
        {
            var window = new FloatingWindow(false) { Width = 620, Height = 750, Opacity = 0, ShowActivated = false };
            try
            {
                window.Show(); var scene = (PoolScene)window.FindName("Scene");
                window.Preview(State(count: 3), 0); Pump(1000);
                var hwnd = new WindowInteropHelper(window).Handle; Check(GetWindowRect(hwnd, out var initial));
                var sizes = 0;
                HwndSource.FromHwnd(hwnd).AddHook((nint _, int message, nint wp, nint lp, ref bool handled) => { if (message == 5) sizes++; return 0; });
                var accountButton = scene.AccountTargets["a1"];
                var fullHeight = accountButton.Height;
                foreach (var mode in new[] { 1, 2, 0, 2, 1, 0 })
                {
                    var previous = scene.Shape.Value.ToArray(); window.Preview(State(count: 3), mode);
                    Check(previous.SequenceEqual(scene.Shape.Value)); Pump(80);
                    Check(ReferenceEquals(accountButton, scene.AccountTargets["a1"]));
                    Check(GetWindowRect(hwnd, out var current) && current.Equals(initial));
                }
                Pump(1100); Check(!scene.Animating && sizes == 0);
                window.Preview(State(count: 3), 1); Pump(1100); Check(accountButton.Height < fullHeight && scene.Shape.Value[0] == 284);
                Check(accountButton.Height == 124); // Three quota rows share a compact identity column.
                Check(AutomationProperties.GetName(accountButton).Contains("备用账户1"));
                Check(AutomationProperties.GetHelpText(accountButton).Contains("7h 66%"));
                string? invoked = null; scene.ActivateAccount = id => invoked = id;
                var peer = new ButtonAutomationPeer(accountButton);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke(); Pump(20); Check(invoked == "a1");
                var point = accountButton.TransformToAncestor(scene).Transform(new Point(15, 15));
                Check(scene.InputHitTest(point) is not null);
                scene.SetBusy(true); Check(!accountButton.IsEnabled); scene.SetBusy(false);
                Check(!scene.AccountTargets["a2"].IsEnabled);
                Check(!scene.IsHitTestVisible || scene.InputHitTest(new Point(1, 1)) is null);
                scene.SetTheme(true, true); window.Preview(State(), 2); Check(!scene.Animating);
                window.Hide(); Check(!scene.Animating);
            }
            finally { window.Close(); }
        });
        run("Live state updates during morph replace stale accounts without resetting the current pose", () =>
        {
            var window = new FloatingWindow(false) { Opacity = 0, ShowActivated = false };
            try
            {
                window.Show(); window.Preview(State(), 0); Pump(1100);
                var scene = (PoolScene)window.FindName("Scene");
                window.Preview(State(), 2); Pump(70); var pose = scene.Shape.Value.ToArray();
                window.Preview(State(count: 3), 1, false);
                Check(pose.SequenceEqual(scene.Shape.Value));
                Check(scene.AccountTargets.Count == 3 && scene.AccountTargets.Values.All(b => !b.IsEnabled));
                Pump(1100); Check(!scene.Animating);
                window.Preview(State(count: 1), 2); Pump(1100); Check(scene.AccountTargets.Count == 1);
            }
            finally { window.Close(); }
        });
        run("Pinning keeps the same panel and account targets visible after deactivation; unpin restores dismissal", () =>
        {
            var window = new FloatingWindow(false) { Opacity = 0, ShowActivated = false };
            try
            {
                window.Show(); window.Preview(State(), 1); Pump(1100);
                var scene = (PoolScene)window.FindName("Scene"); var account = scene.AccountTargets["a1"];
                var hwnd = new WindowInteropHelper(window).Handle; Check(GetWindowRect(hwnd, out var before));
                Check(!window.Pinned && scene.PinTarget.IsChecked == false);
                var pinPeer = new ToggleButtonAutomationPeer(scene.PinTarget);
                ((IToggleProvider)pinPeer.GetPattern(PatternInterface.Toggle)).Toggle(); Pump(20);
                Check(window.Pinned && scene.PinTarget.IsChecked == true);
                Check(GetWindowRect(hwnd, out var after) && before.Equals(after));
                window.DismissOnDeactivation(); Check(window.IsVisible);
                window.Preview(State(), 2); Pump(1100);
                Check(window.Pinned && ReferenceEquals(account, scene.AccountTargets["a1"]));
                Check(scene.PinTarget.Visibility == Visibility.Collapsed);
                window.Preview(State(), 1); Pump(1100);
                ((IToggleProvider)pinPeer.GetPattern(PatternInterface.Toggle)).Toggle(); Pump(20);
                Check(!window.Pinned && scene.PinTarget.IsChecked == false);
                window.DismissOnDeactivation(); Check(!window.IsVisible && !scene.Animating);
            }
            finally { window.Close(); }
        });
        run("Retargeting a scrolled pool preserves the on-screen account pose and settles inside the new scroll range", () =>
        {
            var window = new FloatingWindow(false) { Opacity = 0, ShowActivated = false };
            try
            {
                window.Show(); window.Preview(State(count: 12), 0); Pump(1200);
                var scene = (PoolScene)window.FindName("Scene");
                scene.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -12000) { RoutedEvent = Mouse.MouseWheelEvent });
                Check(scene.ScrollOffset > 500);
                var card = scene.AccountTargets["a11"];
                var before = card.TranslatePoint(new Point(), scene); var previousScroll = scene.ScrollOffset;
                window.Preview(State(count: 12), 2);
                Check(before == card.TranslatePoint(new Point(), scene) && scene.ScrollOffset == previousScroll);
                Pump(100); var interrupted = card.TranslatePoint(new Point(), scene);
                window.Preview(State(count: 12), 1); Check(interrupted == card.TranslatePoint(new Point(), scene));
                Pump(1400); Check(!scene.Animating && scene.ScrollOffset >= 0);
                window.Preview(State(count: 1), 1); Pump(1400); Check(scene.ScrollOffset == 0 && !scene.Animating);
            }
            finally { window.Close(); }
        });
        run("Pinned expansion at the work-area corner stays on screen without a visible jump or native resize", () =>
        {
            var window = new FloatingWindow(false) { Opacity = 0, ShowActivated = false };
            try
            {
                window.Show(); window.Preview(State(), 1); Pump(1200); window.TogglePinned();
                var scene = (PoolScene)window.FindName("Scene"); var hwnd = new WindowInteropHelper(window).Handle;
                var area = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea; var dpi = VisualTreeHelper.GetDpi(scene);
                var root = scene.SurfaceBounds;
                Check(SetWindowPos(hwnd, 0, area.Left + 16 - (int)Math.Round(root.Left * dpi.DpiScaleX), area.Top + 16 - (int)Math.Round(root.Top * dpi.DpiScaleY), 0, 0, 0x15));
                var before = ScreenBounds(window, scene); var sizes = 0;
                HwndSource.FromHwnd(hwnd).AddHook((nint _, int message, nint wp, nint lp, ref bool handled) => { if (message == 5) sizes++; return 0; });
                window.Preview(State(), 0);
                var immediate = ScreenBounds(window, scene);
                Check(Math.Abs(before.Left - immediate.Left) < 1.1 && Math.Abs(before.Top - immediate.Top) < 1.1);
                Pump(80);
                foreach (var mode in new[] { 2, 0, 1, 0 })
                {
                    var interrupted = ScreenBounds(window, scene);
                    window.Preview(State(), mode); var retargeted = ScreenBounds(window, scene);
                    Check(Math.Abs(interrupted.Left - retargeted.Left) < 1.1 && Math.Abs(interrupted.Top - retargeted.Top) < 1.1);
                    Check(GetWindowRect(hwnd, out var start)); Pump(80);
                    Check(GetWindowRect(hwnd, out var unchanged) && start.Equals(unchanged));
                }
                Pump(1400); var after = ScreenBounds(window, scene);
                Check(after.Left >= area.Left - 1 && after.Top >= area.Top - 1 && after.Right <= area.Right + 1 && after.Bottom <= area.Bottom + 1);
                Check(!scene.Animating && sizes == 0);
                window.Preview(State(), 1); Pump(1200); root = scene.SurfaceBounds;
                Check(SetWindowPos(hwnd, 0, area.Left + 16 - (int)Math.Round(root.Left * dpi.DpiScaleX), area.Top + 16 - (int)Math.Round(root.Top * dpi.DpiScaleY), 0, 0, 0x15));
                window.Preview(State(), 0); Check(scene.Animating);
                window.TogglePinned();
                Check(!window.Pinned && Math.Abs(scene.SurfaceBounds.Top - (scene.AtTop ? 10 : scene.ActualHeight - 10 - scene.Shape.Value[1])) < .001);
            }
            finally { window.Close(); }
        });
        run("Scene displays empty pools, credits and many accounts with bounded viewport and redraw", () =>
        {
            var window = new FloatingWindow(false) { Opacity = 0, ShowActivated = false };
            try
            {
                window.Show(); var scene = (PoolScene)window.FindName("Scene");
                window.Preview(State(count: 12), 0); Pump(1100);
                Check(scene.SurfaceBounds.Height <= scene.ActualHeight - 19 && scene.AccountTargets.Count == 12);
                scene.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
                Check(scene.ScrollOffset > 0);
                window.Preview(State(count: 0), 2); Pump(1100); Check(scene.AccountTargets.Count == 0 && scene.SurfaceBounds.Height >= 64);
                var credits = JsonSerializer.SerializeToElement(new { updatedAt = DateTimeOffset.UtcNow, windows = Array.Empty<object>(), credits = new { hasCredits = true, unlimited = false, balance = 12.5 } });
                Check(QuotaDisplay.State(credits) == "Credits 12.5");
                var output = System.IO.Path.GetFullPath(".research/integrated-scene"); System.IO.Directory.CreateDirectory(output);
                foreach (var mode in new[] { 0, 1, 2 })
                {
                    window.Preview(State(), mode); Pump(1100); scene.UpdateLayout();
                    var dpi = VisualTreeHelper.GetDpi(scene);
                    var bitmap = new RenderTargetBitmap((int)(scene.ActualWidth * dpi.DpiScaleX), (int)(scene.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    bitmap.Render(scene); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(System.IO.Path.Combine(output, "mode-" + mode + ".png")); encoder.Save(stream);
                }
            }
            finally { window.Close(); }
        });
    }
    private static Rect ScreenBounds(Window window, PoolScene scene)
    {
        Check(GetWindowRect(new WindowInteropHelper(window).Handle, out var native));
        var dpi = VisualTreeHelper.GetDpi(scene); var root = scene.SurfaceBounds;
        return new(native.Left + root.Left * dpi.DpiScaleX, native.Top + root.Top * dpi.DpiScaleY, root.Width * dpi.DpiScaleX, root.Height * dpi.DpiScaleY);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out WindowRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
