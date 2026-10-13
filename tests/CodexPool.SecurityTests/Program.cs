
using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexPool.Daemon;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using CodexPool.Core;

var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".research", "security-" + Guid.NewGuid().ToString("N")));
var tests = new (string Name, Func<Task> Run)[]
{
    ("DPAPI storage never contains plaintext canaries; public views redact", Storage),
    ("OIDC rejects wrong signature, issuer, audience, nonce and expiry", IdToken),
    ("OAuth PKCE and state isolation; callback is one-use", Login),
    ("Returning login retains registration and host identity", Reauthorize),
    ("Missing plan grant and swapped registration are rejected", Grant),
    ("A/B refresh rotation and concurrent single-flight are independent", Refresh),
    ("Permanent refresh failure suppresses subsequent refresh attempts", InvalidGrant),
    ("Tampered ciphertext never falls back to plaintext", Tamper),
    ("Local API authenticates controls, rejects Origin/Host, enforces one daemon", LocalApi),
    ("Account controls preserve credentials and cannot activate excluded accounts", AccountControls),
    ("Crash during rotation leaves durable refresh_pending and never reuses token", CrashRefresh),
    ("Native registrations sharing official client ID remain separate", NativeRegistrations),
    ("Quota windows use durations and reject malformed percentages", QuotaParser),
    ("Durable quota, scheduler and manual epoch survive control races", PoolRouting),
    ("Native JSON refresh isolates A/B and accepts omitted replacement refresh token", NativeRefresh),
    ("Official model ingress accepts A/B, tracks rotation, excludes controls and never writes login", OfficialIngress),
    ("Native zstd request bytes survive decoding; corrupt, unsupported and oversized bodies fail safely", CompressedRequest),
    ("Delete removes vault entry and rejects unknown id", AccountDelete),
    ("Delete protects selected and standby model reservations, releases on failure and clears empty scheduler", DeleteModelReservations),
    ("Delete refuses active handover beyond the migration gate and preserves eligible routing", DeleteMigrationGate),
    ("Delete coordinates refresh, quota, catalog and reauthorization with deterministic barriers", DeleteBackgroundOperations),
    ("Rename preserves credentials, quotas, controls and active epoch through refresh and reauthorization", AccountRename),
    ("Catalog client_version prefers Codex query and keeps visibility=list Sol slugs", CatalogClientVersion)
};
if (args.Length != 0)
{
    if (args.Length != 2 || args[0] is not ("--filter" or "--skip")) return 2;
    tests = tests.Where(x => x.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase) == (args[0] == "--filter")).ToArray();
    if (tests.Length == 0) return 2;
}
var failed = 0;
foreach (var (name, run) in tests)
{ try { await run(); Console.WriteLine("PASS " + name); } catch { failed++; Console.WriteLine("FAIL " + name); } }
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed; synthetic credentials only");
return failed == 0 ? 0 : 1;

Vault Store(string name) => new(Path.Combine(root, name));
static void Check(bool value) { if (!value) throw new InvalidOperationException(); }
static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
    .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x.ElementAtOrDefault(1) ?? ""));
