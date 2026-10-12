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
        owner = new Mutex(true, "Local\\CodexPoolFloating-" + Environment.UserName, out var first);
        var signalName = "Local\\CodexPoolFloatingShow-" + Environment.UserName;
        if (!first)
        {
            try { using var signal = EventWaitHandle.OpenExisting(signalName); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            owner.Dispose(); owner = null; Shutdown(); return;
        }
        var window = new FloatingWindow { ShowInTaskbar = e.Args.Contains("--review", StringComparer.Ordinal) };
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
        showListener = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => window.Dispatcher.BeginInvoke(() =>
        { window.Show(); window.Activate(); }), null, Timeout.Infinite, false);
        MainWindow = window; window.Show();
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        showListener?.Unregister(null); showSignal?.Dispose();
        if (owner is not null) { owner.ReleaseMutex(); owner.Dispose(); }
        base.OnExit(e);
    }
}
