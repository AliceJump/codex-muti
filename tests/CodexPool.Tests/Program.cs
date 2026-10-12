
using System.Net;
using System.Text;
using CodexPool.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Definite quota rejection fails over once", () => Scenario(429, "usage_limit_reached", 2)),
    ("Definite auth rejection fails over once", () => Scenario(401, "invalid_token", 2)),
    ("Ordinary 429 never fails over", () => Scenario(429, "rate_limit_exceeded", 1)),
    ("Unknown 401 never fails over", () => Scenario(401, "unknown", 1)),
    ("5xx never fails over", () => Scenario(503, "usage_limit_reached", 1)),
    ("Network ambiguity never fails over", NetworkFailure),
    ("Output then disconnect never fails over", PartialStream),
    ("Created then failure forwards byte-for-byte", FailureEvent),
    ("Client cancellation never fails over", Cancel),
    ("Cancellation during output never fails over", CancelOutput),
    ("Empty stream never fails over", EmptyStream),
    ("Body and safe headers preserved; local credentials excluded", Headers),
    ("Candidate exhaustion has no retry cycle", Exhausted),
    ("Downstream write failure cannot reopen commit gate", SinkFailure),
    ("Oversized error body is never trusted or echoed", LargeError),
    ("Malformed JSON rejection never fails over", MalformedError),
    ("Lease formatting and serialization redact secrets", SecretRedaction),
    ("Missing Content-Type accepts typed SSE framing without losing bytes", MissingContentType),
    ("Missing Content-Type rejects JSON or untyped streams without retry", MissingContentTypeRejected),
    ("Sticky scheduling, recovery and stale-request epochs", Sticky),
    ("No eligible account and auto-switch OFF never relax admission", Eligibility),
    ("Native retries use each lease account header and balance in-flight observers", NativeRouting),
    ("Streaming quota failure excludes future routing without replay", StreamQuota),
    ("Failed durable selection never changes active owner or epoch", PersistenceFailure),
    ("Completed metadata requires a delivered event and cannot reopen replay", CompletedMetadata)
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch { failed++; Console.WriteLine($"FAIL {name}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static ModelLease[] Candidates() => [new("A", new("https://fixture.invalid/responses"), "upstream-a"), new("B", new("https://fixture.invalid/responses"), "upstream-b")];
static HttpResponseMessage Response(int status, string content, string type = "application/json") => new((HttpStatusCode)status) { Content = new StringContent(content, Encoding.UTF8, type) };
static string Error(string code) => "{\"error\":{\"code\":\"" + code + "\",\"message\":\"SECRET-CANARY\"}}";
static void Check(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
static async Task Scenario(int status, string code, int attempts)
{
    var handler = new FixtureHandler((index, _) => Task.FromResult(index == 1 ? Response(status, Error(code)) : Response(200, "data: {}\n\n", "text/event-stream")));
    using var client = new HttpClient(handler); var sink = new MemorySink();
    var result = await Relay(client, sink);
    Check(handler.Count == attempts && result.Attempts == attempts && result.AccountId == (attempts == 2 ? "B" : "A"));
    Check(!sink.Text.Contains("SECRET-CANARY"));
}
static Task<RelayResult> Relay(HttpClient client, IResponseSink sink, CancellationToken token = default) =>
    new ResponsesRelay(client).ForwardAsync("{}"u8.ToArray(), "application/json", Candidates(), new Dictionary<string, string>(), sink, token);
static async Task NetworkFailure()
{
    var handler = new FixtureHandler((_, _) => throw new HttpRequestException("secret-do-not-log"));
    using var client = new HttpClient(handler); var result = await Relay(client, new MemorySink());
    Check(handler.Count == 1 && !result.Committed && result.Outcome == "upstream_unavailable");
}
static async Task PartialStream()
{
    var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FaultStream()) }));
    using var client = new HttpClient(handler); var sink = new MemorySink();
    handler.Decorate = response => response.Content.Headers.ContentType = new("text/event-stream");
    var result = await Relay(client, sink);
    Check(result.Committed && result.Outcome == "stream_interrupted" && handler.Count == 1 && sink.Text.Contains("response.created"));
}
static async Task FailureEvent()
{
    const string events = "event: response.created\ndata: {\"type\":\"response.created\"}\n\nevent: response.failed\ndata: {\"type\":\"response.failed\"}\n\n";
    var handler = new FixtureHandler((_, _) => Task.FromResult(Response(200, events, "text/event-stream")));
    using var client = new HttpClient(handler); var sink = new MemorySink(); var result = await Relay(client, sink);
    Check(result.Committed && handler.Count == 1 && sink.Text == events);
}
static async Task NativeRouting()
{
    var leases = Candidates().Select(x => x with { Endpoint = new("https://chatgpt.com/backend-api/codex/responses"), BackendAccountId = "backend-" + x.AccountId }).ToArray();
    var observations = new List<string>();
    var handler = new FixtureHandler((index, request) =>
    {
        Check(request.Headers.GetValues("ChatGPT-Account-Id").Single() == (index == 1 ? "backend-A" : "backend-B"));
        return Task.FromResult(index == 1 ? Response(429, Error("quota_exhausted")) : Response(200, "data: {}\n\n", "text/event-stream"));
    });
    using var client = new HttpClient(handler);
    await new ResponsesRelay(client).ForwardAsync("{}"u8.ToArray(), "application/json", leases,
        new Dictionary<string, string> { ["ChatGPT-Account-Id"] = "untrusted" }, new MemorySink(), default,
        (lease, reason) => observations.Add(lease.AccountId + ":" + reason), (lease, start) => observations.Add(lease.AccountId + ":" + start));
    Check(observations.SequenceEqual(new[] { "A:True", "A:quota_rejected", "A:False", "B:True", "B:False" }));
}
static async Task StreamQuota()
{
    const string events = "data: {\"type\":\"response.created\"}\n\ndata: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"usage_limit_reached\"}}}\n\n";
    var handler = new FixtureHandler((_, _) => Task.FromResult(Response(200, events, "text/event-stream")));
    using var client = new HttpClient(handler); var sink = new MemorySink(); var rejected = new List<string>();
    var result = await new ResponsesRelay(client).ForwardAsync("{}"u8.ToArray(), "application/json", Candidates(), new Dictionary<string, string>(), sink, default,
        (lease, reason) => rejected.Add(lease.AccountId + ":" + reason));
    Check(result.Committed && handler.Count == 1 && sink.Text == events && rejected.SequenceEqual(new[] { "A:quota_rejected" }));
}
static async Task CompletedMetadata()
{
    const string events = "data: {\"type\":\"response.created\"}\n\ndata: {\"type\":\"response.completed\"}\n\n";
    foreach (var fails in new[] { false, true })
    {
        var handler = new FixtureHandler((_, _) => Task.FromResult(Response(200, events, "text/event-stream")));
        using var client = new HttpClient(handler); var completed = false;
        var sink = new MemorySink { OnWrite = () => { if (fails) throw new IOException("write-failed"); } };
        var result = await new ResponsesRelay(client).ForwardAsync("{}"u8.ToArray(), "application/json", Candidates(), new Dictionary<string, string>(), sink, default,
            completed: _ => completed = true);
        Check(completed == !fails && result.Committed && handler.Count == 1);
    }
}
static async Task Cancel()
{
    var handler = new FixtureHandler((_, _) => Task.FromResult(Response(200, "", "text/event-stream")));
    using var client = new HttpClient(handler); using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await Relay(client, new MemorySink(), cts.Token); throw new InvalidOperationException(); }
    catch (OperationCanceledException) { Check(handler.Count == 0); }
}
static async Task CancelOutput()
{
    var handler = new FixtureHandler((_, _) => Task.FromResult(Response(200, "data: {}\n\n", "text/event-stream")));
    using var client = new HttpClient(handler); using var cts = new CancellationTokenSource();
    var sink = new MemorySink { OnWrite = () => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); } };
    try { await Relay(client, sink, cts.Token); throw new InvalidOperationException(); }
    catch (OperationCanceledException) { Check(handler.Count == 1); }
}
static async Task EmptyStream()
{
    var handler = new FixtureHandler((_, _) => Task.FromResult(Response(200, "", "text/event-stream")));
    using var client = new HttpClient(handler); var result = await Relay(client, new MemorySink());
    Check(handler.Count == 1 && !result.Committed && result.Outcome == "empty_stream");
}
static async Task Headers()
{
    const string body = "{\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"c1\",\"output\":\"kept\"}],\"opaque_field\":42}";
    var headers = new Dictionary<string, string> { ["Authorization"] = "local-only", ["Cookie"] = "local-cookie", ["session_id"] = "session", ["x-codex-turn-state"] = "A-bound", ["x-openai-internal-codex-responses-lite"] = "true",
        ["x-codex-beta-features"] = "compact", ["x-codex-turn-metadata"] = "{\"turn_id\":\"turn\"}", ["x-codex-window-id"] = "window" };
    var handler = new FixtureHandler(async (index, request) =>
    {
        Check(await request.Content!.ReadAsStringAsync() == body);
        Check(request.Headers.Authorization?.Parameter == (index == 1 ? "upstream-a" : "upstream-b"));
        Check(!request.Headers.Contains("Cookie") && request.Headers.GetValues("session_id").Single() == "session");
        Check(request.Headers.GetValues("x-openai-internal-codex-responses-lite").Single() == "true");
        foreach (var name in new[] { "x-codex-beta-features", "x-codex-turn-metadata", "x-codex-window-id" })
            Check(request.Headers.GetValues(name).Single() == headers[name]);
        Check(request.Headers.Accept.Any(x => x.MediaType == "text/event-stream"));
        Check(request.Headers.Contains("x-codex-turn-state") == (index == 1));
        return index == 1 ? Response(429, Error("usage_limit_reached")) : Response(200, "data: {}\n\n", "text/event-stream");
    });
    using var client = new HttpClient(handler);
    await new ResponsesRelay(client).ForwardAsync(Encoding.UTF8.GetBytes(body), "application/json", Candidates(), headers, new MemorySink(), default);
    Check(handler.Count == 2);
}
static async Task Exhausted()
{
    var handler = new FixtureHandler((_, _) => Task.FromResult(Response(429, Error("usage_limit_reached"))));
    using var client = new HttpClient(handler); var result = await Relay(client, new MemorySink());
    Check(handler.Count == 2 && result.StatusCode == 429 && result.AccountId == "B");
}
static async Task SinkFailure()
{
    var handler = new FixtureHandler((_, _) => Task.FromResult(Response(200, "data: {}\n\n", "text/event-stream")));
    using var client = new HttpClient(handler); var sink = new MemorySink { OnWrite = () => throw new IOException("partial-write") };
    var result = await Relay(client, sink); Check(handler.Count == 1 && result.Committed && result.Outcome == "stream_interrupted");
}
static async Task LargeError()
{
    var handler = new FixtureHandler((_, _) => Task.FromResult(Response(429, Error("usage_limit_reached") + new string(' ', 9000))));
    using var client = new HttpClient(handler); var sink = new MemorySink(); var result = await Relay(client, sink);
    Check(handler.Count == 1 && result.Outcome == "unknown_rejection" && !sink.Text.Contains("SECRET-CANARY"));
}
static async Task MalformedError()
{
    foreach (var text in new[] { "null", "[]", "\"value\"", "invalid-json" })
    {
        var handler = new FixtureHandler((_, _) => Task.FromResult(Response(429, text)));
        using var client = new HttpClient(handler); var result = await Relay(client, new MemorySink());
        Check(handler.Count == 1 && result.Outcome == "unknown_rejection");
    }
}
static Task SecretRedaction()
{
    var lease = Candidates()[0];
    Check(!lease.ToString().Contains("upstream-a"));
    Check(!System.Text.Json.JsonSerializer.Serialize(lease).Contains("upstream-a"));
    return Task.CompletedTask;
}
static async Task MissingContentType()
{
    var events = ": keepalive\r\n\r\nevent: response.created\r\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"r1\",\"large_field\":\"" + new string('x', 20000) + "\"}}\r\n\r\ndata: {\"type\":\"response.completed\"}\n\n";
    var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentsStream(Encoding.UTF8.GetBytes(events))) }));
    using var client = new HttpClient(handler); var sink = new MemorySink(); var result = await Relay(client, sink);
    Check(result.Committed && result.PayloadKind == "typed_sse" && result.UpstreamContentType == "missing" && sink.Text == events && handler.Count == 1);
}
static async Task MissingContentTypeRejected()
{
    foreach (var content in new[] { "{\"id\":\"response-json\"}", "data: {}\n\n", new string('x', 9000),
        "data: {\"type\":\"SECRET-CANARY\",\"SECRET-CANARY\":true,\"error\":{\"code\":\"SECRET-CANARY\"}}\n\n" })
    {
        var handler = new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)) }));
        using var client = new HttpClient(handler); var sink = new MemorySink(); var result = await Relay(client, sink);
        Check(!result.Committed && handler.Count == 1 && sink.Text.Length == 0 && result.Outcome == "unexpected_content_type");
        Check(!System.Text.Json.JsonSerializer.Serialize(result).Contains("SECRET-CANARY"));
    }
}
static Task Sticky()
{
    var now = DateTimeOffset.UtcNow; var settings = new PoolSettings(true); var scheduler = new StickyScheduler("A");
    PoolAccount[] healthy = [new("A"), new("B"), new("C")];
    for (var i = 0; i < 10; i++) Check(scheduler.Select(healthy, settings, now).AccountId == "A");
    PoolAccount[] exhausted = [new("A", FiveHourUsed: 95, QuotaUpdatedAt: now), new("B"), new("C")];
    Check(scheduler.Select(exhausted, settings, now).AccountId == "B");
    Check(scheduler.Select(healthy, settings, now).AccountId == "B"); // recovered A cannot steal B
    var lease = scheduler.Select(healthy, settings, now);
    scheduler.Activate("C", healthy, settings, now);
    scheduler.CompleteFailover(lease, "A", "quota_rejected", healthy, settings, now);
    Check(scheduler.Active == "C");
    scheduler.Activate("B", healthy, settings, now);
    PoolAccount[] bExhausted = [new("A"), new("B", WeeklyUsed: 95, QuotaUpdatedAt: now), new("C")];
    Check(scheduler.Select(bExhausted, settings, now).AccountId == "A");
    var stale = new[] { new PoolAccount("A", FiveHourUsed: 100, QuotaUpdatedAt: now.AddMinutes(-5)), new PoolAccount("B") };
    Check(scheduler.Select(stale, settings, now).AccountId == "A");
    return Task.CompletedTask;
}
static Task Eligibility()
{
    var now = DateTimeOffset.UtcNow; var scheduler = new StickyScheduler("A");
    PoolAccount[] accounts = [new("A", Paused: true), new("B")];
    try { scheduler.Select(accounts, new(false), now); throw new InvalidOperationException(); } catch (NoEligibleAccountException) { }
    Check(scheduler.Select(accounts, new(true), now).AccountId == "B");
    accounts = [new("A", Enabled: false), new("B", Draining: true)];
    try { scheduler.Select(accounts, new(true), now); throw new InvalidOperationException(); } catch (NoEligibleAccountException) { }
    Check(scheduler.History.Length == 1);
    return Task.CompletedTask;
}
static Task PersistenceFailure()
{
    var scheduler = new StickyScheduler("A", _ => throw new IOException("disk-unavailable"));
    PoolAccount[] accounts = [new("A"), new("B")]; var now = DateTimeOffset.UtcNow;
    var before = scheduler.Select(accounts, new(true), now);
    try { scheduler.Activate("B", accounts, new(true), now); throw new InvalidOperationException(); } catch (IOException) { }
    var after = scheduler.Select(accounts, new(true), now);
    Check(after.AccountId == "A" && after.Epoch == before.Epoch && scheduler.History.Length == 0);
    return Task.CompletedTask;
}

sealed class FixtureHandler(Func<int, HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Count { get; private set; }
    public Action<HttpResponseMessage>? Decorate { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { var response = await respond(++Count, request); Decorate?.Invoke(response); return response; }
}
sealed class MemorySink : IResponseSink
{
    private readonly MemoryStream bytes = new();
    public string Text => Encoding.UTF8.GetString(bytes.ToArray());
    public Action? OnWrite { get; init; }
    public void Start(int statusCode, string contentType) { }
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) { OnWrite?.Invoke(); return bytes.WriteAsync(data, cancellationToken); }
}
sealed class FaultStream : Stream
{
    private bool delivered;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (delivered) throw new IOException("upstream-disconnect");
        delivered = true; var eventBytes = "event: response.created\ndata: {\"type\":\"response.created\"}\n\n"u8; eventBytes.CopyTo(buffer.Span); return ValueTask.FromResult(eventBytes.Length);
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
sealed class FragmentsStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
}
