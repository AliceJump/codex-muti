using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using CodexPool.Core;

namespace CodexPool.Daemon;

// A separate, opt-in registration process. Only this process talks to the
// daemon; neither the broker nor Shell receives the daemon's management key.
internal static class QuickSettingsRegistration
{
    private const string Id = "codex-pool";
    private const int MaxFrame = 32768;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync(string[] args, string directory, string? activationNonce = null,
        Func<CancellationToken,Task<ProxySnapshot>>? readOverride = null,
        Func<JsonObject,string,CancellationToken,Task<JsonObject>>? exchangeOverride = null,
        CancellationToken cancellation = default, TimeSpan? unavailableExitAfter = null)
    {
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0]))
            return 2;
        var broker = Path.GetFullPath(args[0]);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var lastBrokerContact=Stopwatch.GetTimestamp();
        var unavailableTimeout=unavailableExitAfter ?? TimeSpan.FromSeconds(60);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new("http://127.0.0.1:3210"), Timeout = TimeSpan.FromSeconds(2) };
        var proxy = new QuickSettingsProxy(args[1], directory, http);
        Task<ProxyOperation>? operation = null;
        var operationEventId = "";
        var providerEpoch = Guid.NewGuid().ToString("N");
        string? brokerEpoch = null;
        var operationNotice = "";
        var lastKnownRunning = false;
        var registered = false;
        var publishAfterRegister = false;
        ulong revision = 1;
        JsonObject? completion = null;
        JsonObject? completionState = null;
        JsonObject? previous = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (operation is { IsCompleted: true })
                    {
                        ProxyOperation result;
                        try{result=await operation;}
                        catch(Exception failure) when(Unavailable(failure)){result=new(false,"操作结果无法确认，未自动重试");}
                        operationNotice = result.Message;
                        completion = new() { ["eventId"] = operationEventId, ["outcome"] = result.Success ? "confirmed" : "rejected" };
                        operation = null;
                    }
                    var state = await (readOverride is null ? proxy.ReadAsync(stop.Token) : readOverride(stop.Token));
                    if (state.Known) lastKnownRunning = state.Running;
                    var current = Presentation(state, lastKnownRunning, operationNotice);
                    var changed = !JsonNode.DeepEquals(current, previous) || completion is not null || publishAfterRegister;
                    // A result awaiting ACK owns an immutable state/revision.
                    // Re-register that exact state until its receipt is resolved.
                    if (changed && completionState is null) ++revision;
                    previous = (JsonObject)current.DeepClone();
                    current["revision"] = revision;
                    var publishedState = ContractState(current, providerEpoch, revision);
                    if (completion is not null) completionState ??= (JsonObject)publishedState.DeepClone();
                    JsonObject request;
                    if (!registered)
                    {
                        request = Message("plugin.register");
                        var capabilities = await (exchangeOverride ?? ExchangeAsync)(Message("framework.hello"), broker, stop.Token);
                        if (capabilities["manifestVersion"]?.GetValue<int>() != 2 || capabilities["stateVersion"]?.GetValue<int>() != 2)
                            throw new IOException("Unsupported Quick Settings contract.");
                        request["id"] = Id;request["manifest"] = Manifest();request["state"] = completionState?.DeepClone() ?? publishedState;
                        if (activationNonce is not null) request["activationNonce"] = activationNonce;
                    }
                    else
                    {
                        request = Message("plugin.poll"); request["id"] = Id;request["providerEpoch"] = providerEpoch;
                        if (changed) request["state"] = publishedState;
                        if (completionState is not null) request["state"] = completionState.DeepClone();
                        if (completion is not null) request["result"] = completion.DeepClone();
                    }
                    var response = await (exchangeOverride ?? ExchangeAsync)(request, broker, stop.Token);
                    if (response["protocol"]?.GetValue<int>() != 2 || response["type"]?.GetValue<string>() == "error")
                    {
                        completion = null;completionState = null;
                        throw new IOException("Registration rejected.");
                    }
                    var responseEpoch = response["brokerEpoch"]?.GetValue<string>() ?? throw new IOException("Broker epoch unavailable.");
                    lastBrokerContact=Stopwatch.GetTimestamp();
                    if (!registered && brokerEpoch is not null && brokerEpoch != responseEpoch) { completion = null;completionState = null; }
                    brokerEpoch = responseEpoch;
                    publishAfterRegister = !registered || completionState is not null;
                    if(registered){completion = null;completionState = null;}
                    registered = true;
                    // The framework leases activity while Quick Settings is visible
                    // or an action is pending. Idle helpers exit; the next click wakes them.
                    if (operation is null && response["idleExitAfterMs"]?.GetValue<long>() == 0 && response["event"] is null)
                        return 0;
                    if (response["event"] is JsonObject action)
                    {
                        var eventId = Required<string>(action,"eventId");
                        var accepted = action["id"]?.GetValue<string>() == Id
                            && action["expectedRevision"]?.GetValue<ulong>() == revision
                            && action["providerEpoch"]?.GetValue<string>() == providerEpoch && action["brokerEpoch"]?.GetValue<string>() == brokerEpoch;
                        if (accepted && operation is null && action["kind"]?.GetValue<string>() == "setEnabled")
                        {
                            operationNotice = "操作处理中…"; operationEventId = eventId;
                            operation = proxy.SetRunningAsync(Required<bool>(action,"value"), stop.Token);
                        }
                        else if (accepted && operation is null && action["kind"]?.GetValue<string>() == "invoke"
                            && action["componentId"]?.GetValue<string>() == "open-management" && action["action"]?.GetValue<string>() == "open-management")
                        {
                            operationEventId = eventId;
                            operation = Task.FromResult(OpenManagement(state, () => Process.Start(new ProcessStartInfo("http://127.0.0.1:3210/") { UseShellExecute = true })));
                        }
                        else if (accepted && operation is null && action["kind"]?.GetValue<string>() == "invoke"
                            && action["componentId"]?.GetValue<string>() == "accounts" && action["action"]?.GetValue<string>() == "activate-account" && action["argument"]?.GetValue<string>() is { } accountId)
                        {
                            operationNotice = "正在切换账户…"; operationEventId = eventId;
                            operation = proxy.ActivateAccountAsync(accountId, stop.Token);
                        }
                        else
                            completion = new() { ["eventId"] = eventId, ["outcome"] = accepted && operation is null
                                && action["kind"]?.GetValue<string>() == "invoke" && action["componentId"]?.GetValue<string>() == "refresh"
                                && action["action"]?.GetValue<string>() == "refresh" && state.Known ? "confirmed" : "rejected" };
                    }
                }
                catch (Exception e) when (Unavailable(e))
                {
                    // An unknown transport outcome never replays an action.
                    // Re-register only a newly read authoritative state.
                    registered = false;
                    if(activationNonce is not null && operation is null && Stopwatch.GetElapsedTime(lastBrokerContact)>=unavailableTimeout)return 0;
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
    internal static T Required<T>(JsonObject value,string field) where T:notnull
        => value[field] is { } node ? JsonSerializer.Deserialize<T>(node.ToJsonString()) ?? throw new JsonException("Required status field unavailable.") : throw new JsonException("Required status field unavailable.");
    internal static IReadOnlyList<JsonObject> Accounts(JsonObject status){
        if(status["accounts"] is not JsonArray values)throw new JsonException("Account list unavailable.");
        var result=new List<JsonObject>();foreach(var value in values){if(value is not JsonObject account)throw new JsonException("Invalid account list.");_ = Required<string>(account,"id");result.Add(account);}return result;
    }

    private static JsonObject Message(string type) => new() { ["protocol"] = 2, ["type"] = type };

    internal static ProxyOperation OpenManagement(ProxySnapshot state, Action launch)
    {
        if (!state.Known || !state.Running) return new(false, "代理未运行，无法打开网页管理");
        try { launch(); return new(true, ""); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { return new(false, "无法打开网页管理，请检查默认浏览器"); }
    }

    internal static JsonObject Manifest()
    {
        using var stream = typeof(QuickSettingsRegistration).Assembly.GetManifestResourceStream("CodexPool.Daemon.QuickSettings.manifest.json")
            ?? throw new IOException("Quick Settings manifest unavailable.");
        return JsonNode.Parse(stream)!.AsObject();
    }

    internal static JsonObject ContractState(JsonObject projection,string providerEpoch,ulong revision)
    {
        var components = new JsonArray();
        foreach (var line in projection["lines"]!.AsArray().Select(x => x!.GetValue<string>()).Select((text,index) => (text,index)))
            components.Add(new JsonObject { ["id"] = "notice-" + line.index, ["type"] = "text", ["subtitle"] = line.text });
        components.Add(new JsonObject { ["id"] = "accounts", ["type"] = "list",["title"] = "账户",["appearance"] = "settings-list", ["icon"] = "person", ["action"] = "activate-account",
            ["items"] = new JsonArray(projection["rows"]!.AsArray().OfType<JsonObject>().Select(row => (JsonNode)new JsonObject {
                ["id"] = row["id"]!.DeepClone(),["title"] = row["title"]!.DeepClone(),["subtitle"] = row["subtitle"]!.DeepClone(),
                ["selected"] = row["selected"]!.DeepClone(),["enabled"] = row["enabled"]!.DeepClone()
            }).ToArray()) });
        if(!projection["enabled"]!.GetValue<bool>() && projection["canToggle"]!.GetValue<bool>()){
            components.Clear();components.Add(new JsonObject { ["id"] = "service-stopped",["type"] = "text",["appearance"] = "empty-state",["title"] = "代理已关闭",["subtitle"] = "使用上面的切换开关启动代理，查看和管理账户。" });
        }
        var footer = new JsonArray(new JsonObject { ["id"] = "refresh",["type"] = "button",["appearance"] = "settings-link",["title"] = "刷新状态",["action"] = "refresh" });
        var icon = projection["quotaIcon"]!.AsObject();
        var tone = icon["accentArgb"]!.GetValue<uint>() switch {0xFF75DBAF => "success",0xFFFFC65F => "warning",0xFFFF7069 => "critical",_ => "neutral"};
        return new JsonObject {
            ["stateVersion"] = 2,["providerEpoch"] = providerEpoch,["revision"] = revision,["observedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["tile"] = new JsonObject { ["label"] = projection["tileLabel"]!.DeepClone(),["detailLabel"] = "管理账户",["summary"] = projection["summary"]!.DeepClone(),
                ["valueKnown"] = projection["canToggle"]!.DeepClone(),["value"] = projection["enabled"]!.DeepClone(),["canToggle"] = projection["canToggle"]!.DeepClone(),
                ["unavailableReason"] = projection["canToggle"]!.GetValue<bool>() ? "" : "代理状态暂不可确认",
                ["indicator"] = new JsonObject { ["kind"] = "ring",["known"] = icon["known"]!.DeepClone(),["value"] = icon["remaining"]!.DeepClone(),["exhausted"] = icon["exhausted"]!.DeepClone(),["tone"] = tone } },
            ["components"] = components,["footer"] = footer,
            ["contextMenu"] = new JsonArray(new JsonObject { ["id"] = "open-management",["type"] = "button",["title"] = "打开网页管理",["action"] = "open-management",
                ["enabled"] = projection["enabled"]!.GetValue<bool>() && projection["canToggle"]!.GetValue<bool>() })
        };
    }

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
        var tileLabel = !state.Known ? "代理状态暂不可确认" : !state.Running ? "代理未启动" : "未选择账户";
        var tileIcon = new QuotaIconState(null, false);
        if (state.Status is { } status)
        {
            var accounts = Accounts(status);
            var usage = status["usage"] as JsonArray;
            var availability = status["accountAvailability"] as JsonArray;
            if(accounts.Count == 0) lines.Add("暂无账户，请先在 Codex Pool 中登录");
            var activeId=status["activeAccountId"]?.GetValue<string>();
            var active=accounts.FirstOrDefault(x=>Required<string>(x,"id")==activeId);
            if(active is not null){tileLabel=active["name"]?.GetValue<string>() ?? "未命名账户";var activeQuota=usage?.OfType<JsonObject>().FirstOrDefault(x=>x["accountId"]?.GetValue<string>()==activeId);var window=QuotaDisplay.TrayWindow(JsonSerializer.SerializeToElement(activeQuota),JsonSerializer.SerializeToElement(status["settings"]));tileIcon=new(window?.Remaining,window?.Remaining is <=0);}
            var visible=accounts.Take(32).ToList();if(active is not null && !visible.Contains(active)){visible=accounts.Take(31).ToList();visible.Add(active);}
            foreach (var account in visible)
            {
                var id = Required<string>(account,"id");
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
            ["tileLabel"] = tileLabel.Length > 128 ? tileLabel[..128] : tileLabel,
            ["quotaIcon"] = new JsonObject { ["known"] = tileIcon.Known, ["remaining"] = tileIcon.Amount,
                ["exhausted"] = tileIcon.Exhausted, ["label"] = tileIcon.Label, ["accentArgb"] = tileIcon.AccentArgb },
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
        if (quota?["windows"] is not JsonArray windows) return "额度：未知";
        var reported = windows.OfType<JsonObject>()
            .Where(value => (value["limitId"]?.GetValue<string>() ?? "codex") == "codex").ToArray();
        if (reported.Length == 0) return "额度：未知";
        string Window(JsonObject value)
        {
            int? seconds = value["windowSeconds"] is JsonValue duration && duration.TryGetValue<int>(out var length)
                ? length : null;
            var label = seconds == 604800 ? "每周" : QuotaLabels.Duration(seconds);
            if (value["remainingPercent"] is not JsonValue remaining || !remaining.TryGetValue<double>(out var number)
                || !double.IsFinite(number) || number is < 0 or > 100) return label + "：未知";
            var current = quota["cacheState"]?.GetValue<string>() == "current"
                && DateTimeOffset.TryParse(value["resetAt"]?.GetValue<string>(), out var reset) && reset > DateTimeOffset.UtcNow;
            return $"{label}：{number:0.#}%" + (current ? "" : "（缓存）");
        }
        return string.Join("    ", reported.Select(Window));
    }

    internal static async Task<JsonObject> ExchangeAsync(JsonObject request, string broker, CancellationToken token)
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
