using System.Diagnostics;
using System.Text;
using CodexPool.Bridge;
using CodexPool.Daemon;

// CODEX_CLI_PATH selects this executable in Desktop. The actual official CLI remains
// responsible for authentication, tools, approvals, history, environments and worktrees.
try
{
    var path = Environment.GetEnvironmentVariable("CODEX_POOL_UPSTREAM_CLI");
    if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path)
        || Path.GetFullPath(path).Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        throw new BridgeFailure("upstream_cli_missing");
    var enabled = Environment.GetEnvironmentVariable("CODEX_POOL_COMPACT_BRIDGE") == "1";
    var index = Array.IndexOf(args, "app-server");
    var isServer = index >= 0 && !args.Skip(index + 1).Any(x => x is "daemon" or "proxy" or "generate-ts" or "generate-json-schema" or "help");
    var relay = enabled && isServer && !args.Any(x => x is "--help" or "-h");
    if (relay)
    {
        var versionStart = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        versionStart.ArgumentList.Add("--version");
        using var version = Process.Start(versionStart) ?? throw new BridgeFailure("upstream_version_unknown");
        try
        {
            var line = await version.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await version.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (version.ExitCode != 0 || line?.Trim() != "codex-cli 0.162.1") throw new BridgeFailure("upstream_version_not_validated");
        }
        finally { if (!version.HasExited) version.Kill(entireProcessTree: true); }
        if (args.Any(x => x == "--remote-control" || x.StartsWith("--remote-control=", StringComparison.Ordinal)))
            throw new BridgeFailure("additional_client_not_supported");
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--listen" && (i + 1 >= args.Length || args[i + 1] != "stdio://")
                || args[i].StartsWith("--listen=", StringComparison.Ordinal) && args[i] != "--listen=stdio://")
                throw new BridgeFailure("sole_stdio_required");
        }
    }
    var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = relay, RedirectStandardOutput = relay };
    if (relay) { start.StandardInputEncoding = new UTF8Encoding(false, true); start.StandardOutputEncoding = new UTF8Encoding(false, true); }
    foreach (var argument in args) start.ArgumentList.Add(argument);
    start.Environment.Remove("CODEX_CLI_PATH");
    if (relay) start.Environment["CODEX_INTERNAL_APP_SERVER_REMOTE_CONTROL_DISABLED"] = "1";
    using var child = Process.Start(start) ?? throw new BridgeFailure("upstream_start_failed");
    if (!relay) { await child.WaitForExitAsync(); return child.ExitCode; }
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(3) };
    ICompactionControl control;
    try
    {
        var directory = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPool");
        http.DefaultRequestHeaders.Authorization = new("Bearer", Vault.ReadLocalKey(directory));
        control = new DaemonCompactionControl(http);
    }
    catch { control = new UnavailableControl(); }
    // Console.In is a synchronized reader whose async calls may block while filling
    // the requested character buffer. Use the raw pipe stream for incremental JSONL.
    using var clientInput = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
    await using var clientOutput = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
    try
    {
        await new AppServerBridge(control).RunAsync(clientInput, clientOutput, child.StandardOutput, child.StandardInput, CancellationToken.None);
        return 0;
    }
    finally
    {
        // Only this wrapper's child is stopped; never discover or terminate Desktop/other daemons.
        child.StandardInput.Close();
        if (!child.HasExited)
        {
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }
}
catch (BridgeFailure ex) { Console.Error.WriteLine("Codex Pool 桥接停止：" + ex.Code); return 1; }
catch { Console.Error.WriteLine("Codex Pool 桥接停止：bridge_failed"); return 1; }

internal sealed class UnavailableControl : ICompactionControl
{
    public Task<CodexPool.Core.CompactionAdvice?> AdviceAsync(string model, CancellationToken token) => Task.FromResult<CodexPool.Core.CompactionAdvice?>(null);
    public Task<CodexPool.Core.CompactionHold?> ReserveAsync(CodexPool.Core.CompactionAdvice advice, CancellationToken token) => Task.FromResult<CodexPool.Core.CompactionHold?>(null);
    public Task FinishAsync(string id, string outcome, CancellationToken token) => Task.CompletedTask;
}