static AccountCredential Account(string id) => new()
{
    Id = id, Name = id, Subject = "sub-" + id, ClientId = "client-" + id,
    AccessToken = "ACCESS-CANARY-" + id, RefreshToken = "REFRESH-CANARY-" + id, IdToken = "ID-CANARY-" + id,
    Scope = "openid offline_access resource.invoke chatgpt.tokens.use.direct", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), UpdatedAt = DateTimeOffset.UtcNow
};
async Task OfficialIngress()
{
    var directory = Path.Combine(root, "official-ingress");
    var vault = Store("official-ingress-vault");
    var a = Account("A"); a.ProviderKind = "native"; a.BackendAccountId = "backend-A"; vault.Add(a);
    var b = Account("B"); b.ProviderKind = "native"; b.BackendAccountId = "backend-B"; vault.Add(b);
    Directory.CreateDirectory(directory);
    var file = Path.Combine(directory, "auth.json");
    var auth = new OfficialModelAuth(directory, vault);
    async Task Write(string token) => await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
    { auth_mode = "chatgpt", tokens = new { access_token = token, account_id = "official", refresh_token = "UNTOUCHED-REFRESH" } }));
    await Write("OFFICIAL-CANARY-1"); var original = await File.ReadAllBytesAsync(file);
    foreach (var token in new[] { a.AccessToken, b.AccessToken, "OFFICIAL-CANARY-1" })
    {
        Check(await auth.AcceptsAsync("POST", "/v1/responses", "Bearer " + token, default));
        Check(await auth.AcceptsAsync("GET", "/v1/responses", "Bearer " + token, default));
        Check(await auth.AcceptsAsync("GET", "/v1/models", "Bearer " + token, default));
        Check(!await auth.AcceptsAsync("POST", "/v1/models", "Bearer " + token, default));
        foreach (var path in new[] { "/status", "/settings", "/accounts/A/activate", "/accounts/A/rename", "/dashboard/session", "/v1/other" })
            Check(!await auth.AcceptsAsync("POST", path, "Bearer " + token, default));
        Check(!await auth.AcceptsAsync("DELETE", "/v1/responses", "Bearer " + token, default));
    }
    var afterValidation = await File.ReadAllBytesAsync(file);
    Check(original.SequenceEqual(afterValidation));
    Check(!await auth.AcceptsAsync("POST", "/v1/responses", "Bearer UNKNOWN-CANARY", default));
    await Write("OFFICIAL-CANARY-2");
    Check(!await auth.AcceptsAsync("POST", "/v1/responses", "Bearer OFFICIAL-CANARY-1", default));
    Check(await auth.AcceptsAsync("POST", "/v1/responses", "Bearer OFFICIAL-CANARY-2", default));
    vault.ReplaceTokens(a.Id, a.RefreshToken, new() { AccessToken = "A-ROTATED", RefreshToken = "A-REFRESH-ROTATED", Scope = "native_codex", ExpiresIn = 3600 });
    Check(!await auth.AcceptsAsync("POST", "/v1/responses", "Bearer " + a.AccessToken, default));
    Check(await auth.AcceptsAsync("POST", "/v1/responses", "Bearer A-ROTATED", default));
    Check(await auth.AcceptsAsync("POST", "/v1/responses", "Bearer " + b.AccessToken, default));
    foreach (var malformed in new[] { "[]", "{", "{}", "{\"auth_mode\":\"apikey\",\"tokens\":{\"access_token\":\"OFFICIAL-CANARY-2\",\"account_id\":\"official\"}}", new string('x', 129 * 1024) })
    {
        await File.WriteAllTextAsync(file, malformed);
        Check(!await auth.AcceptsAsync("POST", "/v1/responses", "Bearer OFFICIAL-CANARY-2", default));
    }
    Check(vault.Find(b.Id)?.RefreshToken == b.RefreshToken && vault.Find(a.Id)?.RefreshToken == "A-REFRESH-ROTATED");
}
async Task CompressedRequest()
{
    var original = Encoding.UTF8.GetBytes("{\"model\":\"gpt-6.1-sol\",\"input\":[{\"type\":\"function_call_output\",\"output\":\"工具结果\"}],\"opaque\":true}");
    using var compressor = new ZstdSharp.Compressor();
    var compressed = compressor.Wrap(original).ToArray();
    foreach (var encoding in new[] { "", "zstd" })
    {
        using var source = new MemoryStream(encoding.Length == 0 ? original : compressed);
        var decoded = await ModelRequestBody.ReadAsync(source, encoding, default);
        Check(decoded.SequenceEqual(original));
    }
    foreach (var (payload, encoding, limit, status) in new[]
    {
        (compressed, "gzip", ModelRequestBody.MaximumBytes, 415),
        (compressed, "zstd,gzip", ModelRequestBody.MaximumBytes, 415),
        (original, "zstd", ModelRequestBody.MaximumBytes, 400),
        (compressed[..^1], "zstd", ModelRequestBody.MaximumBytes, 400),
        (compressed, "zstd", original.Length - 1, 413),
        (original, "", original.Length - 1, 413)
    })
    {
        using var source = new MemoryStream(payload);
        try { await ModelRequestBody.ReadAsync(source, encoding, default, limit); Check(false); }
        catch (RequestBodyFailure ex) { Check(ex.Status == status && !ex.Message.Contains("工具结果")); }
    }
    using var canceledSource = new MemoryStream(compressed);
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await ModelRequestBody.ReadAsync(canceledSource, "zstd", canceled.Token); Check(false); }
    catch (OperationCanceledException) { }
}
async Task Storage()
{
    var vault = Store("storage"); vault.Add(Account("A")); vault.Add(Account("B"));
    var onDisk = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(root, "storage", "vault.dpapi")));
    var publicView = JsonSerializer.Serialize(vault.PublicAccounts);
    foreach (var secret in new[] { "ACCESS-CANARY", "REFRESH-CANARY", "ID-CANARY" }) Check(!onDisk.Contains(secret) && !publicView.Contains(secret));
    var reopened = Store("storage"); Check(reopened.Find("A")?.RefreshToken == "REFRESH-CANARY-A" && reopened.Find("B")?.AccessToken == "ACCESS-CANARY-B");
    Check(reopened.HostId == vault.HostId && reopened.LocalKey == vault.LocalKey);
    Check(!Account("A").ToString().Contains("CANARY"));
}
async Task IdToken()
{
    var vault = Store("id-token"); using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer);
    var oauth = new OAuth(http, vault);
    Check(await oauth.ValidateIdTokenAsync(issuer.Token("client-A", "sub-A", "nonce"), "client-A", "nonce", default) == "sub-A");
    var invalid = new[]
    {
        issuer.Token("wrong-client", "sub-A", "nonce"),
        issuer.Token("client-A", "sub-A", "wrong-nonce"),
        issuer.Token("client-A", "sub-A", "nonce", issuer: "https://evil.invalid"),
        issuer.Token("client-A", "sub-A", "nonce", expired: true),
        issuer.Token("client-A", "sub-A", "nonce", forged: true), "unsigned.not.a.token"
    };
    foreach (var token in invalid) await Rejected(() => oauth.ValidateIdTokenAsync(token, "client-A", "nonce", default));
}
async Task Login()
{
    var vault = Store("login"); using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var oauth = new OAuth(http, vault);
    var first = Query(new Uri(await oauth.BeginAsync("A", null, default)).Query);
    var second = Query(new Uri(await oauth.BeginAsync("B", null, default)).Query);
    Check(first["client_id"] == "dynamic_agent_client" && first["ext_agent_host_id"] == vault.HostId && first["state"] != second["state"]);
    Check(first["nonce"] != second["nonce"] && first["code_challenge"] != second["code_challenge"] && first["code_challenge_method"] == "S256");
    issuer.Nonce = first["nonce"]; issuer.ExpectedChallenge = first["code_challenge"];
    await Rejected(() => oauth.CompleteAsync("unknown-state", "code", "client-A", null, default));
    var id = await oauth.CompleteAsync(first["state"], "code", "client-A", null, default);
    Check(vault.Find(id)?.ClientId == "client-A" && issuer.CodeCalls == 1 && issuer.ValidPkce);
    await Rejected(() => oauth.CompleteAsync(first["state"], "code", "client-A", null, default)); Check(issuer.CodeCalls == 1);
    await Rejected(() => oauth.CompleteAsync(second["state"], "code", null, null, default));
}
async Task Reauthorize()
{
    var vault = Store("reauth"); vault.Add(Account("A")); using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var oauth = new OAuth(http, vault);
    var returning = Query(new Uri(await oauth.BeginAsync("A", "A", default)).Query);
    Check(returning["client_id"] == "client-A" && returning["id_token_hint"] == "ID-CANARY-A" && !returning.ContainsKey("agent_name_hint"));
    issuer.Nonce = returning["nonce"]; var id = await oauth.CompleteAsync(returning["state"], "code", null, null, default);
    Check(id == "A" && vault.PublicAccounts.Length == 1);
    var swapped = Query(new Uri(await oauth.BeginAsync("A", "A", default)).Query);
    await Rejected(() => oauth.CompleteAsync(swapped["state"], "code", "client-B", null, default));
    Check(vault.Find("A")?.ClientId == "client-A");
}
async Task Grant()
{
    var vault = Store("grant"); using var issuer = new FakeIssuer { GrantPlan = false }; using var http = new HttpClient(issuer); var oauth = new OAuth(http, vault);
    var login = Query(new Uri(await oauth.BeginAsync("A", null, default)).Query); issuer.Nonce = login["nonce"];
    await Rejected(() => oauth.CompleteAsync(login["state"], "code", "client-A", null, default)); Check(vault.PublicAccounts.Length == 0);
}
async Task Refresh()
{
    var vault = Store("refresh"); vault.Add(Account("A")); vault.Add(Account("B")); using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var oauth = new OAuth(http, vault);
    var bBefore = JsonSerializer.Serialize(vault.Find("B"));
    await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => oauth.ReadyAsync("A", default)));
    Check(issuer.RefreshCalls == 1 && JsonSerializer.Serialize(vault.Find("B")) == bBefore);
    Check(vault.Find("A")!.RefreshToken != "REFRESH-CANARY-A");
    var aBefore = JsonSerializer.Serialize(vault.Find("A")); await oauth.ReadyAsync("B", default);
    Check(issuer.RefreshCalls == 2 && JsonSerializer.Serialize(vault.Find("A")) == aBefore);
    Check(issuer.UsedRefreshTokens.SequenceEqual(new[] { "REFRESH-CANARY-A", "REFRESH-CANARY-B" }));
}
async Task InvalidGrant()
{
    var vault = Store("invalid-grant"); vault.Add(Account("A")); using var issuer = new FakeIssuer { RejectRefresh = true }; using var http = new HttpClient(issuer); var oauth = new OAuth(http, vault);
    await Rejected(() => oauth.ReadyAsync("A", default)); await Rejected(() => oauth.ReadyAsync("A", default));
    Check(issuer.RefreshCalls == 1 && vault.Find("A")!.RefreshToken == "REFRESH-CANARY-A");
    var restarted = new OAuth(http, Store("invalid-grant"));
    await Rejected(() => restarted.ReadyAsync("A", default)); Check(issuer.RefreshCalls == 1);
}
async Task Tamper()
{
    var vault = Store("tamper"); vault.Add(Account("A")); var path = Path.Combine(root, "tamper", "vault.dpapi");
    var data = await File.ReadAllBytesAsync(path); data[^1] ^= 0xff; await File.WriteAllBytesAsync(path, data);
    try { Store("tamper"); throw new InvalidOperationException(); } catch (CryptographicException) { }
}
Task AccountControls()
{
    var vault = Store("controls"); vault.Add(Account("A")); vault.Add(Account("B"));
    var before = vault.Find("B")!.RefreshToken;
    Check(vault.Control("B", "pause") && !vault.ActivateForProbe("B"));
    Check(vault.Control("B", "resume") && vault.ActivateForProbe("B"));
    Check(vault.Control("B", "drain") && !vault.ActivateForProbe("B"));
    Check(vault.Control("B", "undrain") && vault.ActivateForProbe("B"));
    Check(vault.Control("B", "disable") && !vault.ActivateForProbe("B"));
    Check(vault.Control("B", "enable") && vault.ActivateForProbe("B"));
    Check(vault.Find("B")!.RefreshToken == before && !vault.Control("missing", "pause"));
    Check(Store("controls").ActiveId == "B"); return Task.CompletedTask;
}
async Task CrashRefresh()
{
    var vault = Store("crash-refresh"); vault.Add(Account("A")); vault.SetAuthStatus("A", "refresh_pending");
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer);
    var reopened = Store("crash-refresh");
    await Rejected(() => new OAuth(http, reopened).ReadyAsync("A", default)); Check(issuer.RefreshCalls == 0);
}
Task NativeRegistrations()
{
    var vault = Store("native-registrations");
    foreach (var id in new[] { "A", "B" })
    {
        var template = Account(id);
        vault.Add(new() { Id = id, Name = id, Subject = template.Subject, ClientId = NativeLogin.NativeClientId,
            ProviderKind = "native", BackendAccountId = "backend-" + id, AccessToken = template.AccessToken,
            RefreshToken = template.RefreshToken, IdToken = template.IdToken, Scope = "native_codex", ExpiresAt = template.ExpiresAt });
    }
    Check(vault.PublicAccounts.Length == 2 && vault.DistinctUsers == 2 && vault.Find("A")!.RefreshToken != vault.Find("B")!.RefreshToken);
    return Task.CompletedTask;
}
async Task NativeRefresh()
{
    var vault = Store("native-refresh");
    foreach (var id in new[] { "A", "B" })
    {
        var template = Account(id);
        vault.Add(new() { Id = id, Name = id, Subject = template.Subject, ClientId = NativeLogin.NativeClientId,
            ProviderKind = "native", BackendAccountId = "backend-" + id, AccessToken = template.AccessToken,
            RefreshToken = template.RefreshToken, IdToken = template.IdToken, Scope = "native_codex", ExpiresAt = template.ExpiresAt });
    }
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var oauth = new OAuth(http, vault);
    var bBefore = JsonSerializer.Serialize(vault.Find("B"));
    await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => oauth.ReadyAsync("A", default)));
    Check(issuer.RefreshCalls == 1 && JsonSerializer.Serialize(vault.Find("B")) == bBefore);
    var aBefore = JsonSerializer.Serialize(vault.Find("A")); await oauth.ReadyAsync("B", default);
    Check(issuer.RefreshCalls == 2 && JsonSerializer.Serialize(vault.Find("A")) == aBefore);
    var old = vault.Find("A")!.RefreshToken; issuer.OmitNativeRefresh = true;
    await oauth.ReadyAsync("A", default, forceRefresh: true);
    Check(vault.Find("A")!.RefreshToken == old && vault.Find("A")!.AuthStatus == "ready");
}
Task QuotaParser()
{
    var valid = """
        {"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":95,"limit_window_seconds":604800,"reset_at":1791810000},"secondary_window":{"used_percent":10,"limit_window_seconds":18000,"reset_at":1791813000}}}
        """;
    var parsed = Quota.Parse("A", Encoding.UTF8.GetBytes(valid));
    Check(parsed.Windows[0].Kind == "weekly" && parsed.Windows[1].Kind == "5h" && parsed.Windows[0].RemainingPercent == 5);
    var invalid = valid.Replace("\"used_percent\":95", "\"used_percent\":\"secret-value\"").Replace("\"used_percent\":10", "\"used_percent\":-5");
    Check(Quota.Parse("A", Encoding.UTF8.GetBytes(invalid)).Windows.Count == 0);
    Check(Quota.Parse("A", "{\"rate_limit\":null}"u8.ToArray()).Windows.Count == 0);
    return Task.CompletedTask;
}
Task PoolRouting()
{
    var vault = Store("pool-routing"); vault.Add(Account("A")); vault.Add(Account("B"));
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
    var quota = new Quota(http, auth, vault); var pool = new PoolRuntime(vault, auth, quota, true);
    pool.UpdateSettings(new(true)); var a = pool.Select(); Check(a.AccountId == "A");
    pool.Rejected(new("A", new("https://api.openai.com/v1/responses"), "canary"), "quota_rejected");
    pool.Complete(a, new("B", 200, 2, true, "stream_forwarded")); Check(vault.ActiveId == "B");
    var b = pool.Select(); Check(b.AccountId == "B");
    vault.Cooldown("A", DateTimeOffset.UtcNow.AddSeconds(-1)); pool.Activate("A");
    pool.Complete(b, new("B", 200, 1, true, "stream_forwarded")); Check(vault.ActiveId == "A");
    vault.SaveQuota(new("A", "native_codex_usage", "plus", DateTimeOffset.UtcNow,
        [new("5h", 18000, 96, 4, DateTimeOffset.UtcNow.AddHours(1))]));
    var reopened = Store("pool-routing"); var quota2 = new Quota(http, auth, reopened);
    Check(quota2.Snapshot("A")!.Windows[0].UsedPercent == 96 && reopened.Settings.AutoSwitch);
    Check(new PoolRuntime(reopened, auth, quota2, true).Select().AccountId == "B");
    var production = new PoolRuntime(reopened, auth, quota2, false); Check(!production.Settings.AutoSwitch);
    try { production.UpdateSettings(new(true)); Check(false); } catch (AuthFailure) { }
    return Task.CompletedTask;
}
async Task LocalApi()
{
    var vault = Store("http-api");
    var officialHome = Path.Combine(root, "http-api-official"); Directory.CreateDirectory(officialHome);
    await File.WriteAllTextAsync(Path.Combine(officialHome, "auth.json"), JsonSerializer.Serialize(new
    { auth_mode = "chatgpt", tokens = new { access_token = "OFFICIAL-HTTP-CANARY", account_id = "official-http" } }));
    Process Start()
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(Vault).Assembly.Location); info.ArgumentList.Add("serve");
        info.Environment["CODEX_POOL_DATA_DIR"] = Path.Combine(root, "http-api");
        info.Environment["CODEX_HOME"] = officialHome;
        var process = Process.Start(info)!; process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
    }
    using var server = Start();
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        var ready = false;
        for (var i = 0; i < 50; i++) { try { using var health = await http.GetAsync("http://127.0.0.1:3210/health"); if (health.IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { } await Task.Delay(100); }
        Check(ready && !server.HasExited);
        using var unauthorized = await http.GetAsync("http://127.0.0.1:3210/status"); Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized);
        using var official = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        official.DefaultRequestHeaders.Authorization = new("Bearer", "OFFICIAL-HTTP-CANARY");
        using var officialModel = await official.PostAsJsonAsync("http://127.0.0.1:3210/v1/responses", new { });
        Check(officialModel.StatusCode == HttpStatusCode.BadRequest);
        using var officialWs = await official.GetAsync("http://127.0.0.1:3210/v1/responses");
        Check(officialWs.StatusCode == HttpStatusCode.UpgradeRequired);
        using var officialControl = await official.PostAsJsonAsync("http://127.0.0.1:3210/settings", new { });
        Check(officialControl.StatusCode == HttpStatusCode.Unauthorized);
        using var officialStatus = await official.GetAsync("http://127.0.0.1:3210/status");
        Check(officialStatus.StatusCode == HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new("Bearer", vault.LocalKey);
        using var status = await http.GetAsync("http://127.0.0.1:3210/status"); var text = await status.Content.ReadAsStringAsync();
        Check(status.IsSuccessStatusCode && !text.Contains(vault.LocalKey) && !text.Contains("accessToken"));
        using var ticketResponse = await http.PostAsync("http://127.0.0.1:3210/dashboard/session", null);
        using var ticketJson = JsonDocument.Parse(await ticketResponse.Content.ReadAsStringAsync());
        var ticketUrl = ticketJson.RootElement.GetProperty("url").GetString()!;
        using var browser = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = true });
        using var redeemed = await browser.GetAsync(ticketUrl); Check(redeemed.StatusCode == HttpStatusCode.Redirect);
        using var replay = await browser.GetAsync(ticketUrl); Check(replay.StatusCode == HttpStatusCode.Unauthorized);
        using var browserStatus = await browser.GetAsync("http://127.0.0.1:3210/status"); Check(browserStatus.IsSuccessStatusCode);
        using var noCsrf = await browser.PostAsJsonAsync("http://127.0.0.1:3210/settings", new { autoSwitch = false, fiveHourThreshold = 90, weeklyThreshold = 90 });
        Check(noCsrf.StatusCode == HttpStatusCode.Forbidden);
        using var sessionResponse = await browser.GetAsync("http://127.0.0.1:3210/browser-session");
        using var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        browser.DefaultRequestHeaders.Add("X-Codex-Pool-CSRF", sessionJson.RootElement.GetProperty("csrf").GetString());
        using var settings = await browser.PostAsJsonAsync("http://127.0.0.1:3210/settings", new { autoSwitch = false, fiveHourThreshold = 90, weeklyThreshold = 90 });
        Check(settings.IsSuccessStatusCode);
        using var gated = await browser.PostAsJsonAsync("http://127.0.0.1:3210/settings", new { autoSwitch = true, fiveHourThreshold = 90, weeklyThreshold = 90 });
        Check(gated.StatusCode == HttpStatusCode.ServiceUnavailable);
        using var browserModel = await browser.PostAsJsonAsync("http://127.0.0.1:3210/v1/responses", new { }); Check(browserModel.StatusCode == HttpStatusCode.Unauthorized);
        using var asset = await browser.GetAsync("http://127.0.0.1:3210/"); Check(asset.IsSuccessStatusCode && (await asset.Content.ReadAsStringAsync()).Contains("Codex Pool"));
        using var eventRequest = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:3210/events");
        using var eventResponse = await browser.SendAsync(eventRequest, HttpCompletionOption.ResponseHeadersRead);
        Check(eventResponse.IsSuccessStatusCode && eventResponse.Content.Headers.ContentType?.MediaType == "text/event-stream");
        using var eventReader = new StreamReader(await eventResponse.Content.ReadAsStreamAsync());
        Check(await eventReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) == "event: status");
        using var originRequest = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:3210/status"); originRequest.Headers.Add("Origin", "https://evil.invalid");
        using var origin = await http.SendAsync(originRequest); Check(origin.StatusCode == HttpStatusCode.Forbidden);
        using var hostRequest = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:3210/status"); hostRequest.Headers.Host = "localhost:3210";
        using var host = await http.SendAsync(hostRequest); Check(host.StatusCode == HttpStatusCode.Forbidden);
        using var noAccount = await http.PostAsJsonAsync("http://127.0.0.1:3210/v1/responses", new { model = "gpt-6-luna", input = Array.Empty<object>(), stream = true, store = false });
        Check(noAccount.StatusCode == HttpStatusCode.ServiceUnavailable);
        using var callback = await http.GetAsync("http://127.0.0.1:3210/auth/callback?state=invalid&code=CODE-CANARY");
        Check(callback.StatusCode == HttpStatusCode.BadRequest && !(await callback.Content.ReadAsStringAsync()).Contains("CODE-CANARY"));
        using var second = Start(); await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Check(second.ExitCode != 0);
    }
    finally { if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); } }
}
async Task AccountRename()
{
    var vault = Store("account-rename"); vault.Add(Account("A")); vault.Add(Account("B")); vault.Control("B", "pause");
    vault.SaveQuota(new("A", "native_codex_usage", "plus", DateTimeOffset.UtcNow,
        [new("5h", 18000, 25, 75, DateTimeOffset.UtcNow.AddHours(1))]));
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
    var pool = new PoolRuntime(vault, auth, new Quota(http, auth, vault), true);
    var selection = pool.Select();
    var beforeA = vault.Find("A")!; var beforeB = JsonSerializer.Serialize(vault.Find("B")); var quotas = JsonSerializer.Serialize(vault.Quotas);
    using (var reserved = pool.ReserveModelRequest())
    {
        Check(vault.Rename("A", "  主力 账户  "));
        Check(vault.Find("A")!.Name == "主力 账户");
        var expected = JsonSerializer.Deserialize<AccountCredential>(JsonSerializer.Serialize(beforeA))!; expected.Name = "主力 账户";
        Check(JsonSerializer.Serialize(vault.Find("A")) == JsonSerializer.Serialize(expected));
        Check(vault.ActiveId == "A" && pool.Select().Epoch == selection.Epoch);
    }
    Check(JsonSerializer.Serialize(vault.Find("B")) == beforeB && JsonSerializer.Serialize(vault.Quotas) == quotas);
    Check(Store("account-rename").Find("A")!.Name == "主力 账户");
    Check(!vault.Rename("missing", "不存在"));
    foreach (var invalid in new string?[] { null, "", "   ", new('x', 81), "A\nB", "A\t", "A\u007f", "A\u0085" })
    {
        try { vault.Rename("A", invalid); Check(false); } catch (ArgumentException) { }
        Check(vault.Find("A")!.Name == "主力 账户");
    }
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    issuer.Barrier = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
    var refresh = auth.ReadyAsync("A", default);
    try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check(vault.Rename("A", "刷新中改名")); }
    finally { release.TrySetResult(); }
    await refresh; issuer.Barrier = null;
    Check(vault.Find("A")!.Name == "刷新中改名" && JsonSerializer.Serialize(vault.Find("B")) == beforeB);
    var query = Query(new Uri(await auth.BeginAsync("旧的登录名称", "A", default)).Query); issuer.Nonce = query["nonce"];
    Check(await auth.CompleteAsync(query["state"], "synthetic-code", "client-A", null, default) == "A");
    Check(vault.Find("A")!.Name == "刷新中改名" && pool.Select().Epoch == selection.Epoch);
    Check(JsonSerializer.Serialize(vault.Quotas) == quotas && Store("account-rename").Find("A")!.Name == "刷新中改名");
}
Task AccountDelete()
{
    var vault = Store("account-delete");
    vault.Add(Account("A")); vault.Add(Account("B"));
    vault.SaveQuota(new("B", "native_codex_usage", "plus", DateTimeOffset.UtcNow,
        [new("5h", 18000, 10, 90, DateTimeOffset.UtcNow.AddHours(1))]));
    Check(!vault.Delete("missing", out _));
    Check(vault.Find("missing") is null);
    var beforeA = vault.Find("A")!.RefreshToken;
    Check(vault.Delete("B", out var stillActive) && stillActive == "A");
    Check(vault.Find("B") is null && vault.Quotas.All(x => x.AccountId != "B"));
    Check(vault.Find("A")!.RefreshToken == beforeA && vault.ActiveId == "A");
    Check(vault.Delete("A", out var cleared) && cleared is null);
    Check(vault.Find("A") is null && vault.ActiveId is null && vault.PublicAccounts.Length == 0);
    var reopened = Store("account-delete");
    Check(reopened.PublicAccounts.Length == 0 && reopened.ActiveId is null);
    // Active deleted with eligible standby auto-picked
    var vault2 = Store("account-delete-active");
    vault2.Add(Account("A")); vault2.Add(Account("B"));
    Check(vault2.ActiveId == "A");
    Check(vault2.Delete("A", out var promoted) && promoted == "B" && vault2.ActiveId == "B");
    Check(vault2.Find("A") is null && vault2.Find("B") is not null);
    return Task.CompletedTask;
}

