using System.Threading;

namespace CodexPool.Floating;

public partial class App : System.Windows.Application
{
    private Mutex? owner;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        owner = new Mutex(true, "Local\\CodexPoolFloating-" + Environment.UserName, out var first);
        if (!first) { owner.Dispose(); owner = null; Shutdown(); return; }
        var window = new FloatingWindow { ShowInTaskbar = e.Args.Contains("--review", StringComparer.Ordinal) };
        MainWindow = window; window.Show();
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    { if (owner is not null) { owner.ReleaseMutex(); owner.Dispose(); } base.OnExit(e); }
}
