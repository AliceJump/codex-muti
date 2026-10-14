using System.Text.Json;
using System.Threading.Channels;
using CodexPool.Core;

namespace CodexPool.Bridge;

public interface ICompactionControl
{
    Task<CompactionAdvice?> AdviceAsync(string model, CancellationToken token);
    Task<CompactionHold?> ReserveAsync(CompactionAdvice advice, CancellationToken token);
    Task FinishAsync(string id, string outcome, CancellationToken token);
}

public sealed class BridgeFailure(string code) : Exception(code)
{ public string Code { get; } = code; }

// A transport adapter, not an agent. Owns the sole client pipe; does not start turns,
// interpret model output, answer approvals or execute tools. Only protocol metadata is retained.
public sealed class AppServerBridge(ICompactionControl control, TimeSpan? pollInterval = null, TimeSpan? operationTimeout = null)
{
    private sealed class ThreadState(string id)
    {
        public string Id { get; } = id;
        public string? Model { get; set; }
        public string? TurnId { get; set; }
        public bool Idle { get; set; }
        public bool Blocked { get; set; }
        public bool HasWork { get; set; }
        public int PendingStarts { get; set; }
    }
    private sealed class Operation(ThreadState thread, CompactionAdvice advice, string rpcId, DateTimeOffset deadline)
    {
        public ThreadState Thread { get; } = thread;
        public CompactionAdvice Advice { get; } = advice;
        public CompactionHold? Hold { get; set; }
        public string RpcId { get; set; } = rpcId;
        public bool CheckingGoal { get; set; } = true;
        public DateTimeOffset Deadline { get; set; } = deadline;
        public bool Acknowledged { get; set; }
        public string? TurnId { get; set; }
        public string? ItemId { get; set; }
        public bool ItemCompleted { get; set; }
        public string? TurnStatus { get; set; }
        public bool Failed { get; set; }
    }
    private sealed record Pending(string Method, string? ThreadId);
    private sealed record Frame(int Source, string? Line);
    private readonly Dictionary<string, ThreadState> threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly Queue<string> deferred = new();
    private readonly string rpcPrefix = "codex-pool-compact-" + Guid.NewGuid().ToString("N");
    private Operation? operation;
    private int deferredChars;
    private bool initialized;
    private bool automaticDisabled;
    private long operationSequence;
    private const int MaxMetadataEntries = 256;
    public const int MaxFrameChars = 32 * 1024 * 1024;