static void DeleteRejected(Action delete, string code)
{
    try { delete(); throw new InvalidOperationException(); }
    catch (AccountDeletionRejected ex) { Check(ex.Code == code); }
}
Task DeleteModelReservations()
{
    var vault = Store("delete-model-reservation"); vault.Add(Account("A")); vault.Add(Account("B"));
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer);
    var quota = new Quota(http, new OAuth(http, vault), vault);
    var pool = new PoolRuntime(vault, new OAuth(http, vault), quota, true); pool.UpdateSettings(new(true));
    var reservation = pool.ReserveModelRequest();
    Check(reservation.CandidateIds.SequenceEqual(new[] { "A", "B" }));
    foreach (var id in reservation.CandidateIds) DeleteRejected(() => pool.Delete(id, out _), "account_in_flight");
    // A standby that has not sent a byte is still part of this request's protected lease set.
    pool.Rejected(new("A", new("https://fixture.invalid"), "synthetic"), "quota_rejected");
    pool.Complete(reservation.Selection, new("B", 200, 2, true, "stream_forwarded"));
    Check(pool.Active == "B");
    reservation.Dispose(); reservation.Dispose();
    Check(pool.Delete("A", out _) && pool.Active == "B");
    Check(pool.Delete("B", out var empty) && empty is null && pool.Active is null && vault.ActiveId is null);
    vault.Add(Account("C")); pool.UpdateSettings(new(false));
    Check(pool.Select().AccountId == "C");
    pool.Complete(reservation.Selection, new("A", 200, 2, true, "stream_forwarded"));
    Check(pool.Active == "C" && vault.ActiveId == "C");
    try { using var use = vault.AcquireUse("A"); Check(false); } catch (AuthFailure ex) { Check(ex.Code == "account_not_found"); }
    // ABA: identical account IDs return, and the old request reports a valid failover target.
    // Only the advanced epoch can stop that stale completion from replacing the new owner.
    vault.Add(Account("A")); vault.Add(Account("B")); pool.UpdateSettings(new(true)); pool.Activate("A");
    pool.Complete(reservation.Selection, new("B", 200, 2, true, "stream_forwarded"));
    Check(pool.Active == "A" && vault.ActiveId == "A");
    // Unwinding before a model send (e.g. auth failure/cancellation) releases every candidate.
    vault.Add(Account("D")); pool.UpdateSettings(new(true));
    try { using var pending = pool.ReserveModelRequest(); throw new OperationCanceledException(); } catch (OperationCanceledException) { }
    Check(pool.Delete("D", out _));
    return Task.CompletedTask;
}
Task DeleteMigrationGate()
{
    var vault = Store("delete-migration-gate"); vault.Add(Account("A")); vault.Add(Account("B"));
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
    var pool = new PoolRuntime(vault, auth, new Quota(http, auth, vault), false);
    using (var request = pool.ReserveModelRequest()) Check(request.Selection.AccountId == "A");
    DeleteRejected(() => pool.Delete("A", out _), "live_migration_gate_pending");
    Check(pool.Active == "A" && vault.ActiveId == "A" && vault.Find("A") is not null);
    try { pool.Activate("B"); Check(false); } catch (AuthFailure ex) { Check(ex.Code == "live_migration_gate_pending"); }
    Check(pool.Delete("B", out _)); // Removing standby does not cross an account boundary.
    Check(pool.Delete("A", out _) && pool.Active is null);
    vault.Add(Account("C")); Check(pool.Select().AccountId == "C");
    var excluded = Store("delete-excluded"); excluded.Add(Account("A")); excluded.Add(Account("B")); excluded.Control("B", "pause");
    DeleteRejected(() => excluded.Delete("A", out _), "activate_another_first");
    Check(excluded.Find("A") is not null && excluded.ActiveId == "A");
    return Task.CompletedTask;
}
async Task DeleteBackgroundOperations()
{
    var vault = Store("delete-background"); vault.Add(Account("A")); vault.Add(Account("B"));
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
    var quota = new Quota(http, auth, vault); var pool = new PoolRuntime(vault, auth, quota, true);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    issuer.Barrier = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
    var refresh = auth.ReadyAsync("A", default);
    try
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        DeleteRejected(() => pool.Delete("A", out _), "account_busy");
        Check(vault.Find("B")!.RefreshToken == "REFRESH-CANARY-B");
    }
    finally { release.TrySetResult(); }
    await refresh;
    issuer.Barrier = null;
    var nativeA = Account("N1"); nativeA.ProviderKind = "native"; nativeA.BackendAccountId = "backend-N1"; nativeA.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1); vault.Add(nativeA);
    var nativeB = Account("N2"); nativeB.ProviderKind = "native"; nativeB.BackendAccountId = "backend-N2"; nativeB.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1); vault.Add(nativeB);
    var quotaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var quotaRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var quotaHttp = new HttpClient(new AsyncHandler(async (_, token) =>
    {
        quotaEntered.TrySetResult(); await quotaRelease.Task.WaitAsync(token);
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"plan_type\":\"plus\",\"rate_limit\":{\"primary_window\":{\"limit_window_seconds\":18000,\"used_percent\":10,\"reset_at\":2000000000}}}") };
    }));
    var nativeQuota = new Quota(quotaHttp, auth, vault);
    var nativePool = new PoolRuntime(vault, auth, nativeQuota, true);
    var pendingQuota = nativeQuota.RefreshAsync("N1", default);
    try { await quotaEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)); DeleteRejected(() => nativePool.Delete("N1", out _), "account_busy"); }
    finally { quotaRelease.TrySetResult(); }
    await pendingQuota;
    Check(nativePool.Delete("N1", out _) && nativeQuota.Snapshot("N1") is null);
    await nativeQuota.RefreshAsync("N2", default); Check(nativeQuota.Snapshot("N2") is not null);
    using (var catalog = vault.AcquireUse("B"))
    {
        await auth.ReadyAsync("B", default);
        DeleteRejected(() => pool.Delete("B", out _), "account_busy");
    }
    var query = Query(new Uri(await auth.BeginAsync("A again", "A", default)).Query); issuer.Nonce = query["nonce"];
    Check(pool.Delete("A", out _));
    try { await auth.CompleteAsync(query["state"], "synthetic-code", "client-A", null, default); Check(false); }
    catch (AuthFailure ex) { Check(ex.Code == "account_not_found"); }
    Check(vault.Find("A") is null);
    // A callback already in flight also pins the old registration through the final write.
    var bQuery = Query(new Uri(await auth.BeginAsync("B again", "B", default)).Query); issuer.Nonce = bQuery["nonce"];
    var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    issuer.Barrier = async token => { callbackEntered.TrySetResult(); await callbackRelease.Task.WaitAsync(token); };
    var callback = auth.CompleteAsync(bQuery["state"], "synthetic-code", "client-B", null, default);
    try { await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)); DeleteRejected(() => pool.Delete("B", out _), "account_busy"); }
    finally { callbackRelease.TrySetResult(); }
    await callback;
    vault.Add(Account("C")); // Keep a compatible standby for deleting the active SIWC account.
    Check(pool.Delete("B", out _));
}

