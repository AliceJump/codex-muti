
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
    var nativeLogin = new NativeLogin(vault, auth, directory);
    var quota = new Quota(http, auth, vault);
    var recent = new ConcurrentQueue<object>();
    var inFlight = new ConcurrentDictionary<string, int>();
    var boundaryProbe = args.Contains("--live-boundary-probe", StringComparer.Ordinal);
    var pool = new PoolRuntime(vault, auth, quota, boundaryProbe);
    var browsers = new BrowserSessions(); var events = new StateEvents();
    var officialModelAuth = new OfficialModelAuth(Environment.GetEnvironmentVariable("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), vault);
    long requestCount = 0;
    var sessionOwners = new ConcurrentDictionary<string, string>();
    app.Use(async (context, next) =>
    {
        // No CORS. Only the exact local origin may use an authenticated browser session.
        if (context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != 3210
            || context.Request.Headers.TryGetValue("Origin", out var origin) && origin != "http://127.0.0.1:3210")
        { context.Response.StatusCode = 403; return; }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var path = context.Request.Path.Value ?? "";
        var publicPath = path is "/" or "/health" or "/auth/callback" or "/dashboard.js" or "/dashboard.css" || path.StartsWith("/dashboard/open/", StringComparison.Ordinal);
        var bearer = LocalCommands.EqualSecret(context.Request.Headers.Authorization.ToString(), "Bearer " + vault.LocalKey);
        var modelBearer = !bearer && await officialModelAuth.AcceptsAsync(context.Request.Method, path,
            context.Request.Headers.Authorization.ToString(), context.RequestAborted);
        var session = browsers.Find(context);
        var mutating = context.Request.Method is not ("GET" or "HEAD");
        var bearerOnly = path.StartsWith("/v1/", StringComparison.Ordinal) || path is "/accounts/login" or "/dashboard/session";
        if (!publicPath && !bearer && !modelBearer && (bearerOnly || session is null))
        { context.Response.StatusCode = 401; return; }
        if (!publicPath && !bearer && !modelBearer && mutating && !LocalCommands.EqualSecret(context.Request.Headers["X-Codex-Pool-CSRF"].ToString(), session!.Csrf))
        { context.Response.StatusCode = 403; return; }
        try { await next(); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (AuthFailure ex)
        { if (!context.Response.HasStarted) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = new { code = ex.Code } }); } else context.Abort(); }
        catch (NoEligibleAccountException)
        { if (!context.Response.HasStarted) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = new { code = "no_eligible_account" } }); } else context.Abort(); }
        catch (JsonException)
        { if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { code = "invalid_json" } }); } else context.Abort(); }
        catch (RequestBodyFailure ex)
        { if (!context.Response.HasStarted) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { error = new { code = ex.Code } }); } else context.Abort(); }
        catch
        { if (!context.Response.HasStarted) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = new { code = "internal_error" } }); } else context.Abort(); }
    });
    app.MapGet("/health", () => new { status = "running", stage = "acceptance", automaticSwitching = pool.Settings.AutoSwitch });
    object Status() => new { activeAccountId = vault.ActiveId, stage = "acceptance", automaticSwitching = pool.Settings.AutoSwitch, settings = pool.Settings,
        boundaryProbe, distinctUsers = vault.DistinctUsers, nativeDistinctUsers = vault.NativeDistinctUsers, liveMigrationValidated = false, quotaSource = "per_registration", accounts = vault.Views(auth.Health),
        nativeLoginAttempts = nativeLogin.PublicAttempts, usage = quota.Views, inFlightRequests = inFlight.ToArray(), recentRequests = recent.ToArray(), recentSwitches = pool.History };
    app.MapGet("/status", () => Status());
    app.MapGet("/events", (HttpContext context) => events.StreamAsync(context, Status));
    app.MapGet("/browser-session", (HttpContext context) => browsers.Find(context) is { } session
        ? Results.Ok(new { csrf = session.Csrf }) : Results.Unauthorized());
    app.MapPost("/dashboard/session", () => Results.Ok(new { url = "http://127.0.0.1:3210/dashboard/open/" + browsers.Issue() }));
    app.MapGet("/dashboard/open/{ticket}", (string ticket, HttpContext context) => browsers.Redeem(ticket, context) ? Results.Redirect("/") : Results.Unauthorized());
    foreach (var asset in new[] { (Path: "/", File: "index.html", Type: "text/html"), (Path: "/dashboard.js", File: "dashboard.js", Type: "text/javascript"), (Path: "/dashboard.css", File: "dashboard.css", Type: "text/css") })
        app.MapGet(asset.Path, () => Results.Stream(typeof(Vault).Assembly.GetManifestResourceStream("CodexPool.Daemon.Dashboard." + asset.File)!, asset.Type));
    app.MapGet("/settings", () => pool.Settings);
    app.MapPost("/settings", async (HttpContext context) =>
    {
        var settings = await context.Request.ReadFromJsonAsync<PoolSettings>(context.RequestAborted) ?? throw new AuthFailure("invalid_settings");
        try { StickyScheduler.Validate(settings); } catch (ArgumentException) { return Results.BadRequest(new { error = new { code = "invalid_thresholds" } }); }
        pool.UpdateSettings(settings); return Results.Ok(pool.Settings);
    });
    app.MapGet("/accounts", () => vault.Views(auth.Health));
    app.MapGet("/usage", async (HttpContext context) =>
    {
        foreach (var account in vault.Accounts.Where(x => x.ProviderKind == "native")) await quota.RefreshAsync(account.Id, context.RequestAborted);
        return quota.Views;
    });
    app.MapPost("/accounts/native-login", async (HttpContext context) =>
    {
        var input = await context.Request.ReadFromJsonAsync<LoginInput>(context.RequestAborted) ?? throw new AuthFailure("invalid_login_request");
        return Results.Json(new { attemptId = await nativeLogin.StartAsync(input.Name), state = "waiting_for_browser" });
    });
    app.MapPost("/accounts/{id}/refresh", async (string id, HttpContext context) =>
    {
        var before = vault.Accounts; var original = before.FirstOrDefault(x => x.Id == id) ?? throw new AuthFailure("account_not_found");
        var refreshed = await auth.ReadyAsync(id, context.RequestAborted, forceRefresh: true);
        return Results.Ok(new { accountId = refreshed.Id, auth = "ready", updatedAt = refreshed.UpdatedAt,
            accessTokenChanged = original.AccessToken != refreshed.AccessToken, refreshTokenRotated = original.RefreshToken != refreshed.RefreshToken,
            otherAccountCredentialsUnchanged = before.Where(x => x.Id != id).All(x => vault.Find(x.Id) is { } current
                && current.AccessToken == x.AccessToken && current.RefreshToken == x.RefreshToken) });
    });
    async Task<IResult> ModelCatalog(HttpContext context)
    {
        var selected = vault.Selected ?? throw new AuthFailure("no_authorized_account");
        var account = await auth.ReadyAsync(selected.Id, context.RequestAborted);
        using var request = new HttpRequestMessage(HttpMethod.Get, account.ProviderKind == "native"
            ? "https://chatgpt.com/backend-api/codex/models?client_version=0.157.1" : "https://api.openai.com/v1/models");
        request.Headers.Authorization = new("Bearer", account.AccessToken);
        if (account.ProviderKind == "native") request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account.BackendAccountId);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        if (!response.IsSuccessStatusCode) return Results.Json(new { error = new { code = "model_catalog_unavailable" } }, statusCode: (int)response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        using var content = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await stream.ReadAsync(buffer, context.RequestAborted); if (count == 0) break; if (content.Length + count > 2 * 1024 * 1024) throw new AuthFailure("oversized_model_catalog"); content.Write(buffer, 0, count); }
        using var json = JsonDocument.Parse(content.ToArray());
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new AuthFailure("unrecognized_model_catalog");
        if (context.Request.Path == "/v1/models") return Results.Json(json.RootElement.Clone());
        var visible = models.EnumerateArray().Where(x => x.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "list")
            .Select(x => new { slug = x.GetProperty("slug").GetString(), displayName = x.GetProperty("display_name").GetString() }).ToArray();
        return Results.Json(new { accountId = account.Id, models = visible });
    }
    app.MapGet("/models", (Func<HttpContext, Task<IResult>>)ModelCatalog);
    app.MapGet("/v1/models", (Func<HttpContext, Task<IResult>>)ModelCatalog);
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
        if (auth.Health(id) == "reauth_required" || !Vault.IsEligible(vault.Find(id)!))
            return Results.Conflict(new { error = new { code = "account_not_eligible" } });
        pool.Activate(id);
        return Results.Ok(new { activeAccountId = id, affects = "new_requests_only" });
    });
    foreach (var operation in new[] { "pause", "resume", "drain", "undrain", "enable", "disable" })
        app.MapPost("/accounts/{id}/" + operation, (string id) => vault.Control(id, operation)
            ? Results.Ok(new { accountId = id, operation, affects = "new_requests_only" })
            : Results.NotFound(new { error = new { code = "account_not_found" } }));
    // Built-in OpenAI first probes WebSocket. 426 selects Codex's supported HTTP/SSE fallback.
    // No model request or upstream bytes are sent for this transport negotiation.
    app.MapGet("/v1/responses", () => Results.StatusCode(StatusCodes.Status426UpgradeRequired));
    app.MapPost("/v1/responses", async (HttpContext context) =>
    {
        if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        { context.Response.StatusCode = 415; return; }
        var body = await ModelRequestBody.ReadAsync(context.Request.Body, context.Request.Headers.ContentEncoding.ToString(), context.RequestAborted);
        using var document = JsonDocument.Parse(body); var json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array
            || !json.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.True
            || !json.TryGetProperty("store", out var store) || store.ValueKind != JsonValueKind.False
            || !json.TryGetProperty("model", out var modelValue) || modelValue.ValueKind != JsonValueKind.String
            || !Regex.IsMatch(modelValue.GetString()!, "^[a-zA-Z0-9._:/-]{1,128}$"))
        { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { code = "invalid_responses_request" } }); return; }
        if (json.TryGetProperty("previous_response_id", out var previous) && previous.ValueKind != JsonValueKind.Null
            || json.TryGetProperty("conversation", out var conversation) && conversation.ValueKind != JsonValueKind.Null)
        { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { code = "server_continuation_unsupported" } }); return; }
        var selection = pool.Select();
        var account = await auth.ReadyAsync(selection.AccountId, context.RequestAborted);
        var candidates = new List<ModelLease>();
        ModelLease Lease(AccountCredential credential) => new(credential.Id, new(credential.ProviderKind == "native"
            ? "https://chatgpt.com/backend-api/codex/responses" : "https://api.openai.com/v1/responses"), credential.AccessToken, credential.BackendAccountId);
        candidates.Add(Lease(account));
        foreach (var id in pool.Candidates(selection).Skip(1))
        {
            try { candidates.Add(Lease(await auth.ReadyAsync(id, context.RequestAborted))); }
            catch (AuthFailure) { } // No model bytes were sent; this candidate cannot be used.
        }
        Interlocked.Increment(ref requestCount);
        var headers = context.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        headers.Remove("ChatGPT-Account-Id"); // Backend account routing is supplied only by validated credentials.
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
        var sink = new WebSink(context); var attemptedAccount = account.Id; var attemptCount = 0;
        var upstreamCompleted = false;
        var rejections = new List<object>(); string? failoverReason = null;
        try
        {
            result = await relay.ForwardAsync(body, "application/json", candidates, headers, sink, lifetime.Token,
                (lease, reason) => { pool.Rejected(lease, reason); failoverReason = reason; rejections.Add(new { account = lease.AccountId, reason }); },
                (lease, entering) =>
                {
                    if (entering) { attemptedAccount = lease.AccountId; attemptCount++; }
                    inFlight.AddOrUpdate(lease.AccountId, entering ? 1 : 0, (_, count) => Math.Max(0, count + (entering ? 1 : -1)));
                }, _ => upstreamCompleted = true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        { result = new(attemptedAccount, upstreamCompleted ? 200 : context.RequestAborted.IsCancellationRequested ? 499 : 504, attemptCount, sink.Started,
            upstreamCompleted ? "completed_client_disconnect" : context.RequestAborted.IsCancellationRequested ? "client_cancelled" : "request_deadline"); }
        pool.Complete(selection, result, failoverReason);
        if (headers.TryGetValue("session_id", out var completedSession)) sessionOwners[Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(completedSession)))] = result.AccountId;
        recent.Enqueue(new { timestamp = DateTimeOffset.UtcNow, requestId, account = result.AccountId, model = modelValue.GetString(),
            durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, status = result.StatusCode, result.Outcome, result.Attempts,
            failover = result.Attempts > 1, failoverReason, rejections,
            inputItemCount = input.GetArrayLength(), encryptedItemCount = input.EnumerateArray().Count(x => x.TryGetProperty("encrypted_content", out _)),
            toolResultCount = input.EnumerateArray().Count(x => x.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"),
            upstreamHeaderNames = result.UpstreamHeaderNames, upstreamContentType = result.UpstreamContentType, payloadKind = result.PayloadKind, prefixShape = result.PrefixShape });
        while (recent.Count > 100) recent.TryDequeue(out _);
        if (context.RequestAborted.IsCancellationRequested) return;
        if (result.Committed && result.Outcome is "stream_interrupted" or "request_deadline") context.Abort();
        else if (!result.Committed) { context.Response.StatusCode = 502; await context.Response.WriteAsJsonAsync(new { error = new { code = result.Outcome } }); }
    });
    app.MapFallback(() => Results.NotFound(new { error = new { code = "endpoint_not_supported_in_phase0" } }));
    Console.WriteLine("Codex Pool listening at http://127.0.0.1:3210 (live migration acceptance gate applies).");
    var eventTask = events.RunAsync(Status, app.Lifetime.ApplicationStopping);
    var quotaTask = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
            {
                foreach (var account in vault.Accounts.Where(x => x.ProviderKind == "native" && x.Enabled && x.AuthStatus == "ready"))
                    try { await quota.RefreshAsync(account.Id, app.Lifetime.ApplicationStopping); }
                    catch (Exception ex) when (ex is AuthFailure or HttpRequestException or JsonException or OperationCanceledException) { }
                if (pool.Settings.AutoSwitch) try { pool.Select(); } catch (NoEligibleAccountException) { }
            }
        }
        catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested) { }
    });
    await app.RunAsync(); await Task.WhenAll(eventTask, quotaTask); return 0;
}
catch { Console.Error.WriteLine("Daemon failed to start or stopped unexpectedly. Check port 3210 and single-instance ownership."); return 1; }

sealed record LoginInput(string Name, string? AccountId);
sealed class WebSink(HttpContext context) : IResponseSink
{
    public bool Started { get; private set; }
    public void Start(int statusCode, string contentType) { Started = true; context.Response.StatusCode = statusCode; context.Response.ContentType = contentType; }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    { await context.Response.Body.WriteAsync(bytes, cancellationToken); await context.Response.Body.FlushAsync(cancellationToken); }
}