    public async Task RunAsync(TextReader clientInput, TextWriter clientOutput, TextReader serverOutput, TextWriter serverInput, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var frames = Channel.CreateBounded<Frame>(new BoundedChannelOptions(2) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        async Task ReadAsync(TextReader reader, int source)
        {
            try
            {
                var lines = new BoundedJsonLines(reader, MaxFrameChars);
                while (await lines.ReadAsync(lifetime.Token) is { } line)
                    await frames.Writer.WriteAsync(new(source, line), lifetime.Token);
                await frames.Writer.WriteAsync(new(source, null), lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch { frames.Writer.TryComplete(new BridgeFailure("protocol_read_failed")); }
        }
        async Task TickAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(pollInterval ?? TimeSpan.FromSeconds(2));
                while (await timer.WaitForNextTickAsync(lifetime.Token)) await frames.Writer.WriteAsync(new(2, ""), lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        var pumps = new[] { ReadAsync(clientInput, 0), ReadAsync(serverOutput, 1), TickAsync() };
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(token))
            {
                if (frame.Line is null) break;
                if (operation is { } expired && DateTimeOffset.UtcNow >= expired.Deadline)
                    throw new BridgeFailure("compaction_outcome_unknown");
                if (frame.Source == 2)
                {
                    await TryBeginAsync(serverInput, token);
                    continue;
                }
                using var json = JsonDocument.Parse(frame.Line);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new BridgeFailure("invalid_protocol_frame");
                if (frame.Source == 0)
                {
                    if (operation is not null && IsWorkflowRequest(root))
                    {
                        if (deferred.Count >= 64 || deferredChars > MaxFrameChars - frame.Line.Length)
                            throw new BridgeFailure("pending_input_limit");
                        deferred.Enqueue(frame.Line); deferredChars += frame.Line.Length;
                    }
                    else await ForwardClientAsync(frame.Line, root, serverInput, token);
                }
                else
                {
                    var responseId = Text(root, "id");
                    var internalResponse = Text(root, "method") is null && responseId is not null && responseId.StartsWith(rpcPrefix, StringComparison.Ordinal);
                    if (!internalResponse) await WriteAsync(clientOutput, frame.Line, token);
                    if (internalResponse && (operation is null || responseId != operation.RpcId)) continue;
                    if (internalResponse && operation is { CheckingGoal: true }) await AcceptGoalAsync(root, serverInput, token);
                    else ObserveServer(root, internalResponse);
                    if (operation is { } current && (current.Failed || current.TurnStatus is not null))
                    {
                        if (current.Failed && current.TurnId is not null && current.TurnStatus is null) continue;
                        if (!current.Failed && !current.Acknowledged) continue;
                        var success = !current.Failed && current.Acknowledged && current.ItemCompleted && current.TurnStatus == "completed";
                        await FinishAsync(success ? "completed" : "failed", token);
                        while (deferred.TryDequeue(out var line))
                        {
                            deferredChars -= line.Length;
                            using var input = JsonDocument.Parse(line);
                            await ForwardClientAsync(line, input.RootElement, serverInput, token);
                        }
                    }
                }
            }
        }
        finally
        {
            lifetime.Cancel(); frames.Writer.TryComplete();
            if (operation is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await FinishAsync("unknown", cleanup.Token);
            }
            // Closing the owned streams is the host's responsibility. Pumps are cancelled,
            // and no deferred input is forwarded after EOF, timeout or an unknown result.
            try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
    }

    private async Task TryBeginAsync(TextWriter serverInput, CancellationToken token)
    {
        if (!initialized || automaticDisabled || operation is not null || pending.Count != 0 || threads.Count == 0
            || threads.Values.Any(x => !x.Idle || x.PendingStarts != 0 || x.Blocked)) return;
        foreach (var thread in threads.Values)
        {
            if (thread.Model is null || !thread.HasWork) continue;
            CompactionAdvice? advice;
            try
            {
                advice = await control.AdviceAsync(thread.Model, token);
                if (advice is null) continue;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return; } // Daemon unavailable: original client traffic remains usable.
            var id = rpcPrefix + "-" + ++operationSequence + "-goal";
            operation = new(thread, advice, id, DateTimeOffset.UtcNow.Add(operationTimeout ?? TimeSpan.FromMinutes(2)));
            thread.Idle = false;
            var request = JsonSerializer.Serialize(new { id, method = "thread/goal/get", @params = new { threadId = thread.Id } });
            await WriteAsync(serverInput, request, token);
            return;
        }
    }

    private async Task AcceptGoalAsync(JsonElement root, TextWriter serverInput, CancellationToken token)
    {
        var op = operation!;
        if (root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out var result)
            || !result.TryGetProperty("goal", out var goal) || goal.ValueKind != JsonValueKind.Null || op.Thread.TurnId is not null || op.Thread.Blocked)
        { op.Failed = true; op.Thread.Blocked = true; return; }
        try { op.Hold = await control.ReserveAsync(op.Advice, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { op.Failed = true; return; }
        if (op.Hold is null) { op.Failed = true; return; }
        if (op.Hold.ExpiresAt.AddSeconds(-5) < op.Deadline) op.Deadline = op.Hold.ExpiresAt.AddSeconds(-5);
        op.CheckingGoal = false;
        op.RpcId = op.RpcId[..^5] + "-compact";
        await WriteAsync(serverInput, JsonSerializer.Serialize(new { id = op.RpcId, method = "thread/compact/start", @params = new { threadId = op.Thread.Id } }), token);
    }

    private async Task ForwardClientAsync(string line, JsonElement root, TextWriter serverInput, CancellationToken token)
    {
        var method = Text(root, "method"); var threadId = NestedText(root, "params", "threadId");
        if (Text(root, "id") is { } idValue && idValue.StartsWith(rpcPrefix, StringComparison.Ordinal))
            throw new BridgeFailure("internal_id_collision");
        if (method is not null && root.TryGetProperty("id", out var id))
        {
            if (pending.Count >= MaxMetadataEntries || id.GetRawText().Length > 256 || pending.ContainsKey(id.GetRawText())) automaticDisabled = true;
            else pending[id.GetRawText()] = new(method, threadId);
        }
        if (method is "turn/start" or "turn/steer" or "thread/compact/start" or "review/start" && threadId is not null)
        { var thread = GetThread(threadId); thread.Idle = false; thread.PendingStarts++; }
        if (method is "thread/goal/set" or "thread/shellCommand" && threadId is not null) GetThread(threadId).Blocked = true;
        if (method is "turn/start" && threadId is not null && NestedText(root, "params", "model") is { } model) GetThread(threadId).Model = model;
        if (method == "turn/start" && threadId is not null) GetThread(threadId).HasWork = true;
        await WriteAsync(serverInput, line, token);
    }

    private void ObserveServer(JsonElement root, bool internalResponse)
    {
        if (internalResponse && operation is { } op)
        {
            if (root.TryGetProperty("error", out _)) op.Failed = true;
            else if (root.TryGetProperty("result", out _)) op.Acknowledged = true;
            return;
        }
        if (root.TryGetProperty("id", out var id) && Text(root, "method") is null && pending.Remove(id.GetRawText(), out var request))
        {
            if (request.Method == "initialize" && root.TryGetProperty("result", out _)) initialized = true;
            if (request.ThreadId is not null && request.Method is "turn/start" or "turn/steer" or "thread/compact/start" or "review/start")
            { var thread = GetThread(request.ThreadId); thread.PendingStarts = Math.Max(0, thread.PendingStarts - 1); }
            if (root.TryGetProperty("result", out var result))
            {
                if (result.TryGetProperty("thread", out var thread)) ObserveThread(thread, request.Method);
                if (request.ThreadId is not null && result.TryGetProperty("turn", out var turn)) ObserveTurn(request.ThreadId, turn, false);
            }
            return;
        }
        var method = Text(root, "method");
        if (!root.TryGetProperty("params", out var parameters)) return;
        if (method == "thread/started" && parameters.TryGetProperty("thread", out var started)) ObserveThread(started, method);
        var threadId = Text(parameters, "threadId");
        if (threadId is null) return;
        var state = GetThread(threadId);
        if (method is "thread/closed" or "thread/archived" or "thread/deleted") { threads.Remove(threadId); return; }
        if (method == "thread/goal/updated") state.Blocked = true;
        if (method == "thread/status/changed" && NestedText(parameters, "status", "type") == "active")
        {
            state.Idle = false;
            if (operation is { CheckingGoal: true } checking && checking.Thread.Id == threadId) checking.Failed = true;
        }
        if (method is "turn/started" or "turn/completed" && parameters.TryGetProperty("turn", out var turnValue))
            ObserveTurn(threadId, turnValue, method == "turn/completed");
        if (operation is not { } current || current.Thread.Id != threadId) return;
        if (method is "item/started" or "item/completed" && Text(parameters, "turnId") == current.TurnId
            && parameters.TryGetProperty("item", out var item) && Text(item, "type") == "contextCompaction")
        {
            if (method == "item/started") current.ItemId = Text(item, "id");
            else if (current.ItemId is not null && Text(item, "id") == current.ItemId) current.ItemCompleted = true;
        }
        if (method == "error" && Text(parameters, "turnId") == current.TurnId
            && (!parameters.TryGetProperty("willRetry", out var retry) || retry.ValueKind != JsonValueKind.True)) current.Failed = true;
    }

    private void ObserveThread(JsonElement value, string method)
    {
        var id = Text(value, "id"); if (id is null) return;
        var thread = GetThread(id); thread.Model = Text(value, "model") ?? thread.Model;
        if (Text(value, "parentThreadId") is not null) thread.Blocked = true;
        if (method is "thread/start" or "thread/resume") thread.Idle = NestedText(value, "status", "type") == "idle";
        if (method == "thread/resume") thread.HasWork = true;
    }

    private void ObserveTurn(string threadId, JsonElement turn, bool completed)
    {
        var turnId = Text(turn, "id"); if (turnId is null) return;
        var state = GetThread(threadId);
        if (!completed) { state.TurnId = turnId; state.Idle = false; }
        else if (state.TurnId == turnId) { state.Idle = true; state.TurnId = null; }
        if (operation is { } op && op.Thread.Id == threadId)
        {
            if (!completed && op.CheckingGoal) { op.Failed = true; op.Thread.Blocked = true; }
            else if (!completed && op.TurnId is null) op.TurnId = turnId;
            else if (completed && op.TurnId == turnId) op.TurnStatus = Text(turn, "status");
        }
    }

    private async Task FinishAsync(string outcome, CancellationToken token)
    {
        var current = operation; operation = null;
        if (current is null) return;
        if (outcome != "completed") automaticDisabled = true;
        if (current.TurnId is null && current.Thread.TurnId is null) current.Thread.Idle = true;
        if (current.Hold is not null)
            try { await control.FinishAsync(current.Hold.Id, outcome, token); } catch { automaticDisabled = true; }
    }

    private ThreadState GetThread(string id)
    {
        if (threads.TryGetValue(id, out var state)) return state;
        if (threads.Count >= MaxMetadataEntries) throw new BridgeFailure("thread_metadata_limit");
        return threads[id] = new(id);
    }
    private static bool IsWorkflowRequest(JsonElement root) => Text(root, "method") is { } method
        && (method.StartsWith("thread/", StringComparison.Ordinal) || method is "turn/start" or "turn/steer" or "review/start");
    private static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? NestedText(JsonElement root, string parent, string name) => root.TryGetProperty(parent, out var value) ? Text(value, name) : null;
    private static async Task WriteAsync(TextWriter writer, string line, CancellationToken token)
    { await writer.WriteLineAsync(line.AsMemory(), token); await writer.FlushAsync(token); }
}