async Task CatalogClientVersion()
{
    Check(CodexClientVersion.FromQueryOrFallback("0.162.1", "0.157.1") == "0.162.1");
    Check(CodexClientVersion.FromQueryOrFallback(null, "0.162.1") == "0.162.1");
    Check(CodexClientVersion.FromQueryOrFallback(" ", CodexClientVersion.Fallback) == CodexClientVersion.Fallback);
    Check(CodexClientVersion.FromQueryOrFallback("latest", "0.162.1") == "0.162.1");
    Check(CodexClientVersion.FromQueryOrFallback("../x", "0.162.1") == "0.162.1");
    Check(CodexClientVersion.IsValid("0.162.1"));
    Check(!CodexClientVersion.IsValid("0.162"));
    Check(!CodexClientVersion.IsValid("v0.162.1"));
    // Upstream-shaped picker payload: visibility=list Sol must survive as a listable slug.
    using var catalog = JsonDocument.Parse("""{"models":[{"slug":"gpt-6.1-sol","display_name":"GPT-6.1 Sol","visibility":"list"},{"slug":"gpt-hidden","display_name":"Hidden","visibility":"hide"}]}""");
    var visible = catalog.RootElement.GetProperty("models").EnumerateArray()
        .Where(x => x.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "list")
        .Select(x => x.GetProperty("slug").GetString())
        .ToArray();
    Check(visible.Contains("gpt-6.1-sol") && !visible.Contains("gpt-hidden"));
}
static async Task Rejected<T>(Func<Task<T>> operation) { try { await operation(); throw new InvalidOperationException(); } catch (AuthFailure) { } }

