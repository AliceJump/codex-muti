
using System.Collections.Concurrent;
using System.Diagnostics;
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
    // Startup fallback when /v1/models omits client_version; prefer the query Codex sends.
    var modelClientVersion = CodexClientVersion.ResolveInstalled(Environment.GetEnvironmentVariable("CODEX_POOL_CLIENT_VERSION"));
    var builder = WebApplication.CreateBuilder([]);
    builder.Logging.ClearProviders();
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(11));
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(System.Net.IPAddress.Loopback, 3210);
        options.Limits.MaxRequestBodySize = ModelRequestBody.MaximumBytes;
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
    using var statistics = new UsageStatistics(directory);
    var inFlight = new ConcurrentDictionary<string, int>();
    var boundaryProbe = args.Contains("--live-boundary-probe", StringComparer.Ordinal);
    var pool = new PoolRuntime(vault, auth, quota, boundaryProbe, statistics.RecordSwitch);
    var browsers = new BrowserSessions(); var events = new StateEvents();
    var officialModelAuth = new OfficialModelAuth(Environment.GetEnvironmentVariable("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), vault);
    var sessionOwners = new ConcurrentDictionary<string, string>();
    app.Use(async (context, next) =>
    {
        // No CORS. Only the exact local origin may use an authenticated browser session.
        if (context.Request.Scheme != "http" || context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != 3210
            || context.Request.Headers.TryGetValue("Origin", out var origin) && origin != "http://127.0.0.1:3210")
        { context.Response.StatusCode = 403; return; }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var path = context.Request.Path.Value ?? "";
        var publicPath = path is "/" or "/health" or "/auth/callback" or "/dashboard.js" or "/dashboard.css"
            || path.StartsWith("/dashboard/open/", StringComparison.Ordinal);
        var bearer = LocalCommands.EqualSecret(context.Request.Headers.Authorization.ToString(), "Bearer " + vault.LocalKey);
        var clientKey = vault.AcceptsModelKey(context.Request.Headers.Authorization.ToString());
        if (clientKey && !ModelAccess.IsModelRoute(context.Request.Method, path))
        { context.Response.StatusCode = 403; return; }
        context.Items["modelClient"] = clientKey;
        var modelBearer = !bearer && !clientKey && await officialModelAuth.AcceptsAsync(context.Request.Method, path,
            context.Request.Headers.Authorization.ToString(), context.RequestAborted);
        var session = browsers.Find(context);
        var mutating = context.Request.Method is not ("GET" or "HEAD");
        var bearerOnly = path.StartsWith("/v1/", StringComparison.Ordinal) || path.StartsWith("/api/v1/", StringComparison.Ordinal) || path.StartsWith("/control/", StringComparison.Ordinal)
            || path is "/accounts/login" or "/dashboard/session";
        if (!publicPath && !bearer && !modelBearer && !clientKey && (bearerOnly || session is null))
        { context.Response.StatusCode = 401; return; }
        if (!publicPath && !bearer && !modelBearer && !clientKey && mutating && !LocalCommands.EqualSecret(context.Request.Headers["X-Codex-Pool-CSRF"].ToString(), session!.Csrf))
        { context.Response.StatusCode = 403; return; }
        try { await next(); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (AuthFailure ex)
        {
            if (context.Response.HasStarted) context.Abort();
            else if (path.StartsWith("/v1/", StringComparison.Ordinal) || path.StartsWith("/api/v1/", StringComparison.Ordinal))
            {
                var error = PoolAvailability.Auth(ex.Code); context.Response.StatusCode = error.Status; context.Response.ContentType = "application/json";
                if (error.RetryAfter is not null) context.Response.Headers["Retry-After"] = error.RetryAfter;
                await context.Response.Body.WriteAsync(error.Body(), context.RequestAborted);
            }
            else { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = new { code = ex.Code } }); }
        }
        catch (NoEligibleAccountException)
        { if (!context.Response.HasStarted) { var error = pool.Unavailable(model: context.Items["quotaModel"] as string); context.Response.StatusCode = error.Status; context.Response.ContentType = "application/json"; if (error.RetryAfter is not null) context.Response.Headers["Retry-After"] = error.RetryAfter; await context.Response.Body.WriteAsync(error.Body(), context.RequestAborted); } else context.Abort(); }
        catch (JsonException)
        { if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { code = "invalid_json" } }); } else context.Abort(); }
        catch (RequestBodyFailure ex)
        { if (!context.Response.HasStarted) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { error = new { code = ex.Code } }); } else context.Abort(); }
        catch (CompatibilityFailure ex)
        { if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { type = "invalid_request_error", code = ex.Code, message = "请求格式或参数暂不支持，请查阅机器人接入说明。" } }); } else context.Abort(); }
        catch
        { if (!context.Response.HasStarted) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = new { code = "internal_error" } }); } else context.Abort(); }
    });
    app.MapGet("/health", () => new { status = "running", stage = "acceptance", automaticSwitching = pool.Settings.AutoSwitch });
    ModelKeyEndpoints.Map(app, vault);
    object Status() => new { activeAccountId = vault.ActiveId, stage = "acceptance", automaticSwitching = pool.Settings.AutoSwitch, settings = pool.Settings,
        boundaryProbe, distinctUsers = vault.DistinctUsers, nativeDistinctUsers = vault.NativeDistinctUsers, liveMigrationValidated = false, quotaSource = "per_registration", accounts = vault.Views(auth.Health, quota.Snapshot), accountAvailability = pool.Availability(),
        nativeLoginAttempts = nativeLogin.PublicAttempts, usage = quota.Views, quotaRefreshErrors = quota.RefreshErrors, quotaSelectionError = pool.QuotaSelectionError,
        tokenStatistics = statistics.View() with { Daily = [] }, inFlightRequests = inFlight.ToArray(), recentRequests = statistics.RecentRequests, recentSwitches = statistics.RecentSwitches };
    app.MapGet("/status", () => Status());
    UsageEndpoints.Map(app, statistics);
    app.MapGet("/events", (HttpContext context) => events.StreamAsync(context, Status));
    app.MapGet("/browser-session", (HttpContext context) => browsers.Find(context) is { } session
        ? Results.Ok(new { csrf = session.Csrf }) : Results.Unauthorized());
    app.MapPost("/dashboard/session", () => Results.Ok(new { url = "http://127.0.0.1:3210/dashboard/open/" + browsers.Issue() }));
    app.MapGet("/dashboard/open/{ticket}", (string ticket, HttpContext context) => browsers.Redeem(ticket, context) ? Results.Redirect("/") : Results.Unauthorized());
    foreach (var asset in new[] { (Path: "/", File: "index.html", Type: "text/html"), (Path: "/dashboard.js", File: "dashboard.js", Type: "text/javascript"), (Path: "/dashboard.css", File: "dashboard.css", Type: "text/css") })
        app.MapGet(asset.Path, () => Results.Stream(typeof(Vault).Assembly.GetManifestResourceStream("CodexPool.Daemon.Dashboard." + asset.File)!, asset.Type));
    app.MapGet("/settings", () => pool.Settings);
    DaemonControlEndpoints.Map(app, vault.LocalKey, app.Lifetime.StopApplication,
        () => inFlight.Values.All(x => x == 0) && vault.Accounts.All(x => !auth.IsRefreshing(x.Id)));
    app.MapPost("/settings", async (HttpContext context) =>
    {
        var settings = await context.Request.ReadFromJsonAsync<PoolSettings>(context.RequestAborted) ?? throw new AuthFailure("invalid_settings");
        try { StickyScheduler.Validate(settings); } catch (ArgumentException) { return Results.BadRequest(new { error = new { code = "invalid_thresholds" } }); }
        pool.UpdateSettings(settings); return Results.Ok(pool.Settings);
    });
    app.MapGet("/accounts", () => vault.Views(auth.Health, quota.Snapshot));
    app.MapGet("/usage", async (HttpContext context) =>
    {
        await quota.RefreshAllAsync(vault.Accounts.Where(x => x.ProviderKind == "native").Select(x => x.Id), context.RequestAborted, force: true);
        // Reuse sticky auto-switch after explicit refresh so exhausted weekly/5h can fail over without waiting for a model request.
        pool.SelectAfterQuotaRefresh();
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
        using var reservation = pool.ReserveCatalog();
        var candidates = await pool.PrepareAsync(reservation, context.RequestAborted);
        // Forward Codex's client_version so the picker catalog matches the installed CLI (e.g. gpt-6.1-sol).
        var catalogVersion = CodexClientVersion.FromQueryOrFallback(context.Request.Query["client_version"], modelClientVersion);
        for (var index = 0; index < candidates.Length; index++)
        {
            var account = candidates[index]; var native = account.Endpoint.Host == "chatgpt.com";
            using var request = new HttpRequestMessage(HttpMethod.Get, native
                ? $"https://chatgpt.com/backend-api/codex/models?client_version={Uri.EscapeDataString(catalogVersion)}" : "https://api.openai.com/v1/models");
            request.Headers.Authorization = new("Bearer", account.AccessToken);
            if (native) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account.BackendAccountId);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ModelErrors.FromResponseAsync(response, context.RequestAborted);
                pool.RecordFailure(account.AccountId, error);
                if (error.FailoverReason is not null && index + 1 < candidates.Length) continue;
                if (error.FailoverReason is not null) error = pool.Unavailable(error, reservation.CandidateIds.ToArray(), reservation.AutoSwitch);
                if (error.RetryAfter is not null) context.Response.Headers["Retry-After"] = error.RetryAfter;
                return Results.Content(Encoding.UTF8.GetString(error.Body()), "application/json", statusCode: error.Status);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            using var content = new MemoryStream(); var buffer = new byte[8192];
            while (true) { var count = await stream.ReadAsync(buffer, context.RequestAborted); if (count == 0) break; if (content.Length + count > 2 * 1024 * 1024) throw new AuthFailure("oversized_model_catalog"); content.Write(buffer, 0, count); }
            using var json = JsonDocument.Parse(content.ToArray());
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new AuthFailure("unrecognized_model_catalog");
            if (context.Request.Path == "/api/v1/models" || context.Items["modelClient"] is true)
                return Results.Json(CompatibilityCatalog.Models(json.RootElement));
            if (context.Request.Path == "/v1/models") return Results.Json(json.RootElement.Clone());
            var visible = models.EnumerateArray().Where(x => x.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "list")
                .Select(x => new { slug = x.GetProperty("slug").GetString(), displayName = x.GetProperty("display_name").GetString() }).ToArray();
            return Results.Json(new { accountId = account.AccountId, models = visible });
        }
        throw new NoEligibleAccountException();
    }
    app.MapGet("/models", (Func<HttpContext, Task<IResult>>)ModelCatalog);
    app.MapGet("/v1/models", (Func<HttpContext, Task<IResult>>)ModelCatalog);
    app.MapGet("/api/v1/models", (Func<HttpContext, Task<IResult>>)ModelCatalog);
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
        var account = vault.Find(id);
        if (account is null) return Results.NotFound(new { error = new { code = "account_not_found" } });
        if (auth.Health(id) == "reauth_required" || !Vault.IsEligible(account))
            return Results.Conflict(new { error = new { code = "account_not_eligible" } });
        try { pool.Activate(id); }
        catch (AuthFailure ex) when (ex.Code == "live_migration_gate_pending")
        { return Results.Conflict(new { error = new { code = ex.Code } }); }
        catch (NoEligibleAccountException) { return Results.Conflict(new { error = new { code = "account_not_eligible" } }); }
        return Results.Ok(new { activeAccountId = id, affects = "new_requests_only" });
    });
    foreach (var operation in new[] { "pause", "resume", "drain", "undrain", "enable", "disable" })
        app.MapPost("/accounts/{id}/" + operation, (string id) => vault.Control(id, operation)
            ? Results.Ok(new { accountId = id, operation, affects = "new_requests_only" })
            : Results.NotFound(new { error = new { code = "account_not_found" } }));
    app.MapPost("/accounts/{id}/rename", async (string id, HttpContext context) =>
    {
        if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        var input = await context.Request.ReadFromJsonAsync<RenameInput>(context.RequestAborted);
        try
        {
            return vault.Rename(id, input?.Name)
                ? Results.Ok(new { accountId = id, affects = "display_name_only" })
                : Results.NotFound(new { error = new { code = "account_not_found" } });
        }
        catch (ArgumentException) { return Results.BadRequest(new { error = new { code = "invalid_account_name" } }); }
    });
    app.MapPost("/accounts/{id}/delete", (string id) =>
    {
        try
        {
            if (!pool.Delete(id, out var newActiveId)) return Results.NotFound(new { error = new { code = "account_not_found" } });
            return Results.Ok(new { deleted = id, activeAccountId = newActiveId, affects = "credentials_removed" });
        }
        catch (AccountDeletionRejected ex) { return Results.Conflict(new { error = new { code = ex.Code } }); }
    });

    // Built-in OpenAI first probes WebSocket. 426 selects Codex's supported HTTP/SSE fallback.
    // No model request or upstream bytes are sent for this transport negotiation.
    app.MapGet("/v1/responses", () => Results.StatusCode(StatusCodes.Status426UpgradeRequired));
    async Task ModelRequest(HttpContext context)
    {
        if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        { context.Response.StatusCode = 415; return; }
        var body = await ModelRequestBody.ReadAsync(context.Request.Body, context.Request.Headers.ContentEncoding.ToString(), context.RequestAborted);
        using var document = JsonDocument.Parse(body); var json = document.RootElement;
        var compatible = context.Items["modelClient"] is true || context.Request.Path.StartsWithSegments("/api/v1")
            || context.Request.Path.Value is "/v1/chat/completions" or "/v1/images/generations";
        CompatibilityRequest? translation = null;
        string requestedModel;
        if (compatible)
        {
            var kind = context.Request.Path.Value!.EndsWith("/images/generations", StringComparison.Ordinal) ? CompatibilityKind.Image
                : context.Request.Path.Value.EndsWith("/chat/completions", StringComparison.Ordinal) ? CompatibilityKind.Chat : CompatibilityKind.Responses;
            translation = CompatibilityRequest.Parse(json, kind);
            body = translation.Body; requestedModel = translation.Model;
        }
        else
        {
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array
                || !json.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.True
                || !json.TryGetProperty("store", out var store) || store.ValueKind != JsonValueKind.False
                || !json.TryGetProperty("model", out var modelValue) || modelValue.ValueKind != JsonValueKind.String
                || !Regex.IsMatch(modelValue.GetString()!, "^[a-zA-Z0-9._:/-]{1,128}$"))
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { code = "invalid_responses_request" } }); return; }
            if (json.TryGetProperty("previous_response_id", out var previous) && previous.ValueKind != JsonValueKind.Null
                || json.TryGetProperty("conversation", out var conversation) && conversation.ValueKind != JsonValueKind.Null)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = new { code = "server_continuation_unsupported" } }); return; }
            requestedModel = modelValue.GetString()!;
        }
        context.Items["quotaModel"] = requestedModel;
        using var reservation = pool.ReserveModelRequest(requestedModel);
        var selection = reservation.Selection;
        var candidates = await pool.PrepareAsync(reservation, context.RequestAborted);
        if (translation?.Kind == CompatibilityKind.Image && candidates.Any(x => x.Endpoint.Host != "chatgpt.com"))
            throw new CompatibilityFailure("image_requires_native_account");
        var account = candidates[0];
        var headers = context.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        headers.Remove("ChatGPT-Account-Id"); // Backend account routing is supplied only by validated credentials.
        // Global sticky selection is unchanged. This only decides whether account-bound turn state still belongs here.
        ResponsesHeaders.BindTurnState(headers, sessionOwners, account.AccountId);
        var started = Stopwatch.GetTimestamp();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); lifetime.CancelAfter(TimeSpan.FromMinutes(10));
        RelayResult result = new(account.AccountId, 500, 0, false, "transport_error");
        var sink = new WebSink(context);
        using var converter = translation is null ? null : new CompatibilitySink(sink, translation);
        IResponseSink responseSink = converter is null ? sink : converter;
        var attemptedAccount = account.AccountId; var attemptCount = 0;
        var upstreamCompleted = false;
        ResponseTokenUsage? tokenUsage = null;
        string? failoverReason = null;
        try
        {
            result = await relay.ForwardAsync(body, "application/json", candidates, headers, responseSink, lifetime.Token,
                (_, reason) => failoverReason = reason,
                (lease, entering) =>
                {
                    if (entering) { attemptedAccount = lease.AccountId; attemptCount++; }
                    inFlight.AddOrUpdate(lease.AccountId, entering ? 1 : 0, (_, count) => Math.Max(0, count + (entering ? 1 : -1)));
                }, lease => { upstreamCompleted = true; pool.RecordSuccess(lease.AccountId); },
                (lease, error) => pool.RecordFailure(lease.AccountId, error), error => pool.Unavailable(error, reservation.CandidateIds.ToArray(), reservation.AutoSwitch, requestedModel),
                (_, value) => tokenUsage = value);
            if (converter is not null && result.Outcome is "stream_forwarded" or "empty_stream")
            {
                await converter.FinishAsync(lifetime.Token);
                if (converter.Failed) result = result with { StatusCode = 502, Outcome = "generation_failed" };
            }
            if (converter is not null && result.StatusCode < 400 && (!result.Committed || result.Outcome == "stream_interrupted"))
                result = result with { StatusCode = 502 };
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        { result = new(attemptedAccount, upstreamCompleted ? 200 : context.RequestAborted.IsCancellationRequested ? 499 : 504, attemptCount, sink.Started || converter?.Consumed == true,
            upstreamCompleted ? "completed_client_disconnect" : context.RequestAborted.IsCancellationRequested ? "client_cancelled" : "request_deadline"); }
        finally
        {
            // Account admission/control persistence errors must not erase observed consumption.
            if (attemptCount > 0) statistics.RecordRequest(new(DateTimeOffset.UtcNow, attemptedAccount, requestedModel,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, result.StatusCode, result.Outcome, attemptCount, tokenUsage));
        }
        pool.Complete(selection, result, failoverReason, requestedModel);
        ResponsesHeaders.Remember(headers, sessionOwners, result.AccountId);
        if (context.RequestAborted.IsCancellationRequested) return;
        if (converter is not null)
        {
            if (result.Outcome is "stream_interrupted" or "request_deadline" || !result.Committed)
            {
                if (context.Response.HasStarted) context.Abort();
                else { context.Response.StatusCode = result.StatusCode >= 400 ? result.StatusCode : 502;
                    await context.Response.WriteAsJsonAsync(new { error = new { type = "proxy_transport_error", code = converter.FailureCode ?? "generation_interrupted", message = "生成未能完成，请稍后重试。" } }); }
            }
            return;
        }
        if (result.Committed && result.Outcome is "stream_interrupted" or "request_deadline") context.Abort();
        else if (!result.Committed) { context.Response.StatusCode = result.StatusCode; await context.Response.WriteAsJsonAsync(new { error = new { type = "proxy_transport_error", code = result.Outcome, message = "模型传输未能完成。" } }); }
    }
    foreach (var route in new[] { "/v1/responses", "/v1/chat/completions", "/v1/images/generations",
        "/api/v1/responses", "/api/v1/chat/completions", "/api/v1/images/generations" })
        app.MapPost(route, ModelRequest);
    app.MapFallback(() => Results.NotFound(new { error = new { code = "endpoint_not_supported_in_phase0" } }));
    Console.WriteLine("Codex Pool listening at http://127.0.0.1:3210 (live migration acceptance gate applies).");
    var eventTask = events.RunAsync(Status, app.Lifetime.ApplicationStopping);
    var statisticsTask = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try { while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping)) statistics.Flush(); }
        catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested) { }
        finally { statistics.Flush(); }
    });
    var quotaTask = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
            {
                await quota.RefreshAllAsync(vault.Accounts.Where(x => x.ProviderKind == "native" && x.Enabled && x.AuthStatus == "ready")
                    .Select(x => x.Id), app.Lifetime.ApplicationStopping);
                pool.SelectAfterQuotaRefresh();
            }
        }
        catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested) { }
    });
    await app.RunAsync(); await Task.WhenAll(eventTask, quotaTask, statisticsTask);
    // ApplicationStopping occurs before Kestrel drains its accepted requests.
    statistics.Flush(); return 0;
}
catch { Console.Error.WriteLine("Daemon failed to start or stopped unexpectedly. Check port 3210 and single-instance ownership."); return 1; }



sealed record LoginInput(string Name, string? AccountId);
sealed record RenameInput(string? Name);
sealed class WebSink(HttpContext context) : IResponseSink
{
    public void SetHeader(string name, string value) { if (!context.Response.HasStarted && name == "Retry-After") context.Response.Headers[name] = value; }
    public bool Started { get; private set; }
    public void Start(int statusCode, string contentType) { Started = true; context.Response.StatusCode = statusCode; context.Response.ContentType = contentType; }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    { await context.Response.Body.WriteAsync(bytes, cancellationToken); await context.Response.Body.FlushAsync(cancellationToken); }
}
