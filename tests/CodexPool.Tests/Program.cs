
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
    ("Completed metadata requires a delivered event and cannot reopen replay", CompletedMetadata),
    ("Wire errors preserve known semantic codes without leaking upstream data", WireErrors),
    ("Retry advice is validated and delivered without authorizing failover", RetryAdvice),
    ("Token counters validate totals and preserve unknown detail subsets", TokenParser),
    ("Fragmented terminal usage counts once on the serving lease and preserves SSE bytes", TerminalUsage),
    ("Reported consumption survives downstream failure without reopening replay", UsageWriteFailure),
    ("Missing, malformed, truncated and oversized usage remain unknown", UnknownUsage),
    ("Large terminal events and multiline framing retain usage within the observer limit", LargeUsage)
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
static async Task WireErrors()
{
    foreach (var (status, code, expectedType, expectedStatus) in new[]
    {
        (429,"usage_limit_reached","usage_limit_reached",429),
        (429,"quota_exhausted","usage_limit_reached",429),
        (429,"credit_balance_exhausted","insufficient_quota",429),
        (429,"usage_not_included","usage_not_included",429),
        (429,"flex_unavailable","flex_unavailable",429),
        (401,"invalid_token","invalid_request_error",400),
        (503,"server_is_overloaded","server_is_overloaded",503),
        (503,"slow_down","slow_down",503),
        (403,"misalignment_policy_violation","invalid_request_error",403),
        (400,"invalid_prompt","invalid_request_error",400),
        (400,"cyber_policy","invalid_request_error",400)
    })
    {
        var handler = new FixtureHandler((_, _) => Task.FromResult(Response(status, Error(code))));
        using var client = new HttpClient(handler); var sink = new MemorySink();
        var result = await new ResponsesRelay(client).ForwardAsync("{}"u8.ToArray(), "application/json", Candidates()[..1], new Dictionary<string,string>(), sink, default);
        using var json = System.Text.Json.JsonDocument.Parse(sink.Text);
        Check(json.RootElement.GetProperty("error").GetProperty("type").GetString() == expectedType && result.StatusCode == expectedStatus);
        Check(!sink.Text.Contains("SECRET-CANARY") && handler.Count == 1);
    }
    var malformedMetadata = System.Text.Encoding.UTF8.GetBytes("{\"error\":{\"type\":\"usage_limit_reached\",\"plan_type\":\"SECRET-CANARY\",\"resets_at\":999999999999,\"limit_window_minutes\":999999,\"message\":\"SECRET-CANARY\",\"debug\":\"SECRET-CANARY\"}}");
    var safe = ModelErrors.FromUpstream(HttpStatusCode.TooManyRequests, malformedMetadata, "SECRET-CANARY");
    Check(safe.Type == "usage_limit_reached" && safe.Plan is null && safe.ResetsAt is null && safe.WindowMinutes is null && safe.RetryAfter is null);
    Check(!Encoding.UTF8.GetString(safe.Body()).Contains("SECRET-CANARY"));
    var reset = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
    var valid = ModelErrors.FromUpstream(HttpStatusCode.TooManyRequests, Encoding.UTF8.GetBytes($"{{\"error\":{{\"type\":\"usage_limit_reached\",\"plan_type\":\"plus\",\"resets_at\":{reset},\"limit_window_minutes\":300}}}}"), "1");
    Check(valid.Plan == "plus" && valid.ResetsAt == reset && valid.WindowMinutes == 300 && valid.RetryAfter is null);
    foreach (var code in new[] { "credit_balance_exhausted", "organization_spend_limit_exceeded", "project_spend_limit_exceeded", "organization_usage_limit_exceeded", "usage_not_included" })
        await Scenario(429, code, 2);
}
static async Task RetryAdvice()
{
    foreach (var header in new[] { "2", DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("r", System.Globalization.CultureInfo.InvariantCulture) })
    {
        var handler = new FixtureHandler((_, _) => Task.FromResult(Response(429, Error("rate_limit_exceeded"))))
            { Decorate = response => response.Headers.TryAddWithoutValidation("Retry-After", header) };
        using var http = new HttpClient(handler); var sink = new MemorySink();
        var result = await Relay(http, sink);
        Check(handler.Count == 1 && result.StatusCode == 429 && sink.Headers["Retry-After"] == header);
    }
    foreach (var header in new[] { "1.5", "-1", "86400", "SECRET-CANARY", "2\r\nAuthorization: SECRET-CANARY" })
        Check(ModelErrors.ValidateRetryAfter(header) is null);
    Check(ModelErrors.FromUpstream(HttpStatusCode.Unauthorized, Encoding.UTF8.GetBytes(Error("unknown")), "1").Status == 401);
    Check(ModelErrors.FromUpstream(HttpStatusCode.ServiceUnavailable, Encoding.UTF8.GetBytes(Error("usage_limit_reached")), "1").FailoverReason is null);
}
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
    using var client = new HttpClient(handler); var sink = new MemorySink(); var rejected = new List<string>(); ModelError? observed = null;
    var result = await new ResponsesRelay(client).ForwardAsync("{}"u8.ToArray(), "application/json", Candidates(), new Dictionary<string, string>(), sink, default,
        (lease, reason) => rejected.Add(lease.AccountId + ":" + reason), classified: (_, error) => observed = error);
    Check(result.Committed && handler.Count == 1 && sink.Text == events && rejected.SequenceEqual(new[] { "A:quota_rejected" }) && observed?.Type == "usage_limit_reached");
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
static string UsageEvent(string type = "response.completed", string? usage = null, string extra = "") =>
    "data: {\"type\":\"" + type + "\",\"response\":{\"id\":\"response-fixture\",\"usage\":"
    + (usage ?? "{\"input_tokens\":120,\"output_tokens\":30,\"total_tokens\":150,\"input_tokens_details\":{\"cached_tokens\":80},\"output_tokens_details\":{\"reasoning_tokens\":20}}") + extra + "}}\n\n";
