using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CodexPool.Floating;

internal sealed record ProxyDaemonIdentity(int Pid, long StartTicks, string Path);

internal static class ProxyDaemonProcess
{
    internal static bool OwnedPath(string path, string root) => !string.IsNullOrWhiteSpace(root) && System.IO.Path.IsPathFullyQualified(root)
        && !string.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathFullyQualified(path)
        &&
        System.IO.Path.GetFileName(path).Equals("CodexPool.Daemon.exe", StringComparison.OrdinalIgnoreCase)
        && System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(System.IO.Path.Combine(root, ".local")) + "\\", StringComparison.OrdinalIgnoreCase);

    internal static ProxyDaemonIdentity Capture(string root)
    {
        var pid = ListenerPid();
        using var process = Process.GetProcessById(pid);
        var path = process.MainModule?.FileName ?? "";
        if (!OwnedPath(path, root) || process.ProcessName != "CodexPool.Daemon") throw new InvalidOperationException();
        return new(pid, process.StartTime.ToUniversalTime().Ticks, path);
    }

    internal static async Task StopLegacyAsync(ProxyDaemonIdentity expected, string root)
    {
        // Older daemons have no shutdown API. Only an explicitly selected, authenticated
        // local proxy may be stopped, and its port/PID/path/start time must still match.
        if (ListenerPid() != expected.Pid) throw new InvalidOperationException();
        using var process = Process.GetProcessById(expected.Pid);
        if (!OwnedPath(expected.Path, root) || process.MainModule?.FileName != expected.Path
            || process.StartTime.ToUniversalTime().Ticks != expected.StartTicks) throw new InvalidOperationException();
        process.Kill(); // Never entireProcessTree: Codex and UI clients are independent.
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    internal static async Task WaitForExitAsync(ProxyDaemonIdentity expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.Pid);
            if (process.StartTime.ToUniversalTime().Ticks == expected.StartTicks)
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
        }
        catch (ArgumentException) { } // The captured process already exited.
    }

    private static int ListenerPid()
    {
        var size = 0;
        _ = GetExtendedTcpTable(0, ref size, false, 2, 3, 0); // IPv4, owner PID listeners
        if (size is < 4 or > 4 * 1024 * 1024) throw new InvalidOperationException();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0) != 0) throw new InvalidOperationException();
            var count = Marshal.ReadInt32(buffer);
            if (count < 0 || count > (size - 4) / 24) throw new InvalidOperationException();
            var pids = new HashSet<int>();
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 24;
                var address = unchecked((uint)Marshal.ReadInt32(row, 4));
                var port = (ushort)Marshal.ReadInt32(row, 8);
                var hostPort = (port & 255) * 256 + (port >> 8);
                if (address == 0x0100007f && hostPort == 3210) pids.Add(Marshal.ReadInt32(row, 20));
            }
            return pids.Count == 1 ? pids.Single() : throw new InvalidOperationException();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
}
