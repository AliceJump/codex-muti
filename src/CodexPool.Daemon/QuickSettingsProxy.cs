using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace CodexPool.Daemon;

internal sealed record ProxySnapshot(bool Known, bool Running, JsonObject? Status, string Message);
internal sealed record ProxyOperation(bool Success, string Message);
internal sealed record ProxyIdentity(int Pid, long Started, string Path);

internal sealed class QuickSettingsProxy
{
    private readonly string initialPath, directory, allowedBuildRoot;
    private readonly HttpClient http;
    private ProxyIdentity? observed;
    private string lastExecutable;
    private bool inheritedBoundaryProbe;

    internal QuickSettingsProxy(string servicePath, string dataDirectory, HttpClient http)
    {
        if (!Path.IsPathFullyQualified(servicePath) || !File.Exists(servicePath)
            || !Path.GetFileName(servicePath).Equals("CodexPool.Daemon.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Daemon installation is unavailable.");
        initialPath = Path.GetFullPath(servicePath); directory = dataDirectory; this.http = http;
        lastExecutable = initialPath;
        var local = new DirectoryInfo(Path.GetDirectoryName(initialPath)!);
        while (local is not null && local.Name != ".local") local = local.Parent;
        allowedBuildRoot = local is null ? "" : local.FullName + Path.DirectorySeparatorChar;
    }

    private bool Owned(string path) => Path.GetFileName(path).Equals("CodexPool.Daemon.exe", StringComparison.OrdinalIgnoreCase)
        && (path.Equals(initialPath, StringComparison.OrdinalIgnoreCase)
            || allowedBuildRoot.Length != 0 && Path.GetFullPath(path).StartsWith(allowedBuildRoot, StringComparison.OrdinalIgnoreCase));

    private ProxyIdentity? Capture()
    {
        var pid = ListenerPid();
        if (pid is null) return null;
        using var process = Process.GetProcessById(pid.Value);
        using var self = Process.GetCurrentProcess();
        var path = process.MainModule?.FileName ?? "";
        if (process.SessionId != self.SessionId || !Owned(path)) throw new IOException("Port is not owned by this installation.");
        if (!OpenProcessToken(process.Handle, 8, out var raw)) throw new IOException("Daemon identity unavailable.");
        using (raw)
        using (var identity = new WindowsIdentity(raw.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
            if (identity.User != current.User) throw new IOException("Daemon user differs.");
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, path);
    }

    private static bool Exists(ProxyIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            return process.StartTime.ToUniversalTime().Ticks == identity.Started && !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private bool LockAvailable()
    {
        if (!File.Exists(Path.Combine(directory, "daemon.lock"))) return true;
        try { using var file = new FileStream(Path.Combine(directory, "daemon.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

    internal async Task<ProxySnapshot> ReadAsync(CancellationToken token)
    {
        try
        {
            var identity = Capture();
            if (identity is null)
            {
                if (observed is { } previous && Exists(previous) || !LockAvailable())
                    return new(false, observed is not null, null, "代理正在启动或停止，状态等待确认");
                observed = null;
                return new(true, false, null, "代理已停止");
            }
            var status = await QuickSettingsRegistration.ReadAsync(http, directory, "/status", token);
            if (Capture() != identity) return new(false, true, null, "代理实例已变化，等待确认");
            observed = identity;
            lastExecutable = identity.Path;
            inheritedBoundaryProbe = status["boundaryProbe"]?.GetValue<bool>() == true;
            return new(true, true, status, "代理运行中");
        }
        catch (Exception error) when (QuickSettingsRegistration.Unavailable(error))
        { return new(false, observed is not null, null, "无法确认本机代理状态"); }
    }

    internal async Task<ProxyOperation> SetRunningAsync(bool desired, CancellationToken token)
    {
        try
        {
            var before = await ReadAsync(token);
            if (!before.Known) return new(false, before.Message);
            if (before.Running == desired) return new(true, before.Message);
            if (desired)
            {
                var executable = lastExecutable;
                if (!Owned(executable) || !File.Exists(executable) || Capture() is not null || !LockAvailable())
                    return new(false, "已有代理实例或安装状态已变化");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable)! };
                start.ArgumentList.Add("serve");
                // Preserve a previously authenticated daemon's launch mode;
                // never add a migration bypass merely to make the tile work.
                if (inheritedBoundaryProbe) start.ArgumentList.Add("--live-boundary-probe");
                start.Environment["CODEX_POOL_DATA_DIR"] = directory;
                using var child = Process.Start(start) ?? throw new IOException("Daemon did not start.");
                for (var i = 0; i < 50; i++)
                {
                    await Task.Delay(200, token);
                    var state = await ReadAsync(token);
                    if (state.Known && state.Running) { ProxyServiceIntent.Confirm(directory, true); return new(true, "代理已启动"); }
                    if (child.HasExited) break;
                }
                return new(false, "启动结果尚未确认，未自动重试");
            }
            var identity = Capture();
            if (identity is null || before.Status is null || !LocalCommands.CanShutdown(System.Text.Json.JsonSerializer.SerializeToElement(before.Status)))
                return new(false, "代理繁忙或授权状态未知，保持运行");
            if (Capture() != identity) return new(false, "代理实例已变化，未提交关闭");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/control/shutdown");
            request.Headers.Authorization = new("Bearer", Vault.ReadLocalKey(directory));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.Conflict) return new(false, "代理正忙，未关闭");
            if (response.StatusCode != HttpStatusCode.Accepted) return new(false, "代理未接受安全关闭");
            for (var i = 0; i < 60; i++)
            {
                await Task.Delay(200, token);
                if (!Exists(identity) && Capture() is null && LockAvailable())
                { observed = null; ProxyServiceIntent.Confirm(directory, false); return new(true, "代理已停止"); }
            }
            return new(false, "关闭仍待确认，未强制终止进程");
        }
        catch (Exception error) when (QuickSettingsRegistration.Unavailable(error))
        { return new(false, "操作结果未确认，请查看代理状态"); }
    }

    internal async Task<ProxyOperation> ActivateAccountAsync(string id, CancellationToken token)
    {
        try
        {
            var before = await ReadAsync(token);
            if (!before.Known || !before.Running || before.Status is null) return new(false, "代理未运行，无法切换账户");
            var accounts = before.Status["accounts"]!.AsArray();
            var account = accounts.OfType<JsonObject>().FirstOrDefault(x => x["id"]?.GetValue<string>() == id);
            if (account is null || !QuickSettingsRegistration.AccountSelectable(account)) return new(false, "该账户当前不可用");
            var identity = Capture();
            if (identity is null) return new(false, "代理实例已变化");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/accounts/" + Uri.EscapeDataString(id) + "/activate");
            request.Headers.Authorization = new("Bearer", Vault.ReadLocalKey(directory));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return new(false, "账户切换被代理拒绝，保留当前账户");
            var after = await ReadAsync(token);
            return after.Known && after.Status?["activeAccountId"]?.GetValue<string>() == id
                ? new(true, "账户已切换，仅影响新请求") : new(false, "账户切换结果未确认");
        }
        catch (Exception error) when (QuickSettingsRegistration.Unavailable(error))
        { return new(false, "账户切换结果未确认，未自动重试"); }
    }

    private static int? ListenerPid()
    {
        var size = 0; _ = GetExtendedTcpTable(0, ref size, false, 2, 3, 0);
        if (size is < 4 or > 4 * 1024 * 1024) throw new IOException("Listener query failed.");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0) != 0) throw new IOException("Listener query failed.");
            var count = Marshal.ReadInt32(buffer);
            if (count < 0 || count > (size - 4) / 24) throw new IOException("Invalid listener table.");
            var ids = new HashSet<int>();
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 24;
                var port = (ushort)Marshal.ReadInt32(row, 8);
                if ((port & 255) * 256 + (port >> 8) != 3210) continue;
                if (unchecked((uint)Marshal.ReadInt32(row, 4)) != 0x0100007f) throw new IOException("Unexpected listener.");
                ids.Add(Marshal.ReadInt32(row, 20));
            }
            return ids.Count == 0 ? null : ids.Count == 1 ? ids.Single() : throw new IOException("Ambiguous listener.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);
}