sealed class FakeIssuer : HttpMessageHandler
{
    private readonly RSA signing = RSA.Create(2048);
    public string Nonce { get; set; } = "nonce";
    public string? ExpectedChallenge { get; set; }
    public bool GrantPlan { get; set; } = true;
    public bool RejectRefresh { get; set; }
    public bool OmitNativeRefresh { get; set; }
    public Func<CancellationToken, Task>? Barrier { get; set; }
    public int RefreshCalls { get; private set; }
    public int CodeCalls { get; private set; }
    public bool ValidPkce { get; private set; }
    public List<string> UsedRefreshTokens { get; } = [];
    public string Token(string audience, string subject, string? nonce, string issuer = "https://auth.openai.com", bool expired = false, bool forged = false, string? nativeAccountId = null)
    {
        using var other = RSA.Create(2048); var key = new RsaSecurityKey(forged ? other : signing) { KeyId = "fixture-key" };
        var claims = new Dictionary<string, object> { ["sub"] = subject }; if (nonce is not null) claims["nonce"] = nonce;
        if (nativeAccountId is not null) claims["https://api.openai.com/auth"] = new Dictionary<string, object> { ["chatgpt_account_id"] = nativeAccountId };
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = issuer, Audience = audience, Claims = claims,
            IssuedAt = now.AddHours(-2), NotBefore = now.AddHours(-2), Expires = expired ? now.AddMinutes(-10) : now.AddHours(1),
            SigningCredentials = new(key, SecurityAlgorithms.RsaSha256) });
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/.well-known/openid-configuration") return Json(new { issuer = "https://auth.openai.com", authorization_endpoint = "https://auth.openai.com/api/accounts/authorize", token_endpoint = "https://auth.openai.com/api/accounts/oauth/token", jwks_uri = "https://auth.openai.com/.well-known/jwks.json" });
        if (path == "/.well-known/jwks.json")
        {
            var key = signing.ExportParameters(false);
            return Json(new { keys = new[] { new { kty = "RSA", kid = "fixture-key", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } } });
        }
        if (path == "/oauth/token")
        {
            if (Barrier is not null) await Barrier(cancellationToken);
            if (request.Content!.Headers.ContentType?.MediaType != "application/json") throw new InvalidOperationException();
            using var native = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)); var nativeFields = native.RootElement;
            if (nativeFields.GetProperty("client_id").GetString() != NativeLogin.NativeClientId || nativeFields.TryGetProperty("resource", out _)) throw new InvalidOperationException();
            var old = nativeFields.GetProperty("refresh_token").GetString()!; UsedRefreshTokens.Add(old); RefreshCalls++;
            var id = old.Split('-').Last();
            return Json(new { access_token = Token("native-access", "sub-" + id, null),
                refresh_token = OmitNativeRefresh ? "" : "native-refresh-" + RefreshCalls + "-" + id,
                id_token = Token(NativeLogin.NativeClientId, "sub-" + id, null, nativeAccountId: "backend-" + id) });
        }
        var fields = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
        var clientId = fields["client_id"];
        CheckForm(request, fields);
        if (Barrier is not null) await Barrier(cancellationToken);
        if (fields["grant_type"] == "refresh_token")
        {
            RefreshCalls++; UsedRefreshTokens.Add(fields["refresh_token"]); await Task.Delay(20, cancellationToken);
            if (RejectRefresh) return Json(new { error = "invalid_grant" }, 400);
        }
        else
        {
            CodeCalls++; ValidPkce = ExpectedChallenge is null || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"]))) == ExpectedChallenge;
        }
        var sequence = RefreshCalls + CodeCalls;
        return Json(new { access_token = "fresh-access-" + sequence, refresh_token = "fresh-refresh-" + sequence,
            id_token = Token(clientId, "sub-" + clientId.Replace("client-", ""), fields["grant_type"] == "refresh_token" ? null : Nonce),
            expires_in = 3600, token_type = "Bearer", scope = GrantPlan ? "openid offline_access resource.invoke chatgpt.tokens.use.direct" : "openid" });
    }
    private static void CheckForm(HttpRequestMessage request, Dictionary<string, string> fields)
    {
        if (request.Content!.Headers.ContentType?.MediaType != "application/x-www-form-urlencoded" || fields["resource"] != "https://api.openai.com/v1" || fields.ContainsKey("client_secret")) throw new InvalidOperationException();
    }
    private static HttpResponseMessage Json(object value, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    protected override void Dispose(bool disposing) { if (disposing) signing.Dispose(); base.Dispose(disposing); }
}

sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
}
