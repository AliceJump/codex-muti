using System.Threading;

namespace CodexPool.Floating;

public partial class App : System.Windows.Application
{
    private Mutex? owner;
    private EventWaitHandle? showSignal;
    private RegisteredWaitHandle? showListener;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        const string instance = "CodexPoolWidget";
        var signalName = "Local\\" + instance + "Show-" + Environment.UserName;
        // Hold the event before claiming ownership so a concurrent launcher can
        // queue its wake-up even while the first window is being constructed.
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
        owner = new Mutex(true, "Local\\" + instance + "-" + Environment.UserName, out var first);
        if (!first)
        {
            showSignal.Set();
            owner.Dispose(); owner = null; Shutdown(); return;
        }
        var window = new FloatingWindow() { ShowInTaskbar = e.Args.Contains("--review", StringComparer.Ordinal) };
        showListener = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => window.Dispatcher.BeginInvoke(window.ShowFromLauncher), null, Timeout.Infinite, false);
        MainWindow = window;
        // Initialize the HWND, tray and event subscription without flashing a desktop card.
        window.ShowActivated = false;
        window.Opacity = 0;
        window.Show(); window.Hide(); window.Opacity = 1; window.ShowActivated = true;
        if (window.Pinned || !e.Args.Any(x => x is "--tray" or "--widget")) window.ShowFromLauncher();
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        showListener?.Unregister(null); showSignal?.Dispose();
        if (owner is not null) { owner.ReleaseMutex(); owner.Dispose(); }
        base.OnExit(e);
    }
}
