using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace CodexPool.Floating;

internal static class ProxyStartup
{
    public static async Task EnsureRunningAsync(CancellationToken token)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.GetAsync("http://127.0.0.1:3210/health", token);
            // Any HTTP response means a listener exists. Never replace an existing service.
            return;
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException socket
            && socket.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return; }
        token.ThrowIfCancellationRequested();
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Daemon", "CodexPool.Daemon.exe")),
            Path.Combine(AppContext.BaseDirectory, "CodexPool.Daemon.exe")
        };
        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null) return;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("serve");
        using var child = Process.Start(start);
        // The daemon is independent of the window. Its own exclusive lock resolves startup races.
    }
}
