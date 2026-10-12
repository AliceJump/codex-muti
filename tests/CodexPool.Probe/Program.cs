
using System.Collections.Concurrent;
using System.Text.Json;
using CodexPool.Core;

// Test-only executable. It cannot read credentials or contact external endpoints.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:33210");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024 * 1024);
var app = builder.Build();
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
var relay = new ResponsesRelay(http);
var state = new ProbeState();
app.Use(async (context, next) =>
{
    if (context.Request.Host.Host != "127.0.0.1" || context.Request.Headers.ContainsKey("Origin"))
    { context.Response.StatusCode = 403; return; }
    if (context.Request.Path != "/health" && context.Request.Headers.Authorization != "Bearer phase0-fixture-key")
    { context.Response.StatusCode = 401; return; }
    await next();
});
app.MapGet("/health", () => new { status = "fixture-only", production = false });
app.MapGet("/probe/status", () => new { scenario = state.Scenario, requests = state.Requests.ToArray(), relays = state.Relays.ToArray() });
app.MapPost("/probe/reset/{scenario}", (string scenario) =>
{
    if (scenario is not ("plain" or "shell" or "mcp" or "quota" or "partial" or "resume")) return Results.BadRequest();
    state.Scenario = scenario; state.Requests.Clear(); state.Relays.Clear(); state.Sequence = 0;
    return Results.Ok();
});
app.MapPost("/v1/responses", async context =>
{
    using var buffer = new MemoryStream(); await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
    var leases = new[]
    {
        new ModelLease("A", new("http://127.0.0.1:33210/fixture/A/responses"), "phase0-fixture-key"),
        new ModelLease("B", new("http://127.0.0.1:33210/fixture/B/responses"), "phase0-fixture-key")
    };
    var headers = context.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    try
    {
        var result = await relay.ForwardAsync(buffer.ToArray(), "application/json", leases, headers, new AspNetSink(context), context.RequestAborted);
        state.Relays.Enqueue(result);
        if (result.Outcome == "stream_interrupted" && result.Committed) context.Abort();
        else if (!result.Committed) { context.Response.StatusCode = 502; await context.Response.WriteAsJsonAsync(new { error = new { code = result.Outcome } }); }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
});
app.MapPost("/fixture/{account}/responses", async (HttpContext context, string account) =>
{
    using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
    var root = request.RootElement;
    var index = Interlocked.Increment(ref state.Sequence);
    var inputs = root.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array ? input.EnumerateArray().ToArray() : [];
    var results = inputs.Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "function_call_output").ToArray();
    state.Requests.Enqueue(new
    {
        index, account, route = context.Request.Path.Value,
        fields = root.EnumerateObject().Select(x => x.Name).ToArray(), inputCount = inputs.Length,
        inputTypes = inputs.Select(x => x.TryGetProperty("type", out var type) ? type.GetString() : "message").ToArray(),
        toolResultCount = results.Length, resultCallIds = results.Select(x => x.GetProperty("call_id").GetString()).ToArray(),
        toolNames = (root.TryGetProperty("tools", out var tools) ? FindToolNames(tools) : [])
            .Concat(inputs.Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "additional_tools")
                .SelectMany(x => x.TryGetProperty("tools", out var addedTools) ? FindToolNames(addedTools) : [])).Distinct().ToArray(),
        previousResponseId = root.TryGetProperty("previous_response_id", out _),
        encryptedItems = inputs.Count(x => x.TryGetProperty("encrypted_content", out _))
    });
    if (state.Scenario == "quota" && account == "A")
    { context.Response.StatusCode = 429; await context.Response.WriteAsJsonAsync(new { error = new { type = "usage_limit_reached" } }); return; }
    context.Response.ContentType = "text/event-stream";
    await Send(context, new { type = "response.created", response = new { id = $"resp_{index}" } });
    if (state.Scenario == "partial") { context.Abort(); return; }
    if (state.Scenario is "shell" or "mcp" && results.Length == 0)
    {
        // Official Codex executes this fixture tool. The proxy never executes it.
        await Send(context, new { type = "response.output_item.done", item = new
        {
            type = "function_call", call_id = state.Scenario == "shell" ? "phase0_shell_once" : "phase0_mcp_once",
            name = state.Scenario == "shell" ? "exec_command" : "count", @namespace = state.Scenario == "mcp" ? "mcp__phase0" : "functions",
            arguments = state.Scenario == "shell" ? JsonSerializer.Serialize(new { cmd = "Get-Content -LiteralPath fixture-input.txt", shell = "powershell", login = false, max_output_tokens = 100 }) : "{}"
        } });
    }
    else await Send(context, new { type = "response.output_item.done", item = new
    { type = "message", role = "assistant", id = $"msg_{index}", content = new[] { new { type = "output_text", text = "phase0-ok" } } } });
    await Send(context, new { type = "response.completed", response = new { id = $"resp_{index}", usage = new
    { input_tokens = 1, output_tokens = 1, total_tokens = 2, input_tokens_details = new { cached_tokens = 0 } } } });
});
app.MapFallback(() => Results.NotFound(new { error = new { code = "unexpected_route" } }));
await app.RunAsync();

static IEnumerable<string> FindToolNames(JsonElement value)
{
    if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) foreach (var name in FindToolNames(item)) yield return name;
    if (value.ValueKind == JsonValueKind.Object)
    {
        if (value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) yield return name.GetString()!;
        foreach (var property in value.EnumerateObject()) if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            foreach (var nested in FindToolNames(property.Value)) yield return nested;
    }
}
static async Task Send(HttpContext context, object value)
{
    var data = JsonSerializer.Serialize(value);
    using var json = JsonDocument.Parse(data);
    await context.Response.WriteAsync($"event: {json.RootElement.GetProperty("type").GetString()}\ndata: {data}\n\n", context.RequestAborted);
    await context.Response.Body.FlushAsync(context.RequestAborted);
}
sealed class ProbeState
{
    public string Scenario { get; set; } = "plain";
    public int Sequence;
    public ConcurrentQueue<object> Requests { get; } = new();
    public ConcurrentQueue<RelayResult> Relays { get; } = new();
}
sealed class AspNetSink(HttpContext context) : IResponseSink
{
    public void Start(int statusCode, string contentType) { context.Response.StatusCode = statusCode; context.Response.ContentType = contentType; context.Response.Headers.CacheControl = "no-store"; }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    { await context.Response.Body.WriteAsync(bytes, cancellationToken); await context.Response.Body.FlushAsync(cancellationToken); }
}