static Task TokenParser()
{
    ResponseTokenUsage? Parse(string value)
    { using var json = System.Text.Json.JsonDocument.Parse("{\"usage\":" + value + "}"); return ResponseTokenUsage.Parse(json.RootElement); }
    Check(Parse("{\"input_tokens\":120,\"output_tokens\":30,\"total_tokens\":150}") == new ResponseTokenUsage(120, 30, 150, null, null));
    Check(Parse("{\"input_tokens\":0,\"output_tokens\":0,\"total_tokens\":0,\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens_details\":{\"reasoning_tokens\":0}}") == new ResponseTokenUsage(0, 0, 0, 0, 0));
    foreach (var value in new[] { "null", "[]", "{}", "{\"input_tokens\":-1,\"output_tokens\":30,\"total_tokens\":29}",
        "{\"input_tokens\":1.5,\"output_tokens\":30,\"total_tokens\":31}", "{\"input_tokens\":120,\"output_tokens\":30,\"total_tokens\":151}",
        "{\"input_tokens\":9223372036854775807,\"output_tokens\":1,\"total_tokens\":0}" }) Check(Parse(value) is null);
    Check(Parse("{\"input_tokens\":120,\"output_tokens\":30,\"total_tokens\":150,\"input_tokens_details\":{\"cached_tokens\":121},\"output_tokens_details\":{\"reasoning_tokens\":-1}}") == new ResponseTokenUsage(120, 30, 150, null, null));
    return Task.CompletedTask;
}
static async Task TerminalUsage()
{
    foreach (var type in new[] { "response.completed", "response.incomplete", "response.failed" })
    {
        var events = UsageEvent(type) + UsageEvent(type);
        var handler = new FixtureHandler((index, _) => Task.FromResult(index == 1 ? Response(429, Error("usage_limit_reached"))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentsStream(Encoding.UTF8.GetBytes(events))) }));
        handler.Decorate = response => { if (response.IsSuccessStatusCode) response.Content.Headers.ContentType = new("text/event-stream"); };
        using var client = new HttpClient(handler); var sink = new MemorySink(); var usages = new List<(string, ResponseTokenUsage)>();
        var result = await new ResponsesRelay(client).ForwardAsync("{}"u8.ToArray(), "application/json", Candidates(), new Dictionary<string, string>(), sink, default,
            usage: (lease, value) => usages.Add((lease.AccountId, value)));
        Check(result.AccountId == "B" && handler.Count == 2 && sink.Text == events && usages.Count == 1
            && usages[0] == ("B", new ResponseTokenUsage(120, 30, 150, 80, 20)));
    }
}
static async Task UsageWriteFailure()
{
    var handler = new FixtureHandler((_, _) => Task.FromResult(Response(200, UsageEvent(), "text/event-stream")));
    using var client = new HttpClient(handler); ResponseTokenUsage? tokens = null;
    var result = await new ResponsesRelay(client).ForwardAsync("{}"u8.ToArray(), "application/json", Candidates(), new Dictionary<string, string>(),
        new MemorySink { OnWrite = () => throw new IOException() }, default, usage: (_, value) => tokens = value);
    Check(tokens?.TotalTokens == 150 && result.Outcome == "stream_interrupted" && result.Committed && handler.Count == 1);
}
static async Task UnknownUsage()
{
    foreach (var events in new[] { UsageEvent(usage: "null"), UsageEvent(usage: "{}"), "data: {not-json}\n\n", UsageEvent().TrimEnd('\n'),
        UsageEvent("response.created"), UsageEvent(extra: ",\"output\":\"" + new string('x', 2 * 1024 * 1024) + "\"") })
    {
        var handler = new FixtureHandler((_, _) => Task.FromResult(Response(200, events, "text/event-stream")));
        using var client = new HttpClient(handler); var sink = new MemorySink(); var count = 0;
        await new ResponsesRelay(client).ForwardAsync("{}"u8.ToArray(), "application/json", Candidates(), new Dictionary<string, string>(), sink, default,
            usage: (_, _) => count++);
        Check(count == 0 && handler.Count == 1 && sink.Text == events);
    }
}
static async Task LargeUsage()
{
    var oversized = "data: {\"padding\":\"" + new string('x', 2 * 1024 * 1024) + "\"\ndata: ,\"type\":\"response.completed\"}\n\n";
    var events = oversized + ": heartbeat\r\nevent: response.completed\r\n"
        + UsageEvent(extra: ",\"output\":\"" + new string('x', 200000) + "\"").Replace(",\"usage\":", ",\n data: \"usage\":").Replace("\n data:", "\ndata:").Replace("\n", "\r\n");
    var handler = new FixtureHandler((_, _) => Task.FromResult(Response(200, events, "text/event-stream")));
    using var client = new HttpClient(handler); var sink = new MemorySink(); var count = 0;
    await new ResponsesRelay(client).ForwardAsync("{}"u8.ToArray(), "application/json", Candidates(), new Dictionary<string, string>(), sink, default,
        usage: (_, value) => { Check(value.TotalTokens == 150); count++; });
    Check(count == 1 && sink.Text == events && handler.Count == 1);
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
    public Dictionary<string,string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public void SetHeader(string name, string value) => Headers[name] = value;
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
