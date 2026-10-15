using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using CodexPool.Bridge;
using CodexPool.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Busy turns pass through; no compaction advice is requested", Busy),
    ("Real events pass unchanged; ACK and item completion alone cannot switch", () => Success(false)),
    ("Events before ACK still wait for a matching success response", () => Success(true)),
    ("RPC rejection releases input without reporting successful compaction", Rejected),
    ("Autonomous goals never acquire a reservation or issue compaction", Goal),
    ("Failed turn cannot switch even after contextCompaction item completion", FailedTurn),
    ("Unknown timeout releases reservation but never forwards queued input", Timeout),
    ("Unexpected EOF cannot report completion or replay deferred input", Eof),
    ("Daemon outage preserves original protocol and Unicode", Outage),
    ("Late internal replies never escape into the client RPC stream", LateReply),
    ("A truncated client frame is rejected before forwarding", Truncated),
    ("An error before turn/started cannot release queued work without a terminal outcome", EarlyError)
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + " (" + ex.GetType().Name + ")"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed; synthetic RPC and control only");
return failed == 0 ? 0 : 1;

static void Check(bool value) { if (!value) throw new InvalidOperationException(); }
static async Task Busy()
{
    await using var f = new Fixture(); await f.InitializeAsync();
    await f.ExchangeAsync("""{"id":3,"method":"turn/start","params":{"threadId":"thread","input":[]}}""",
        """{"id":3,"result":{"turn":{"id":"work","status":"inProgress"}}}""");
    var before = f.Control.AdviceCalls; f.Control.Enabled = true;
    await Task.Delay(60); Check(f.Control.AdviceCalls == before && f.Control.Reserved == 0);
}
static async Task Success(bool lateAck)
{
    await using var f = new Fixture(); await f.InitializeAsync();
    var id = await f.BeginAsync();
    const string input = """{"id":9,"method":"turn/start","params":{"threadId":"thread","input":[{"type":"text","text":"中文 private-canary"}]}}""";
    await f.Client.WriteLineAsync(input);
    if (!lateAck) await f.Server.WriteLineAsync(JsonSerializer.Serialize(new { id, result = new { } }));
    await f.EventAsync("""{"method":"turn/started","params":{"threadId":"thread","turn":{"id":"compact","status":"inProgress"}}}""");
    await f.EventAsync("""{"method":"item/started","params":{"threadId":"thread","turnId":"compact","item":{"id":"item","type":"contextCompaction"}}}""");
    await f.EventAsync("""{"method":"error","params":{"threadId":"thread","turnId":"compact","willRetry":true,"error":{"message":"private-canary"}}}""");
    await f.EventAsync("""{"method":"item/completed","params":{"threadId":"thread","turnId":"old","item":{"id":"item","type":"contextCompaction"}}}""");
    Check(f.Control.Finished.Count == 0);
    await f.EventAsync("""{"method":"item/completed","params":{"threadId":"thread","turnId":"compact","item":{"id":"item","type":"contextCompaction"}}}""");
    Check(f.Control.Finished.Count == 0);
    await f.EventAsync("""{"method":"turn/completed","params":{"threadId":"thread","turn":{"id":"compact","status":"completed"}}}""");
    if (lateAck) { Check(f.Control.Finished.Count == 0); await f.Server.WriteLineAsync(JsonSerializer.Serialize(new { id, result = new { } })); }
    Check(await f.ReadUpstreamAsync() == input);
    Check(f.Control.Reserved == 1 && f.Control.Finished.SequenceEqual(["completed"]));
}
static async Task Rejected()
{
    await using var f = new Fixture(); await f.InitializeAsync(); var id = await f.BeginAsync();
    const string input = """{"id":9,"method":"turn/start","params":{"threadId":"thread","input":[]}}""";
    await f.Client.WriteLineAsync(input);
    await f.Server.WriteLineAsync(JsonSerializer.Serialize(new { id, error = new { code = 1, message = "private-canary" } }));
    Check(await f.ReadUpstreamAsync() == input && f.Control.Finished.SequenceEqual(["failed"]));
}
static async Task Goal()
{
    await using var f = new Fixture(); await f.InitializeAsync(); f.Control.Enabled = true;
    using var query = JsonDocument.Parse(await f.ReadUpstreamAsync());
    Check(query.RootElement.GetProperty("method").GetString() == "thread/goal/get");
    await f.Server.WriteLineAsync(JsonSerializer.Serialize(new { id = query.RootElement.GetProperty("id").GetString(), result = new { goal = new { status = "active" } } }));
    const string input = """{"id":9,"method":"turn/start","params":{"threadId":"thread","input":[]}}""";
    await f.Client.WriteLineAsync(input);
    Check(await f.ReadUpstreamAsync() == input && f.Control.Reserved == 0 && f.Control.Finished.Count == 0);
}
static async Task FailedTurn()
{
    await using var f = new Fixture(); await f.InitializeAsync(); var id = await f.BeginAsync();
    await f.Server.WriteLineAsync(JsonSerializer.Serialize(new { id, result = new { } }));
    await f.EventAsync("""{"method":"turn/started","params":{"threadId":"thread","turn":{"id":"compact","status":"inProgress"}}}""");
    await f.EventAsync("""{"method":"item/started","params":{"threadId":"thread","turnId":"compact","item":{"id":"item","type":"contextCompaction"}}}""");
    await f.EventAsync("""{"method":"item/completed","params":{"threadId":"thread","turnId":"compact","item":{"id":"item","type":"contextCompaction"}}}""");
    await f.EventAsync("""{"method":"turn/completed","params":{"threadId":"thread","turn":{"id":"compact","status":"failed"}}}""");
    await f.WaitFinishAsync(); Check(f.Control.Finished.SequenceEqual(["failed"]));
}
static async Task Timeout()
{
    await using var f = new Fixture(TimeSpan.FromMilliseconds(150)); await f.InitializeAsync(); await f.BeginAsync();
    await f.Client.WriteLineAsync("""{"id":9,"method":"turn/start","params":{"threadId":"thread","input":[]}}""");
    try { await f.Run.WaitAsync(TimeSpan.FromSeconds(3)); Check(false); }
    catch (BridgeFailure ex) { Check(ex.Code == "compaction_outcome_unknown"); }
    Check(f.Control.Finished.SequenceEqual(["unknown"]));
    using var wait = new CancellationTokenSource(50);
    try { await f.Upstream.ReadLineAsync(wait.Token); Check(false); } catch (OperationCanceledException) { }
}
static async Task Eof()
{
    await using var f = new Fixture(); await f.InitializeAsync(); await f.BeginAsync();
    await f.Client.WriteLineAsync("""{"id":9,"method":"turn/start","params":{"threadId":"thread","input":[]}}""");
    await f.CloseServerAsync(); await f.Run.WaitAsync(TimeSpan.FromSeconds(3));
    Check(f.Control.Finished.SequenceEqual(["unknown"]));
}
static async Task Outage()
{
    await using var f = new Fixture(); await f.InitializeAsync(); f.Control.Throw = true;
    const string input = """{"id":9,"method":"turn/start","params":{"threadId":"thread","input":[{"type":"text","text":"中文 private-canary"}]}}""";
    await f.ExchangeAsync(input, """{"id":9,"error":{"code":1,"message":"private-canary"}}""");
    Check(f.Control.Reserved == 0 && f.Control.Finished.Count == 0);
}
static async Task LateReply()
{
    await using var f = new Fixture(); await f.InitializeAsync(); f.Control.Enabled = true;
    using var goal = JsonDocument.Parse(await f.ReadUpstreamAsync()); var id = goal.RootElement.GetProperty("id").GetString();
    await f.EventAsync("""{"method":"thread/goal/updated","params":{"threadId":"thread","goal":{"status":"active"}}}""");
    var reply = JsonSerializer.Serialize(new { id, result = new { goal = (object?)null } });
    await f.Server.WriteLineAsync(reply);
    const string input = """{"id":9,"method":"turn/start","params":{"threadId":"thread","input":[]}}""";
    await f.Client.WriteLineAsync(input); Check(await f.ReadUpstreamAsync() == input);
    await f.Server.WriteLineAsync(reply);
    await f.EventAsync("""{"method":"item/agentMessage/delta","params":{"threadId":"thread","turnId":"work","itemId":"msg","delta":"中文 private-canary"}}""");
    Check(f.Control.Reserved == 0 && f.Control.Finished.Count == 0);
}
static async Task Truncated()
{
    await using var f = new Fixture(); await f.Client.WriteAsync("{\"private-canary\":"); await f.Client.FlushAsync();
    await f.Client.DisposeAsync();
    try { await f.Run.WaitAsync(TimeSpan.FromSeconds(3)); Check(false); }
    catch (BridgeFailure ex) { Check(ex.Code == "protocol_read_failed"); }
    Check(f.Control.Reserved == 0);
}
static async Task EarlyError()
{
    await using var f = new Fixture(TimeSpan.FromMilliseconds(200)); await f.InitializeAsync(); var id = await f.BeginAsync();
    await f.Server.WriteLineAsync(JsonSerializer.Serialize(new { id, result = new { } }));
    await f.Client.WriteLineAsync("""{"id":9,"method":"turn/start","params":{"threadId":"thread","input":[]}}""");
    await f.EventAsync("""{"method":"error","params":{"threadId":"thread","willRetry":false,"error":{"message":"private-canary"}}}""");
    Check(f.Control.Finished.Count == 0);
    try { await f.Run.WaitAsync(TimeSpan.FromSeconds(3)); Check(false); }
    catch (BridgeFailure ex) { Check(ex.Code == "compaction_outcome_unknown"); }
    Check(f.Control.Finished.SequenceEqual(["unknown"]));
}

