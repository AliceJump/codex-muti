using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace CodexPool.Daemon;

// A separate, opt-in registration process. Only this process talks to the
// daemon; neither the broker nor Shell receives the daemon's management key.
internal static class QuickSettingsRegistration
{
    private const string Id = "codex-pool";
    private const int MaxFrame = 32768;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync(string[] args, string directory)
    {
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0]))
            return 2;
        var broker = Path.GetFullPath(args[0]);
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new("http://127.0.0.1:3210"), Timeout = TimeSpan.FromSeconds(2) };
        var proxy = new QuickSettingsProxy(args[1], directory, http);
        Task<ProxyOperation>? operation = null;
        ulong operationRequestId = 0;
        var operationNotice = "";
        var lastKnownRunning = false;
        var registered = false;
        var publishAfterRegister = false;
        ulong revision = 1;
        JsonObject? completion = null;
        JsonObject? previous = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (operation is { IsCompleted: true })
                    {
                        var result = await operation;
                        operationNotice = result.Message;
                        completion = new() { ["requestId"] = operationRequestId, ["success"] = result.Success };
                        operation = null;
                    }
                    var state = await proxy.ReadAsync(stop.Token);
                    if (state.Known) lastKnownRunning = state.Running;
                    var current = Presentation(state, lastKnownRunning, operationNotice);
                    var changed = !JsonNode.DeepEquals(current, previous) || completion is not null || publishAfterRegister;
                    if (changed) ++revision;
                    previous = (JsonObject)current.DeepClone();
                    current["revision"] = revision;
                    JsonObject request;
                    if (!registered)
                    {
                        request = Message("plugin.register");
                        request["schemaVersion"] = 1;
                        request["permissions"] = new JsonArray("tile:register", "state:publish", "actions:receive");
                        request["entry"] = current;
                    }
                    else
                    {
                        request = Message("plugin.poll"); request["id"] = Id;
                        if (changed) request["state"] = current;
                        if (completion is not null) request["result"] = completion;
                    }
                    var response = await ExchangeAsync(request, broker, stop.Token);
                    if (response["protocol"]?.GetValue<int>() != 1 || response["type"]?.GetValue<string>() == "error")
                        throw new IOException("Registration rejected.");
                    publishAfterRegister = !registered;
                    registered = true; completion = null;
                    if (response["action"] is JsonObject action)
                    {
                        var requestId = action["requestId"]!.GetValue<ulong>();
                        var accepted = action["id"]?.GetValue<string>() == Id
                            && action["expectedRevision"]?.GetValue<ulong>() == revision;
                        if (accepted && operation is null && action["kind"]?.GetValue<string>() == "setEnabled")
                        {
                            operationNotice = "操作处理中…"; operationRequestId = requestId;
                            operation = proxy.SetRunningAsync(action["value"]!.GetValue<bool>(), stop.Token);
                        }
                        else if (accepted && operation is null && action["kind"]?.GetValue<string>() == "invoke"
                            && action["action"]?.GetValue<string>() == "activate-account" && action["argument"]?.GetValue<string>() is { } accountId)
                        {
                            operationNotice = "正在切换账户…"; operationRequestId = requestId;
                            operation = proxy.ActivateAccountAsync(accountId, stop.Token);
                        }
                        else
                            completion = new() { ["requestId"] = requestId, ["success"] = accepted && operation is null
                                && action["kind"]?.GetValue<string>() == "invoke" && action["action"]?.GetValue<string>() == "refresh" && state.Known };
                    }
                }
                catch (Exception e) when (Unavailable(e))
                {
                    // An unknown transport outcome never replays an action.
                    // Re-register only a newly read authoritative state.
                    registered = false; completion = null;
                }
                await Task.Delay(200, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { Console.CancelKeyPress -= cancel; }
        return 0;
    }

    internal static bool Unavailable(Exception e) => e is IOException or HttpRequestException or System.ComponentModel.Win32Exception or ArgumentException
        or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
        or InvalidOperationException or JsonException or OperationCanceledException;

    private static JsonObject Message(string type) => new() { ["protocol"] = 1, ["type"] = type };

    internal static async Task<JsonObject> ReadAsync(HttpClient http, string directory, string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", Vault.ReadLocalKey(directory));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new IOException("Status is too large.");
        await using var body = await response.Content.ReadAsStreamAsync(token);
        using var bounded = new MemoryStream();
        var buffer = new byte[4096]; int read;
        while ((read = await body.ReadAsync(buffer, token)) != 0)
        {
            if (bounded.Length + read > 1024 * 1024) throw new IOException("Status is too large.");
            bounded.Write(buffer, 0, read);
        }
        return JsonNode.Parse(bounded.ToArray()) as JsonObject ?? throw new JsonException();
    }

    internal static JsonObject Presentation(ProxySnapshot state, bool lastKnownRunning, string operationNotice)
    {
        var lines = new JsonArray();
        var rows = new JsonArray();
        if (state.Status is { } status)
        {
            var accounts = status["accounts"]!.AsArray();
            var usage = status["usage"] as JsonArray;
            var availability = status["accountAvailability"] as JsonArray;
            if(accounts.Count == 0) lines.Add("暂无账户，请先在 Codex Pool 中登录");
            foreach (var account in accounts.OfType<JsonObject>().Take(32))
            {
                var id = account["id"]!.GetValue<string>();
                var quota = usage?.OfType<JsonObject>().FirstOrDefault(x => x["accountId"]?.GetValue<string>() == id);
                var routing = availability?.OfType<JsonObject>().FirstOrDefault(x => x["accountId"]?.GetValue<string>() == id);
                rows.Add(new JsonObject {
                    ["id"] = id, ["title"] = account["name"]?.GetValue<string>() ?? "未命名账户",
                    ["subtitle"] = QuotaCaption(quota), ["action"] = "activate-account",
                    ["selected"] = status["activeAccountId"]?.GetValue<string>() == id,
                    ["enabled"] = state.Running && AccountSelectable(account) && routing?["available"]?.GetValue<bool>() != false
                });
            }
        }
        else lines.Add(state.Known ? "启动代理后查看可用账户" : "代理状态暂不可确认");
        if (!string.IsNullOrEmpty(operationNotice) && !operationNotice.StartsWith("代理已", StringComparison.Ordinal)
            && !operationNotice.StartsWith("账户已切换", StringComparison.Ordinal)) lines.Add(operationNotice);
        return new()
        {
            ["id"] = Id, ["name"] = "Codex Pool", ["icon"] = "bot", ["tileType"] = "split-toggle",
            ["summary"] = state.Message, ["added"] = false, ["online"] = true,
            ["enabled"] = state.Known ? state.Running : lastKnownRunning, ["canToggle"] = state.Known,
            ["lines"] = lines, ["rows"] = rows,
            ["actions"] = new JsonArray(new JsonObject { ["id"] = "refresh", ["label"] = "刷新状态", ["rowOnly"] = false },
                new JsonObject { ["id"] = "activate-account", ["label"] = "切换账户", ["rowOnly"] = true })
        };
    }

    internal static bool AccountSelectable(JsonObject account) => account["enabled"]?.GetValue<bool>() == true
        && account["paused"]?.GetValue<bool>() != true && account["drain"]?.GetValue<bool>() != true
        && account["state"]?.GetValue<string>() != "cooldown"
        && account["auth"]?.GetValue<string>() is not ("reauth_required" or "refresh_pending");

    internal static string QuotaCaption(JsonObject? quota)
    {
        if (quota?["windows"] is not JsonArray windows) return "5 小时：未知    每周：未知";
        string Window(int seconds, string label)
        {
            var value = windows.OfType<JsonObject>().FirstOrDefault(x => x["windowSeconds"]?.GetValue<int>() == seconds
                && (x["limitId"]?.GetValue<string>() ?? "codex") == "codex");
            if (value?["remainingPercent"] is not JsonValue remaining || !remaining.TryGetValue<double>(out var number)
                || !double.IsFinite(number) || number is < 0 or > 100) return label + "：未知";
            var current = quota["cacheState"]?.GetValue<string>() == "current"
                && DateTimeOffset.TryParse(value["resetAt"]?.GetValue<string>(), out var reset) && reset > DateTimeOffset.UtcNow;
            return $"{label}：{number:0.#}%" + (current ? "" : "（缓存）");
        }
        return Window(18000, "5 小时") + "    " + Window(604800, "每周");
    }

    private static async Task<JsonObject> ExchangeAsync(JsonObject request, string broker, CancellationToken token)
    {
        using var currentIdentity = WindowsIdentity.GetCurrent();
        using var self = Process.GetCurrentProcess();
        var user = currentIdentity.User?.Value ?? throw new IOException("User unavailable.");
        var session = self.SessionId;
        var name = $"Sessions\\{session}\\AppContainerNamedObjects\\{ShellPackageSid()}\\QuickSettingsExtensions.v1.{user}.{session}";
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        await pipe.ConnectAsync(deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new IOException("Server unavailable.");
        using var server = Process.GetProcessById((int)pid);
        if (server.SessionId != self.SessionId || !string.Equals(server.MainModule?.FileName, broker, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unexpected broker.");
        if (!OpenProcessToken(server.Handle, 8, out var serverToken)) throw new IOException("Server identity unavailable.");
        using (serverToken)
        using (var identity = new WindowsIdentity(serverToken.DangerousGetHandle()))
            if (identity.User?.Value != user) throw new IOException("Unexpected server identity.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        if (bytes.Length is 0 or > MaxFrame) throw new IOException("Invalid frame.");
        var size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        await pipe.WriteAsync(size, deadline.Token); await pipe.WriteAsync(bytes, deadline.Token);
        await pipe.ReadExactlyAsync(size, deadline.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is <= 0 or > MaxFrame) throw new IOException("Invalid frame.");
        var response = new byte[length]; await pipe.ReadExactlyAsync(response, deadline.Token);
        await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
        return JsonNode.Parse(response) as JsonObject ?? throw new JsonException();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    private static string ShellPackageSid()
    {
        Marshal.ThrowExceptionForHR(DeriveAppContainerSidFromAppContainerName("Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy", out var sid));
        try
        {
            if (!ConvertSidToStringSid(sid, out var text)) throw new IOException("Package identity unavailable.");
            try { return Marshal.PtrToStringUni(text) ?? throw new IOException("Package identity unavailable."); }
            finally { LocalFree(text); }
        }
        finally { FreeSid(sid); }
    }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr text);
    [DllImport("advapi32.dll")]
    private static extern IntPtr FreeSid(IntPtr sid);
}
