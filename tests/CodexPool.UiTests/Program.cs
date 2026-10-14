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
        var failed = 0; var total = 0;
        void Run(string name, Action test)
        {
            total++;
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
