using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CodexPool.Floating;

internal static class PortableInstallation
{
    internal const string Manifest = "codex-pool.json";
    internal static bool Exists(string root) => Path.IsPathFullyQualified(root) && File.Exists(Path.Combine(root, Manifest));
    internal static string Daemon(string root) => Path.Combine(root, "Daemon", "CodexPool.Daemon.exe");
    internal static void Validate(string root)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, Manifest)));
        if (json.RootElement.GetProperty("format").GetInt32() != 1
            || !File.Exists(Path.Combine(root, "CodexPool.exe")) || !File.Exists(Daemon(root)))
            throw new IOException("发行包不完整，请重新解压完整的软件包。");
    }
    internal static void Configure(string root, bool proxy)
    {
        Validate(root);
        Environment.SetEnvironmentVariable("CODEX_POOL_ROOT", Path.GetFullPath(root));
        Environment.SetEnvironmentVariable("CODEX_POOL_DAEMON", Daemon(root));
        if (proxy) Environment.SetEnvironmentVariable("CODEX_POOL_DAEMON_AUTOSTART", "0");
    }
    internal static ProcessStartInfo StartInfo(string root, bool accountPanel)
    {
        Validate(root);
        var start = new ProcessStartInfo(Path.Combine(root, "CodexPool.exe")) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root };
        start.Environment["CODEX_POOL_ROOT"] = root;
        start.Environment["CODEX_POOL_DAEMON"] = Daemon(root);
        start.Environment["CODEX_POOL_DAEMON_AUTOSTART"] = "0";
        start.ArgumentList.Add(accountPanel ? "--show" : "--proxy-tray");
        if (accountPanel) start.ArgumentList.Add("--account-only");
        return start;
    }
}
