using System.Threading;

namespace CodexPool.Floating;

public partial class App : System.Windows.Application
{
    private Mutex? owner;
    private EventWaitHandle? showSignal;
    private RegisteredWaitHandle? showListener;
    private EventWaitHandle? dashboardSignal;
    private RegisteredWaitHandle? dashboardListener;
    private ProxyTrayController? proxyTray;
    private readonly bool startClients;
    public App() : this(true) { }
    internal App(bool startClients) => this.startClients = startClients;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!startClients) return; // Isolated UI tests load the actual styles without starting clients.
        if (e.Args.Length == 2 && e.Args[0] == "--package-check")
        {
            // Offline smoke check: no tray, daemon, account state, browser, or official CLI is opened.
            try
            {
                PortableInstallation.Validate(AppContext.BaseDirectory);
                System.IO.File.WriteAllText(e.Args[1], "{\"package\":\"ok\",\"clientsStarted\":false}"); Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        var proxyMode = e.Args.Contains("--proxy-tray", StringComparer.Ordinal);
        var portable = PortableInstallation.Exists(AppContext.BaseDirectory);
        if (portable)
        {
            try { PortableInstallation.Configure(AppContext.BaseDirectory, proxyMode); }
            catch { System.Windows.MessageBox.Show("发行包不完整，请重新解压完整的软件包。", "Codex Pool"); Shutdown(1); return; }
        }
        var instance = proxyMode ? "CodexPoolProxyTray" : "CodexPoolWidget";
        var signalName = "Local\\" + instance + "Show-" + Environment.UserName;
        // Hold the event before claiming ownership so a concurrent launcher can
        // queue its wake-up even while the first window is being constructed.
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
        if (!proxyMode) dashboardSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + instance + "Dashboard-" + Environment.UserName);
        owner = new Mutex(true, "Local\\" + instance + "-" + Environment.UserName, out var first);
        if (!first)
        {
            // Background startup is idempotent: it must not pop an existing menu/card.
            if (ShouldShow(e.Args)) showSignal.Set();
            if (ShouldOpenDashboard(e.Args)) dashboardSignal?.Set();
            owner.Dispose(); owner = null; Shutdown(); return;
        }
        if (proxyMode)
        {
            proxyTray = new ProxyTrayController(Dispatcher);
            showListener = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(proxyTray.ShowMenu), null, Timeout.Infinite, false);
            return;
        }
        var window = new FloatingWindow() { ShowInTaskbar = e.Args.Contains("--review", StringComparer.Ordinal) };
        showListener = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => window.Dispatcher.BeginInvoke(window.ShowFromLauncher), null, Timeout.Infinite, false);
        MainWindow = window;
        dashboardListener = ThreadPool.RegisterWaitForSingleObject(dashboardSignal!, (_, _) => window.Dispatcher.BeginInvoke(window.OpenDashboardOnStartup), null, Timeout.Infinite, false);
        window.InitializeHidden();
        if (ShouldShow(e.Args)) window.ShowFromLauncher();
        if (ShouldOpenDashboard(e.Args)) window.OpenDashboardOnStartup();
        if (portable && !e.Args.Contains("--account-only", StringComparer.Ordinal)) OpenPortableProxyTray();
    }
    internal static bool ShouldShow(string[] args) => args.Any(x => x is "--show" or "--review");
    internal static bool ShouldOpenDashboard(string[] args) => !args.Any(x => x is "--proxy-tray" or "--tray" or "--widget" or "--show" or "--review" or "--account-only");
    private async void OpenPortableProxyTray()
    {
        try { await DesktopClientLauncher.OpenAsync(accountPanel: false, CancellationToken.None, show: false); }
        catch { System.Windows.MessageBox.Show("账户面板已打开，代理控制图标尚未启动，可从账户菜单再次打开。", "Codex Pool"); }
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        proxyTray?.Dispose();
        showListener?.Unregister(null); showSignal?.Dispose();
        dashboardListener?.Unregister(null); dashboardSignal?.Dispose();
        if (owner is not null) { owner.ReleaseMutex(); owner.Dispose(); }
        base.OnExit(e);
    }
}
