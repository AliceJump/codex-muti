using System.Diagnostics;
using System.IO;

namespace CodexPool.Floating;

internal static class DesktopClientLauncher
{
    internal static ProcessStartInfo StartInfo(string root, bool accountPanel)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException();
        if (PortableInstallation.Exists(root)) return PortableInstallation.StartInfo(root, accountPanel);
        var script = Path.Combine(Path.GetFullPath(root), "scripts", accountPanel ? "open-widget.ps1" : "start-proxy-tray.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException();
        var start = new ProcessStartInfo("pwsh.exe") { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root };
        foreach (var argument in new[] { "-NoProfile", "-File", script }) start.ArgumentList.Add(argument);
        if (accountPanel)
            foreach (var argument in new[] { "-Show", "-NoDaemonStart", "-SkipProxyTray" }) start.ArgumentList.Add(argument);
        return start;
    }

    internal static async Task OpenAsync(bool accountPanel, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var name = accountPanel ? "CodexPoolWidget" : "CodexPoolProxyTray";
        try
        {
            using var signal = EventWaitHandle.OpenExisting("Local\\" + name + "Show-" + Environment.UserName);
            signal.Set(); return;
        }
        catch (WaitHandleCannotBeOpenedException) { }
        var start = StartInfo(Environment.GetEnvironmentVariable("CODEX_POOL_ROOT") ?? "", accountPanel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        if (PortableInstallation.Exists(start.WorkingDirectory)) return; // Native clients remain running independently.
        // Disposing this handle on cancellation never terminates a launched client or its daemon.
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0) throw new InvalidOperationException();
    }
}