sealed class Control : ICompactionControl
{
    public bool Enabled; public bool Throw; public int AdviceCalls; public int Reserved;
    public List<string> Finished { get; } = [];
    public Task<CompactionAdvice?> AdviceAsync(string model, CancellationToken token)
    { AdviceCalls++; if (Throw) throw new IOException(); return Task.FromResult(Enabled ? new CompactionAdvice("A", 1, model) : null); }
    public Task<CompactionHold?> ReserveAsync(CompactionAdvice advice, CancellationToken token)
    { Reserved++; Enabled = false; return Task.FromResult<CompactionHold?>(new("hold", "A", 1, advice.Model, DateTimeOffset.UtcNow.AddMinutes(3))); }
    public Task FinishAsync(string id, string outcome, CancellationToken token) { Finished.Add(outcome); return Task.CompletedTask; }
}
sealed class Fixture : IAsyncDisposable
{
    private readonly Pipe clientIn = new(); private readonly Pipe clientOut = new();
    private readonly Pipe serverIn = new(); private readonly Pipe serverOut = new();
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(8));
    private readonly StreamReader bridgeClient; private readonly StreamReader bridgeServer;
    private readonly StreamWriter bridgeUpstream; private readonly StreamWriter bridgeDownstream;
    public Control Control { get; } = new();
    public StreamWriter Client { get; }
    public StreamWriter Server { get; }
    public StreamReader Upstream { get; }
    public StreamReader Downstream { get; }
    public Task Run { get; }
    public Fixture(TimeSpan? timeout = null)
    {
        static StreamWriter Writer(Pipe pipe) => new(pipe.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true };
        static StreamReader Reader(Pipe pipe) => new(pipe.Reader.AsStream(), new UTF8Encoding(false, true));
        Client = Writer(clientIn); Server = Writer(serverOut); Upstream = Reader(serverIn); Downstream = Reader(clientOut);
        bridgeClient = Reader(clientIn); bridgeServer = Reader(serverOut); bridgeUpstream = Writer(serverIn); bridgeDownstream = Writer(clientOut);
        Run = new AppServerBridge(Control, TimeSpan.FromMilliseconds(10), timeout).RunAsync(bridgeClient, bridgeDownstream, bridgeServer, bridgeUpstream, stop.Token);
    }
    public async Task InitializeAsync()
    {
        await ExchangeAsync("""{"id":1,"method":"initialize","params":{"clientInfo":{"name":"fixture","version":"1"}}}""", """{"id":1,"result":{}}""");
        await ExchangeAsync("""{"id":2,"method":"thread/resume","params":{"threadId":"thread"}}""",
            """{"id":2,"result":{"thread":{"id":"thread","model":"model","parentThreadId":null,"status":{"type":"idle"}}}}""");
    }
    public async Task ExchangeAsync(string input, string response)
    { await Client.WriteLineAsync(input); if (await ReadUpstreamAsync() != input) throw new InvalidOperationException(); await EventAsync(response); }
    public async Task EventAsync(string line)
    { await Server.WriteLineAsync(line); if (await Downstream.ReadLineAsync(stop.Token) != line) throw new InvalidOperationException(); }
    public async Task<string> ReadUpstreamAsync() => await Upstream.ReadLineAsync(stop.Token) ?? throw new IOException();
    public async Task<string> BeginAsync()
    {
        Control.Enabled = true;
        using var goal = JsonDocument.Parse(await ReadUpstreamAsync());
        if (goal.RootElement.GetProperty("method").GetString() != "thread/goal/get") throw new InvalidOperationException();
        await Server.WriteLineAsync(JsonSerializer.Serialize(new { id = goal.RootElement.GetProperty("id").GetString(), result = new { goal = (object?)null } }));
        using var compact = JsonDocument.Parse(await ReadUpstreamAsync());
        if (compact.RootElement.GetProperty("method").GetString() != "thread/compact/start") throw new InvalidOperationException();
        return compact.RootElement.GetProperty("id").GetString()!;
    }
    public async Task WaitFinishAsync()
    { for (var i = 0; i < 100 && Control.Finished.Count == 0; i++) await Task.Delay(10, stop.Token); }
    public async Task CloseServerAsync() => await Server.DisposeAsync();
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); try { await Run.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        Client.Dispose(); Server.Dispose(); Upstream.Dispose(); Downstream.Dispose();
        bridgeClient.Dispose(); bridgeServer.Dispose(); bridgeUpstream.Dispose(); bridgeDownstream.Dispose(); stop.Dispose();
    }
}
