using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using CodexPool.Floating;

internal static class DesktopStartupTests
{
    internal static void Run(Action<string, Action> run)
    {
        run("Background startup never requests a card or menu, including repeated tray launch", () =>
        {
            foreach (var args in new[] { Array.Empty<string>(), new[] { "--tray" }, new[] { "--widget" }, new[] { "--proxy-tray" } })
                Check(!App.ShouldShow(args));
            Check(App.ShouldShow(["--tray", "--show"]) && App.ShouldShow(["--review"]));
            Check(App.ShouldOpenDashboard([]) && !App.ShouldOpenDashboard(["--proxy-tray"]) && !App.ShouldOpenDashboard(["--show"]));
            foreach (var pinned in new[] { false, true })
            {
                var window = new FloatingWindow(false);
                try
                {
                    if (pinned) window.TogglePinned();
                    var visibleFrames = 0;
                    window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.AddHook(
                        (nint hwnd, int message, nint wp, nint lp, ref bool handled) =>
                        { if (IsWindowVisible(hwnd)) visibleFrames++; return 0; });
                    window.InitializeHidden(); window.InitializeHidden();
                    Check(!window.IsVisible && !IsWindowVisible(new WindowInteropHelper(window).Handle) && visibleFrames == 0);
                }
                finally { window.Close(); }
            }
        });
        run("Control authentication is per request and recovers after credential read failure", () =>
        {
            var reads = 0; var calls = 0;
            using var client = new LocalControlClient("unused", new Handler(request =>
            {
                calls++;
                Check(request.Headers.Authorization?.Parameter == "fixture-" + reads);
                return Reply(HttpStatusCode.OK, "{}");
            }), () => { reads++; if (reads == 1) throw new System.IO.IOException(); return "fixture-" + reads; });
            var failed = false;
            try { using var response = client.GetAsync("/status", CancellationToken.None).GetAwaiter().GetResult(); }
            catch (System.IO.IOException) { failed = true; }
            using var get = client.GetAsync("/status", CancellationToken.None).GetAwaiter().GetResult();
            using var post = client.PostAsync("/settings", new { autoSwitch = false }, CancellationToken.None).GetAwaiter().GetResult();
            Check(failed && reads == 3 && calls == 2);
        });
        run("Tray status recovers from unauthorized and broken responses without an event stream", () =>
        {
            var calls = 0;
            var window = new FloatingWindow(false, new Handler(request =>
            {
                Check(request.RequestUri!.AbsolutePath == "/status");
                calls++;
                return calls == 1 ? Reply(HttpStatusCode.Unauthorized, "{}")
                    : calls == 3 ? Reply(HttpStatusCode.OK, "{broken") : Reply(HttpStatusCode.OK, State());
            }), () => "fixture");
            try
            {
                foreach (var online in new[] { false, true, false, true })
                {
                    window.RefreshStatusAsync().GetAwaiter().GetResult();
                    var menu = window.BuildTrayMenu();
                    Check(menu.Single(x => x.Label == "刷新额度").Enabled);
                    Check(menu.Single(x => x.Label == "打开账户管理与用量").Enabled);
                    Check((menu.SingleOrDefault(x => x.Label == "关闭自动切换")?.Enabled == true) == online);
                }
                Check(calls == 4 && !window.IsVisible);
            }
            finally { window.Close(); }
        });
        run("Quota action works before first status and releases busy state after a failed request", () =>
        {
            var paths = new List<string>();
            var fail = true;
            var window = new FloatingWindow(false, new Handler(request =>
            {
                var path = request.RequestUri!.AbsolutePath; paths.Add(path);
                return fail ? Reply(HttpStatusCode.ServiceUnavailable, "{}") : Reply(HttpStatusCode.OK, path == "/status" ? State() : "[]");
            }), () => "fixture");
            try
            {
                window.RefreshQuotaAsync(false).GetAwaiter().GetResult();
                Check(window.BuildTrayMenu().Single(x => x.Label == "刷新额度").Enabled);
                fail = false;
                window.RefreshQuotaAsync(false).GetAwaiter().GetResult();
                Check(paths.SequenceEqual(new[] { "/usage", "/usage", "/status" }));
                Check(window.BuildTrayMenu().Single(x => x.Label == "关闭自动切换").Enabled && !window.IsVisible);
            }
            finally { window.Close(); }
        });
        run("Quota refresh reports per-account failures instead of falsely announcing success", () =>
        {
            Check(FloatingWindow.QuotaRefreshNotice(JsonSerializer.SerializeToElement(new { quotaRefreshErrors = new[] { new { code = "quota_network_unavailable" } } })).StartsWith("1 个账户额度更新失败"));
            Check(FloatingWindow.QuotaRefreshNotice(JsonSerializer.SerializeToElement(new { quotaRefreshErrors = Array.Empty<object>() })) == "额度已更新");
        });
    }

    private static string State() => JsonSerializer.Serialize(new { activeAccountId = (string?)null, automaticSwitching = true,
        accounts = Array.Empty<object>(), usage = Array.Empty<object>(), inFlightRequests = Array.Empty<object>() });
    private static HttpResponseMessage Reply(HttpStatusCode code, string content) => new(code) { Content = new StringContent(content) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException(); }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
}
