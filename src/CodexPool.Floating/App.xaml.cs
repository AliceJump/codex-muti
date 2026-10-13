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
        var widget = e.Args.Any(x => x is "--tray" or "--widget");
        var instance = widget ? "CodexPoolWidget" : "CodexPoolFloating";
        owner = new Mutex(true, "Local\\" + instance + "-" + Environment.UserName, out var first);
        var signalName = "Local\\" + instance + "Show-" + Environment.UserName;
        if (!first)
        {
            try { using var signal = EventWaitHandle.OpenExisting(signalName); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            owner.Dispose(); owner = null; Shutdown(); return;
        }
        var window = new FloatingWindow(widget) { ShowInTaskbar = e.Args.Contains("--review", StringComparer.Ordinal) };
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
        showListener = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => window.Dispatcher.BeginInvoke(window.ShowFromLauncher), null, Timeout.Infinite, false);
        MainWindow = window;
        // Initialize the HWND, tray and event subscription without flashing a desktop card.
        window.ShowActivated = !widget;
        if (widget) window.Opacity = 0;
        window.Show();
        if (widget) { window.Hide(); window.Opacity = 1; }
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        showListener?.Unregister(null); showSignal?.Dispose();
        if (owner is not null) { owner.ReleaseMutex(); owner.Dispose(); }
        base.OnExit(e);
    }
}
