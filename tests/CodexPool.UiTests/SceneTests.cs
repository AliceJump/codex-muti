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
        tokenStatistics = new { today = new { accounts = Enumerable.Range(0, count).Select(i => new { accountId = "a" + i,
            totals = new { requests = 2, reportedRequests = reported ? 2 : 0, totalTokens = i == 0 ? 1200 : 3000,
                inputTokens = i == 0 ? 1000 : 2000, outputTokens = i == 0 ? 200 : 1000,
                cachedInputTokens = i == 0 ? 800 : 500, cachedReportedRequests = reported ? 2 : 0 } }).ToArray() } }
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
        run("Subscription deadline uses device local time and keeps absent or invalid dates unknown", () =>
        {
            var state = System.Text.Json.Nodes.JsonNode.Parse(State().GetRawText())!;
            var until = DateTimeOffset.Parse("2030-01-02T23:04:05Z");
            state["accounts"]![0]!["subscriptionActiveUntil"] = until.ToString("O");
            var account = PoolPresentation.Accounts(JsonSerializer.SerializeToElement(state), true)[0];
            Check(account.SubscriptionDeadline == "订阅截止 " + until.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss '(UTC'zzz')'", System.Globalization.CultureInfo.InvariantCulture));
            Check(account.Description.Contains(account.SubscriptionDeadline));
            Check(PoolPresentation.Accounts(State(), true)[0].SubscriptionDeadline == "订阅截止时间未知");
            state["accounts"]![0]!["subscriptionActiveUntil"] = "invalid";
            Check(PoolPresentation.Accounts(JsonSerializer.SerializeToElement(state), true)[0].SubscriptionDeadline == "订阅截止时间未知");
            state["accounts"]![0]!["subscriptionActiveUntil"] = 123;
            Check(PoolPresentation.Accounts(JsonSerializer.SerializeToElement(state), true)[0].SubscriptionDeadline == "订阅截止时间未知");
            state["accounts"]![0]!["subscriptionState"] = "not_reported";
            Check(PoolPresentation.Accounts(JsonSerializer.SerializeToElement(state), true)[0].SubscriptionDeadline.Contains("上游未提供"));
            state["accounts"]![0]!["subscriptionState"] = "stale";
            state["accounts"]![0]!["subscriptionActiveUntil"] = until.ToString("O");
            Check(PoolPresentation.Accounts(JsonSerializer.SerializeToElement(state), true)[0].SubscriptionDeadline.Contains("旧授权资料"));
        });
        run("Quota refresh failure immediately hides current percentages and preserves account selection", () =>
        {
            var state = System.Text.Json.Nodes.JsonNode.Parse(State().GetRawText())!;
            state["usage"]![0]!["refreshError"] = new System.Text.Json.Nodes.JsonObject { ["code"] = "quota_forbidden", ["status"] = 403 };
            var accounts = PoolPresentation.Accounts(JsonSerializer.SerializeToElement(state), true);
            Check(accounts[0].Windows.All(w => w.Remaining is null) && accounts[0].QuotaState.Contains("历史缓存") && accounts[0].QuotaState.Contains("403"));
            Check(accounts[0].Selectable && accounts[1].Windows.All(w => w.Remaining is not null));
        });
        run("Token cache bars use each account independently and keep missing cache coverage unknown", () =>
        {
            var accounts = PoolPresentation.Accounts(State(), true);
            Check(accounts[0].TokenCache is { Cached: 800, Uncached: 200, CachedPercent: 80 });
            Check(accounts[1].TokenCache is { Cached: 500, Uncached: 1500, CachedPercent: 25 });
            Check(accounts[0].Tokens.Contains("1,200") && accounts[1].Tokens.Contains("3,000"));
            Check(accounts[0].Description.Contains("80.0%") && accounts[1].Description.Contains("25.0%"));
            var state = System.Text.Json.Nodes.JsonNode.Parse(State().GetRawText())!;
            var totals = state["tokenStatistics"]!["today"]!["accounts"]![0]!["totals"]!;
            totals["cachedReportedRequests"] = 1;
            var partial = PoolPresentation.Accounts(JsonSerializer.SerializeToElement(state), true)[0].TokenCache!;
            Check(partial.Cached is null && partial.Uncached is null && partial.CachedPercent is null);
            totals["requests"] = 0; totals["reportedRequests"] = 0; totals["inputTokens"] = 0;
            totals["cachedInputTokens"] = 0; totals["cachedReportedRequests"] = 0;
            var empty = PoolPresentation.Accounts(JsonSerializer.SerializeToElement(state), true)[0].TokenCache!;
            Check(empty.Cached == 0 && empty.Uncached == 0 && empty.CachedPercent is null);
            Check(new TokenCacheSplit(100, 0).CachedPercent == 100 && new TokenCacheSplit(0, 100).CachedPercent == 0);
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
                window.Preview(State(count: 3), 1); Pump(1100); Check(accountButton.Height < fullHeight && scene.Shape.Value[0] == 268);
                Check(accountButton.Height == 112); // Three quota rows share a compact identity column.
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
        run("Tray entrance repeats on every hidden-to-visible transition and shares the scene clock without native resizing", () =>
        {
            var window = new FloatingWindow(false) { Opacity = 0, ShowActivated = false };
            try
            {
                var scene = (PoolScene)window.FindName("Scene");
                window.ShowFromLauncher(); Check(scene.EntranceProgress == 0 && scene.Opacity == 0 && scene.Animating);
                Pump(1200); Check(scene.EntranceProgress == 1 && !scene.Animating); window.Hide();
                var hwnd = new WindowInteropHelper(window).Handle; var sizes = 0;
                HwndSource.FromHwnd(hwnd).AddHook((nint _, int message, nint wp, nint lp, ref bool handled) => { if (message == 5) sizes++; return 0; });
                foreach (var mode in new[] { 0, 1, 2 })
                {
                    window.Preview(State(), mode);
                    window.ShowFromLauncher(); Check(scene.EntranceProgress == 0 && scene.Opacity == 0 && scene.Animating);
                    Check(GetWindowRect(hwnd, out var initial));
                    Pump(80); var progress = scene.EntranceProgress;
                    if (progress is <= 0 or >= 1) throw new InvalidOperationException($"Entrance mode {mode} did not expose an intermediate frame: {progress}");
                    Check(((TranslateTransform)scene.RenderTransform).Y * (scene.AtTop ? -1 : 1) > 0);
                    window.Preview(State(count: 3), mode); Check(scene.EntranceProgress == progress);
                    window.ShowFromLauncher(); Check(scene.EntranceProgress == progress);
                    Pump(1200); Check(scene.EntranceProgress == 1 && scene.Opacity == 1 && !scene.Animating);
                    Check(GetWindowRect(hwnd, out var current) && current.Equals(initial));
                    window.Hide(); Check(!scene.Animating);
                }
                window.ShowFromLauncher(); Pump(30); window.Hide();
                Check(!scene.Animating && scene.EntranceProgress == 1);
                window.ShowFromLauncher(); Check(scene.EntranceProgress == 0); Pump(1200);
                window.TogglePinned(); window.Hide(); window.ShowFromLauncher(); Check(scene.EntranceProgress == 0); Pump(1200);
                Check(window.Pinned && !scene.Animating && sizes == 0);
                window.Hide(); window.ShowFromLauncher(); Pump(30); scene.SetTheme(true, true);
                Check(scene.EntranceProgress == 1 && !scene.Animating);
                window.Hide(); window.ShowFromLauncher();
                Check(scene.EntranceProgress == 1 && scene.Opacity == 1 && !scene.Animating);
            }
            finally { window.Close(); }
        });
        run("Native flyout host stays transparent until the replacement entrance frame is ready", () =>
        {
            // Unlike the scene-only tests, exercise the real Window.Opacity path.
            var window = new FloatingWindow(false) { ShowActivated = false, Topmost = false };
            try
            {
                var scene = (PoolScene)window.FindName("Scene");
                var highContrast = SystemParameters.HighContrast || !SystemParameters.ClientAreaAnimation;
                window.ShowFromLauncher();
                Check(window.IsVisible && window.Opacity == (highContrast ? 1 : 0)
                    && scene.EntranceProgress == (highContrast ? 1 : 0) && !scene.Animating);
                var hwnd = new WindowInteropHelper(window).Handle;
                Check(IsAppCloaked(hwnd) == !highContrast);
                Pump(180);
                Check(!IsAppCloaked(hwnd) && window.Opacity == 1 && (highContrast
                    ? scene.EntranceProgress == 1 && !scene.Animating
                    : scene.EntranceProgress > 0 && scene.Animating));
                Pump(1200);
                window.Hide();

                // Reopen a populated, previously rendered native host through the
                // actual tray selection handler, where the stale bitmap was seen.
                foreach (var fromTray in new[] { true, false, true })
                {
                    window.Preview(State(), 0);
                    if (fromTray) { window.HandleTrayPress(); window.HandleTrayClick(System.Drawing.Point.Empty); }
                    else window.ShowFromLauncher();
                    Check(window.Opacity == (highContrast ? 1 : 0));
                    Check(IsAppCloaked(hwnd) == !highContrast);
                    var shortHeight = scene.SurfaceBounds.Height;
                    Check(highContrast || shortHeight < scene.TargetSurfaceBounds.Height);
                    window.Preview(State(count: 3), 0);
                    Check(scene.EntranceProgress == (highContrast ? 1 : 0) && !scene.Animating);
                    Check(scene.SurfaceBounds.Height == shortHeight);
                    Pump(180);
                    Check(window.IsVisible && !IsAppCloaked(hwnd) && window.Opacity == 1 && scene.EntranceProgress > 0);
                    Check(highContrast || scene.SurfaceBounds.Height > shortHeight);
                    Pump(1200);
                    Check(!scene.Animating && scene.SurfaceBounds == scene.TargetSurfaceBounds);
                    window.Hide();
                }

                // A quick hide before the first rendered frame must cancel the old
                // reveal and never let it surface a stale frame on the next open.
                window.ShowFromLauncher();
                Check(window.Opacity == (highContrast ? 1 : 0));
                window.Hide();
                Pump(80);
                Check(!window.IsVisible && !IsAppCloaked(hwnd) && window.Opacity == (highContrast ? 1 : 0));
                window.ShowFromLauncher();
                Check(window.Opacity == (highContrast ? 1 : 0) && scene.EntranceProgress == (highContrast ? 1 : 0));
                Pump(180);
                Check(window.Opacity == 1 && scene.EntranceProgress > 0);
                window.Hide();

                scene.SetTheme(true, true);
                window.ShowFromLauncher();
                Check(window.Opacity == 1 && scene.EntranceProgress == 1 && !scene.Animating);
            }
            finally { window.Close(); }
        });
        run("Tray and launcher entrances grow from a short panel even with warmed account state", () =>
        {
            var window = new FloatingWindow(false) { Opacity = 0, ShowActivated = false };
            try
            {
                window.InitializeHidden();
                var scene = (PoolScene)window.FindName("Scene");
                foreach (var mode in new[] { 0, 1, 2 })
                foreach (var fromTray in new[] { false, true })
                {
                    window.Preview(State(), mode);
                    if (fromTray) { window.HandleTrayPress(); window.HandleTrayClick(System.Drawing.Point.Empty); }
                    else window.ShowFromLauncher();
                    var initial = scene.SurfaceBounds;
                    if (initial.Height >= scene.TargetSurfaceBounds.Height)
                        throw new InvalidOperationException($"Entrance mode {mode}, tray={fromTray} opened at its final height");
                    var hwnd = new WindowInteropHelper(window).Handle;
                    Check(GetWindowRect(hwnd, out var native));
                    Pump(100); var middle = scene.SurfaceBounds;
                    Check(middle.Height > initial.Height && middle.Height < scene.TargetSurfaceBounds.Height);
                    Check(middle.Width == initial.Width && (scene.AtTop ? middle.Top == initial.Top : middle.Bottom == initial.Bottom));
                    window.Preview(State(count: 3), mode);
                    Check(scene.SurfaceBounds == middle); // Polling must preserve the unfolding frame.
                    Pump(1400);
                    Check(!scene.Animating && scene.SurfaceBounds == scene.TargetSurfaceBounds);
                    Check(GetWindowRect(hwnd, out var after) && after.Equals(native));
                    window.Hide();
                }
            }
            finally { window.Close(); }
        });
        run("Tray deactivation and selection form one toggle without dropping rapid consecutive clicks", () =>
        {
            var window = new FloatingWindow(false) { Opacity = 0, ShowActivated = false };
            try
            {
                window.Show(); window.Preview(State(), 1); Pump(1200);
                var scene = (PoolScene)window.FindName("Scene");
                var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
                var point = new System.Drawing.Point(area.Right - 30, area.Bottom - 10);
                for (var i = 0; i < 8; i++)
                {
                    window.DismissOnDeactivation(trayPress: true); Check(window.IsVisible);
                    window.HandleTrayPress();
                    if (i == 0) Pump(80); // The release fallback can run before Explorer's selection callback.
                    window.HandleTrayClick(point); Check(!window.IsVisible && !scene.Animating);
                    window.HandleTrayPress();
                    window.HandleTrayClick(point); Check(window.IsVisible && scene.EntranceProgress == 0);
                }
                window.DismissOnDeactivation(); Check(!window.IsVisible);
                window.HandleTrayPress();
                window.HandleTrayClick(point); Check(window.IsVisible);
                window.Hide(); window.ShowActivated = false; window.Show();
                window.DismissOnDeactivation(trayPress: true);
                // This test synthesizes deactivation rather than moving real OS focus.
                // Supply the released/inactive state to the same timer handler so an
                // unrelated foreground window cannot make the assertion intermittent.
                window.CompleteTrayPress(leftPressed: false, active: false);
                Check(!window.IsVisible); // A canceled tray drag has no selection: release still dismisses.
                window.HandleTrayPress(); window.HandleTrayClick(point); Check(window.IsVisible);
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
                Pump(1400); Check(!scene.Animating && scene.ScrollOffset > 0);
                window.Preview(State(count: 12), 0); Pump(1400);
                Check(Math.Abs(scene.ScrollOffset - previousScroll) < .001);
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
                window.Preview(State(), 1); Pump(1100);
                var compactHeight = scene.AccountTargets["a0"].Height;
                var preview = System.Text.Json.Nodes.JsonNode.Parse(State(count: 3).GetRawText())!;
                preview["accounts"]![1]!["name"] = "主账户";
                preview["accounts"]![2]!["name"] = "A";
                preview["accounts"]![1]!["subscriptionActiveUntil"] = "2030-01-02T03:04:05Z";
                foreach (var usage in preview["usage"]!.AsArray())
                    usage!["credits"] = System.Text.Json.Nodes.JsonNode.Parse("{\"hasCredits\":true,\"unlimited\":false,\"balance\":12.5}");
                var previewState = JsonSerializer.SerializeToElement(preview);
                window.Preview(previewState, 1); Pump(1100);
                Check(scene.AccountTargets["a0"].Height == compactHeight);
                var output = System.IO.Path.GetFullPath(".research/integrated-scene"); System.IO.Directory.CreateDirectory(output);
                foreach (var mode in new[] { 0, 1, 2 })
                {
                    window.Preview(previewState, mode); Pump(1100); scene.UpdateLayout();
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
    private static bool IsAppCloaked(nint hwnd)
    {
        Check(DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out var cloak, sizeof(int)) == 0);
        return (cloak & 1 /* DWM_CLOAKED_APP */) != 0;
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { internal int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out WindowRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
