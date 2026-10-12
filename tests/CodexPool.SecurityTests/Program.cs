
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
    ("Durable quota, scheduler and manual epoch survive control races", PoolRouting)
};
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
    Process Start()
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(Vault).Assembly.Location); info.ArgumentList.Add("serve");
        info.Environment["CODEX_POOL_DATA_DIR"] = Path.Combine(root, "http-api");
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
static async Task Rejected<T>(Func<Task<T>> operation) { try { await operation(); throw new InvalidOperationException(); } catch (AuthFailure) { } }

sealed class FakeIssuer : HttpMessageHandler
{
    private readonly RSA signing = RSA.Create(2048);
    public string Nonce { get; set; } = "nonce";
    public string? ExpectedChallenge { get; set; }
    public bool GrantPlan { get; set; } = true;
    public bool RejectRefresh { get; set; }
    public int RefreshCalls { get; private set; }
    public int CodeCalls { get; private set; }
    public bool ValidPkce { get; private set; }
    public List<string> UsedRefreshTokens { get; } = [];
    public string Token(string audience, string subject, string? nonce, string issuer = "https://auth.openai.com", bool expired = false, bool forged = false)
    {
        using var other = RSA.Create(2048); var key = new RsaSecurityKey(forged ? other : signing) { KeyId = "fixture-key" };
        var claims = new Dictionary<string, object> { ["sub"] = subject }; if (nonce is not null) claims["nonce"] = nonce;
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
        var fields = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));
        var clientId = fields["client_id"];
        CheckForm(request, fields);
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
