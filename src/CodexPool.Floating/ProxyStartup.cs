using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace CodexPool.Floating;

internal static class ProxyStartup
{
    public static async Task EnsureRunningAsync(CancellationToken token, bool explicitStart = false)
    {
        if (!explicitStart && !CodexPool.Daemon.ProxyServiceIntent.AllowsAutomaticStart(CodexPool.Daemon.ProxyServiceIntent.DataDirectory)) return;
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.GetAsync("http://127.0.0.1:3210/health", token);
            if (explicitStart) response.EnsureSuccessStatusCode();
            // Any HTTP response means a listener exists. Never replace an existing service.
            return;
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException socket
            && socket.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { if (explicitStart) throw new IOException("Daemon status did not respond."); return; }
        token.ThrowIfCancellationRequested();
        if (!explicitStart && Environment.GetEnvironmentVariable("CODEX_POOL_DAEMON_AUTOSTART") == "0") return;
        var root = Environment.GetEnvironmentVariable("CODEX_POOL_ROOT") ?? "";
        var pointer = Path.Combine(root, ".local", "current-build.json");
        string? current = null;
        if (Path.IsPathFullyQualified(root) && File.Exists(pointer))
        {
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(pointer, token));
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("path", out var value) || value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { } build) throw new IOException("Invalid daemon build path.");
                var candidate = Path.Combine(build, "Daemon", "CodexPool.Daemon.exe");
                if (!ProxyDaemonProcess.OwnedPath(candidate, root)) throw new IOException("Invalid daemon build path.");
                current = candidate;
            }
            catch (JsonException) { throw new IOException("Invalid daemon build pointer."); }
        }
        var candidates = new[]
        {
            PortableInstallation.Exists(root) ? PortableInstallation.Daemon(root) : current ?? "",
            Environment.GetEnvironmentVariable("CODEX_POOL_DAEMON") ?? "",
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Daemon", "CodexPool.Daemon.exe")),
            Path.Combine(AppContext.BaseDirectory, "CodexPool.Daemon.exe")
        };
        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null) { if (explicitStart) throw new FileNotFoundException(); return; }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("serve");
        // User-facing startup permits migration and honors the persisted automatic-switch setting.
        start.ArgumentList.Add("--live-boundary-probe");
        using var child = Process.Start(start);
        // The daemon is independent of the window. Its own exclusive lock resolves startup races.
        if (!explicitStart) return;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(200, token);
            try { using var response = await client.GetAsync("http://127.0.0.1:3210/health", token); response.EnsureSuccessStatusCode(); return; }
            catch (HttpRequestException) { }
        }
        throw new IOException("Daemon did not become ready.");
    }
}
