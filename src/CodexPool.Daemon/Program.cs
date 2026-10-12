
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexPool.Core;
using CodexPool.Daemon;

var directory = Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPool");
var command = args.FirstOrDefault() ?? "serve";
if (command != "serve")
{
    try { return await LocalCommands.RunAsync(command, args.Skip(1).ToArray(), directory); }
    catch { Console.Error.WriteLine("Command failed. Start the daemon first; credentials were not printed."); return 1; }
}

try
{
    // Exclusive file lock is held for the full daemon lifetime. Browser/CLI clients never refresh.
    Directory.CreateDirectory(directory);
    using var owner = new FileStream(Path.Combine(directory, "daemon.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var vault = new Vault(directory);
    var builder = WebApplication.CreateBuilder([]);
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(System.Net.IPAddress.Loopback, 3210);
        options.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    });
    var app = builder.Build();
    using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate })
        { Timeout = TimeSpan.FromSeconds(30) };
    using var modelHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate })
        { Timeout = TimeSpan.FromMinutes(10) };
    var auth = new OAuth(http, vault); var relay = new ResponsesRelay(modelHttp);
    var recent = new ConcurrentQueue<object>();
    var inFlight = new ConcurrentDictionary<string, int>();
    var boundaryProbe = args.Contains("--live-boundary-probe", StringComparer.Ordinal);
    long requestCount = 0;
    var sessionOwners = new ConcurrentDictionary<string, string>();
    app.Use(async (context, next) =>
    {
        // No CORS. Browser JS cannot operate the control API, including from localhost variants.
        if (context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != 3210 || context.Request.Headers.ContainsKey("Origin"))
        { context.Response.StatusCode = 403; return; }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var publicPath = context.Request.Path is { Value: "/health" or "/auth/callback" };
        if (!publicPath && !LocalCommands.EqualSecret(context.Request.Headers.Authorization.ToString(), "Bearer " + vault.LocalKey))
        { context.Response.StatusCode = 401; return; }
        try { await next(); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (AuthFailure ex)
        { if (!context.Response.HasStarted) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = new { code = ex.Code } }); } else context.Abort(); }
        catch (JsonException)
        { if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { code = "invalid_json" } }); } else context.Abort(); }
        catch
        { if (!context.Response.HasStarted) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = new { code = "internal_error" } }); } else context.Abort(); }
    });
    app.MapGet("/health", () => new { status = "running", stage = "phase0", automaticSwitching = false });
    app.MapGet("/status", () => new { activeAccountId = vault.ActiveId, stage = "phase0", automaticSwitching = false,
        boundaryProbe, distinctUsers = vault.DistinctUsers, liveMigrationValidated = false, quotaSource = "unverified", accounts = vault.Views(auth.Health), inFlightRequests = inFlight.ToArray(), recentRequests = recent.ToArray() });
    app.MapGet("/accounts", () => vault.Views(auth.Health));
    app.MapGet("/usage", () => new { available = false, reason = "public_quota_source_unverified", accounts = vault.PublicAccounts });
    app.MapGet("/models", async (HttpContext context) =>
    {
        var selected = vault.Selected ?? throw new AuthFailure("no_authorized_account");
        var account = await auth.ReadyAsync(selected.Id, context.RequestAborted);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models"); request.Headers.Authorization = new("Bearer", account.AccessToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        if (!response.IsSuccessStatusCode) return Results.Json(new { error = new { code = "model_catalog_unavailable" } }, statusCode: (int)response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        using var content = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await stream.ReadAsync(buffer, context.RequestAborted); if (count == 0) break; if (content.Length + count > 2 * 1024 * 1024) throw new AuthFailure("oversized_model_catalog"); content.Write(buffer, 0, count); }
        using var json = JsonDocument.Parse(content.ToArray());
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new AuthFailure("unrecognized_model_catalog");
        var visible = models.EnumerateArray().Where(x => x.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "list")
            .Select(x => new { slug = x.GetProperty("slug").GetString(), displayName = x.GetProperty("display_name").GetString() }).ToArray();
        return Results.Json(new { accountId = account.Id, models = visible });
    });
    app.MapPost("/accounts/login", async (HttpContext context) =>
    {
        var input = await context.Request.ReadFromJsonAsync<LoginInput>(context.RequestAborted) ?? throw new AuthFailure("invalid_login_request");
        var url = await auth.BeginAsync(input.Name, input.AccountId, context.RequestAborted);
        // Sensitive one-use URL goes only to the trusted initiating CLI; never a log/event.
        return Results.Json(new { authorizationUrl = url });
    });
    app.MapGet("/auth/callback", async (HttpContext context) =>
    {
        if (context.Request.Query.Any(x => x.Value.Count != 1)) return Results.BadRequest("Invalid authorization callback.");
        string? Value(string name) => context.Request.Query.TryGetValue(name, out var value) ? value.ToString() : null;
        try
        {
            await auth.CompleteAsync(Value("state") ?? "", Value("code") ?? "", Value("client_id"), Value("error"), context.RequestAborted);
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Text("ChatGPT account saved locally. You can close this tab and return to Codex Pool.");
        }
        catch (AuthFailure) { return Results.BadRequest("Authorization did not complete. Start a new sign-in from Codex Pool."); }
    });
    app.MapPost("/accounts/{id}/activate", (string id) =>
    {
        if (vault.Find(id) is null) return Results.NotFound(new { error = new { code = "account_not_found" } });
        if (vault.ActiveId != id && Interlocked.Read(ref requestCount) > 0 && !boundaryProbe)
            return Results.Conflict(new { error = new { code = "live_migration_gate_pending" } });
        if (auth.Health(id) == "reauth_required" || !vault.ActivateForProbe(id))
            return Results.Conflict(new { error = new { code = "account_not_eligible" } });
        return Results.Ok(new { activeAccountId = id, affects = "new_requests_only" });
    });
    foreach (var operation in new[] { "pause", "resume", "drain", "undrain", "enable", "disable" })
        app.MapPost("/accounts/{id}/" + operation, (string id) => vault.Control(id, operation)
            ? Results.Ok(new { accountId = id, operation, affects = "new_requests_only" })
            : Results.NotFound(new { error = new { code = "account_not_found" } }));
    app.MapPost("/v1/responses", async (HttpContext context) =>
    {
        if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true || context.Request.Headers.ContainsKey("Content-Encoding"))
        { context.Response.StatusCode = 415; return; }
        using var bytes = new MemoryStream(); await context.Request.Body.CopyToAsync(bytes, context.RequestAborted);
        var body = bytes.ToArray(); using var document = JsonDocument.Parse(body); var json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array
            || !json.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.True
            || !json.TryGetProperty("store", out var store) || store.ValueKind != JsonValueKind.False
            || !json.TryGetProperty("model", out var modelValue) || modelValue.ValueKind != JsonValueKind.String
            || !Regex.IsMatch(modelValue.GetString()!, "^[a-zA-Z0-9._:/-]{1,128}$"))
        { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { code = "invalid_responses_request" } }); return; }
        if (json.TryGetProperty("previous_response_id", out var previous) && previous.ValueKind != JsonValueKind.Null
            || json.TryGetProperty("conversation", out var conversation) && conversation.ValueKind != JsonValueKind.Null)
        { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { code = "server_continuation_unsupported" } }); return; }
        var selected = vault.Selected;
        if (selected is null) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = new { code = "no_authorized_account" } }); return; }
        if (!Vault.IsEligible(selected)) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = new { code = "active_account_not_eligible" } }); return; }
        var account = await auth.ReadyAsync(selected.Id, context.RequestAborted);
        Interlocked.Increment(ref requestCount);
        var headers = context.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        if (headers.TryGetValue("session_id", out var session))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session)));
            if (!sessionOwners.TryGetValue(hash, out var prior) || prior != account.Id) headers.Remove("x-codex-turn-state");
            // Bounded transport ownership hints, never conversation contents or Codex workflow state.
            if (sessionOwners.Count >= 1024) sessionOwners.Clear();
            sessionOwners[hash] = account.Id;
        }
        else headers.Remove("x-codex-turn-state");
        var started = Stopwatch.GetTimestamp(); var requestId = Guid.NewGuid().ToString("N");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); lifetime.CancelAfter(TimeSpan.FromMinutes(10));
        RelayResult result;
        inFlight.AddOrUpdate(account.Id, 1, (_, count) => count + 1);
        try { result = await relay.ForwardAsync(body, "application/json", [new(account.Id, new("https://api.openai.com/v1/responses"), account.AccessToken)], headers,
            new WebSink(context), lifetime.Token); }
        finally { inFlight.AddOrUpdate(account.Id, 0, (_, count) => Math.Max(0, count - 1)); }
        recent.Enqueue(new { timestamp = DateTimeOffset.UtcNow, requestId, account = result.AccountId, model = modelValue.GetString(),
            durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, status = result.StatusCode, result.Outcome, result.Attempts,
            inputItemCount = input.GetArrayLength(), encryptedItemCount = input.EnumerateArray().Count(x => x.TryGetProperty("encrypted_content", out _)),
            toolResultCount = input.EnumerateArray().Count(x => x.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"),
            upstreamHeaderNames = result.UpstreamHeaderNames, upstreamContentType = result.UpstreamContentType, payloadKind = result.PayloadKind, prefixShape = result.PrefixShape });
        while (recent.Count > 100) recent.TryDequeue(out _);
        if (result.Committed && result.Outcome == "stream_interrupted") context.Abort();
        else if (!result.Committed) { context.Response.StatusCode = 502; await context.Response.WriteAsJsonAsync(new { error = new { code = result.Outcome } }); }
    });
    app.MapFallback(() => Results.NotFound(new { error = new { code = "endpoint_not_supported_in_phase0" } }));
    Console.WriteLine("Codex Pool Phase 0 listening at http://127.0.0.1:3210 (automatic switching gated).");
    await app.RunAsync(); return 0;
}
catch { Console.Error.WriteLine("Daemon failed to start or stopped unexpectedly. Check port 3210 and single-instance ownership."); return 1; }

sealed record LoginInput(string Name, string? AccountId);
sealed class WebSink(HttpContext context) : IResponseSink
{
    public void Start(int statusCode, string contentType) { context.Response.StatusCode = statusCode; context.Response.ContentType = contentType; }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    { await context.Response.Body.WriteAsync(bytes, cancellationToken); await context.Response.Body.FlushAsync(cancellationToken); }
}
