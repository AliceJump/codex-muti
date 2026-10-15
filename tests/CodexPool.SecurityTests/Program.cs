
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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".research", "security-" + Guid.NewGuid().ToString("N")));
var tests = new (string Name, Func<Task> Run)[]
{
    ("Experimental compaction holds one serving account without failover and respects explicit rejection", CompactionRouting),
    ("Compaction control API rejects guests, model bearer and browser cookies; conditional switch is idempotent", CompactionApi),
    ("Daemon shutdown accepts only local bearer and acknowledges before requesting host shutdown", DaemonShutdown),
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
    ("Pool errors describe all accounts and distinguish quota, policy, auth and mixed failures", PoolErrors),
    ("A failed refresh can use B, whole-pool rejection is terminal, ambiguous failures never switch", PreparedPoolErrors),
    ("Catalog client_version prefers Codex query and keeps visibility=list Sol slugs", CatalogClientVersion),
    ("Token statistics survive encrypted atomic persistence without touching credentials", UsagePersistence),
    ("Token statistics aggregate local days, accounts, models and unknown detail coverage", UsagePeriods),
    ("Concurrent token accounting is exact and failed writes retry without losing history", UsageConcurrency),
    ("Quota preserves arbitrary, absent, optional and scoped windows plus credit metadata", DynamicQuotaParser),
    ("Generic routing honors credits, explicit denial, budgets, model scope and stale snapshots", DynamicQuotaRouting),
    ("WHAM usage rewrite exposes pool allowed shape for Desktop chatgpt_base_url", WhamUsageRewrite),
    ("ChatGPT backend preserves Desktop account bootstrap and caller identity without leaking local credentials or retrying", ChatGptBackendTests.RunAsync),
    ("Caller cancellation cannot abandon an accepted refresh rotation", RefreshCancellation),
    ("Refresh timeouts distinguish discovery, exchange and identity validation", RefreshTimeoutStages),
    ("Malformed native rotations fail closed without reusing credentials", NativeMalformedRefresh),
    ("Quota batches isolate account IO and auth faults, recover, and preserve cancellation", QuotaBatchFailures),
    ("Quota selection survives persistence faults without changing the owner", QuotaSelectionFailure),
    ("Quota recovery requires a new successful query and preserves independent restrictions", QuotaRecovery),
    ("Quota queries started before rejection cannot clear a newer cooldown", QuotaRecoveryRace),
    ("Legacy cooldown recovery waits for fresh authorization and preserves actual quota denials", LegacyQuotaRecovery),
    ("Settings restoration preserves unified and legacy thresholds exactly", SettingsRestoration)
};
if (args.Length != 0)
{
    if (args.Length != 2 || args[0] is not ("--filter" or "--skip")) return 2;
    tests = tests.Where(x => x.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase) == (args[0] == "--filter")).ToArray();
    if (tests.Length == 0) return 2;
}
var failed = 0;
foreach (var (name, run) in tests)
{ try { await run(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + " (" + ex.GetType().Name + ")"); Console.WriteLine(ex.StackTrace); } }
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
        foreach (var path in new[] { "/status", "/statistics", "/settings", "/accounts/A/activate", "/accounts/A/rename", "/dashboard/session", "/v1/other" })
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
Task UsagePersistence()
{
    var directory = Path.Combine(root, "usage-persistence"); var vault = Store("usage-persistence"); vault.Add(Account("A"));
    var credentials = File.ReadAllBytes(Path.Combine(directory, "vault.dpapi"));
    var usage = new UsageStatistics(directory); usage.Record("A", "gpt-6-luna", new(120, 30, 150, 80, 20)); usage.Flush();
    var saved = File.ReadAllBytes(Path.Combine(directory, "usage.dpapi"));
    Check(!Encoding.UTF8.GetString(saved).Contains("gpt-6-luna") && credentials.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "vault.dpapi"))));
    var restored = new UsageStatistics(directory).View();
    Check(restored.Persistence == "ready" && restored.AllTime.Totals == new TokenTotals(1, 1, 120, 30, 150, 80, 20, 1, 1));
    Check(restored.AllTime.Accounts.Single().AccountId == "A" && restored.AllTime.Models.Single().Model == "gpt-6-luna");
    saved[^1] ^= 255; File.WriteAllBytes(Path.Combine(directory, "usage.dpapi"), saved);
    var damaged = new UsageStatistics(directory); Check(damaged.View().Persistence == "unavailable");
    damaged.Record("A", "gpt-6-luna", new(120, 30, 150, null, null)); damaged.Flush();
    Check(saved.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "usage.dpapi"))) && !Directory.EnumerateFiles(directory, "*.tmp").Any());
    return Task.CompletedTask;
}
Task UsagePeriods()
{
    var directory = Path.Combine(root, "usage-periods"); Directory.CreateDirectory(directory);
    var now = DateTimeOffset.Parse("2026-10-13T15:59:59Z");
    var zone = TimeZoneInfo.CreateCustomTimeZone("Asia/Shanghai-fixture", TimeSpan.FromHours(8), "fixture", "fixture");
    var stats = new UsageStatistics(directory, zone, () => now);
    stats.Record("A", "gpt-6-luna", new(120, 30, 150, 80, 20));
    now = now.AddSeconds(1); stats.Record("B", "gpt-6.1-sol", null); stats.Record("B", "gpt-6.1-sol", new(1, 2, 3, null, null));
    var view = stats.View();
    Check(view.Today.Totals.Requests == 2 && view.Today.Totals.UnreportedRequests == 1 && view.Today.Totals.TotalTokens == 3
        && view.Today.Totals.CachedReportedRequests == 0 && view.Today.Totals.ReasoningReportedRequests == 0);
    Check(view.Last7Days.Totals.TotalTokens == 153 && view.AllTime.Accounts.Length == 2 && view.AllTime.Models.Length == 2);
    Check(view.Daily[^1].Day == new DateOnly(2026, 10, 14) && view.Daily[^2].Totals.TotalTokens == 150);
    now = now.AddDays(95); stats.Record("A", "gpt-6-luna", new(0, 0, 0, 0, 0));
    view = stats.View(); Check(view.Last7Days.Totals.Requests == 1 && view.AllTime.Totals.Requests == 4);
    stats.Flush();
    var restored = new UsageStatistics(directory, zone, () => now).View();
    Check(restored.AllTime.Totals == view.AllTime.Totals && restored.Daily.Sum(x => x.Totals.Requests) == 1);
    return Task.CompletedTask;
}
Task UsageConcurrency()
{
    var directory = Path.Combine(root, "usage-concurrency"); Directory.CreateDirectory(directory);
    var stats = new UsageStatistics(directory);
    Parallel.For(0, 1000, i => stats.Record(i % 2 == 0 ? "A" : "B", "gpt-6-luna", i % 4 == 0 ? null : new(120, 30, 150, 80, 20)));
    var totals = stats.View().AllTime.Totals;
    Check(totals.Requests == 1000 && totals.ReportedRequests == 750 && totals.TotalTokens == 112500 && totals.CachedInputTokens == 60000);
    // A failed atomic replacement retains pending memory and cleans the temporary file; the next flush can recover.
    Directory.CreateDirectory(Path.Combine(directory, "usage.dpapi")); stats.Flush();
    Check(stats.View().Persistence == "pending" && !Directory.EnumerateFiles(directory, "*.tmp").Any());
    Directory.Delete(Path.Combine(directory, "usage.dpapi")); stats.Flush();
    Check(stats.View().Persistence == "ready" && new UsageStatistics(directory).View().AllTime.Totals == totals);
    return Task.CompletedTask;
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
async Task RefreshCancellation()
{
    var vault = Store("refresh-caller-cancellation"); vault.Add(Account("A"));
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    issuer.Barrier = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
    using var caller = new CancellationTokenSource();
    var refreshing = auth.ReadyAsync("A", caller.Token, forceRefresh: true);
    try
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); caller.Cancel();
        using var waiter = new CancellationTokenSource(); waiter.Cancel();
        try { await auth.ReadyAsync("A", waiter.Token); Check(false); } catch (OperationCanceledException) { }
    }
    finally { release.TrySetResult(); }
    try { await refreshing; Check(false); } catch (OperationCanceledException) { }
    Check(issuer.RefreshCalls == 1 && vault.Find("A")!.AuthStatus == "ready" && auth.Health("A") is null);
    Check(vault.Find("A")!.RefreshToken != "REFRESH-CANARY-A");
    await auth.ReadyAsync("A", default); Check(issuer.RefreshCalls == 1);
}
async Task RefreshTimeoutStages()
{
    foreach (var stage in new[] { "discovery", "exchange", "validation" })
    {
        var vault = Store("refresh-timeout-" + stage); vault.Add(Account("A"));
        using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
        var started = 0;
        Task TimeoutAtStage(CancellationToken _) { started++; throw new OperationCanceledException(); }
        if (stage == "discovery") issuer.DiscoveryBarrier = TimeoutAtStage;
        if (stage == "exchange") issuer.Barrier = TimeoutAtStage;
        if (stage == "validation") issuer.JwksBarrier = TimeoutAtStage;
        var expected = stage == "discovery" ? "auth_temporarily_unavailable" : stage == "exchange" ? "refresh_outcome_unknown" : "refresh_validation_timeout";
        try { await auth.ReadyAsync("A", default, forceRefresh: true); Check(false); }
        catch (AuthFailure ex) { Check(ex.Code == expected); }
        Check(started == 1 && vault.Find("A")!.RefreshToken == "REFRESH-CANARY-A");
        Check(vault.Find("A")!.AuthStatus == (stage == "discovery" ? "ready" : "reauth_required"));
        Check(issuer.RefreshCalls == (stage == "validation" ? 1 : 0));
        await Rejected(() => auth.ReadyAsync("A", default)); Check(started == 1);
    }
}
async Task NativeMalformedRefresh()
{
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer);
    var access = issuer.Token("native-access", "sub-A", null);
    var cases = new[]
    {
        "{", "[]", "{\"access_token\":null}", "{\"access_token\":\"bad-jwt\"}",
        JsonSerializer.Serialize(new { access_token = access, id_token = "bad-jwt" }),
        JsonSerializer.Serialize(new { access_token = access, id_token = issuer.Token(NativeLogin.NativeClientId, "sub-A", null,
            nativeMetadata: new Dictionary<string, object> { ["other"] = "value" }) }),
        JsonSerializer.Serialize(new { access_token = access, id_token = issuer.Token(NativeLogin.NativeClientId, "sub-A", null,
            nativeMetadata: new Dictionary<string, object> { ["chatgpt_account_id"] = 7 }) })
    };
    for (var i = 0; i < cases.Length; i++)
    {
        var vault = Store("malformed-native-" + i); var template = Account("A");
        var account = new AccountCredential { Id = "A", Name = "A", Subject = template.Subject, ClientId = NativeLogin.NativeClientId,
            ProviderKind = "native", BackendAccountId = "backend-A", AccessToken = template.AccessToken, RefreshToken = template.RefreshToken,
            IdToken = template.IdToken, Scope = "native_codex", ExpiresAt = template.ExpiresAt };
        vault.Add(account);
        issuer.NativeResponseBody = cases[i]; var before = issuer.RefreshCalls;
        var auth = new OAuth(http, vault);
        try { await auth.ReadyAsync("A", default, forceRefresh: true); Check(false); }
        catch (AuthFailure ex) { Check(ex.Code == "invalid_token_response"); }
        Check(vault.Find("A")!.AuthStatus == "reauth_required" && vault.Find("A")!.RefreshToken == account.RefreshToken);
        await Rejected(() => auth.ReadyAsync("A", default)); Check(issuer.RefreshCalls == before + 1);
    }
}
async Task QuotaBatchFailures()
{
    Vault NativeVault(string name)
    {
        var created = Store(name);
        foreach (var id in new[] { "A", "B" }) { var account = Account(id); account.ProviderKind = "native"; account.BackendAccountId = "backend-" + id; account.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1); created.Add(account); }
        return created;
    }
    using var issuer = new FakeIssuer(); using var authHttp = new HttpClient(issuer);
    var calls = new List<string>(); Exception? failure = new IOException("IO-CANARY");
    using var quotaHttp = new HttpClient(new AsyncHandler((request, _) =>
    {
        var id = request.Headers.GetValues("ChatGPT-Account-Id").Single(); calls.Add(id);
        if (id == "backend-A" && failure is not null) throw failure;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"plan_type\":\"plus\",\"rate_limit\":{\"allowed\":true}}") });
    }));
    var sequence = 0;
    foreach (var error in new Exception[] { new IOException("IO-CANARY"), new UnauthorizedAccessException("IO-CANARY"), new CryptographicException("IO-CANARY"),
        new HttpRequestException("NETWORK-CANARY"), new JsonException("JSON-CANARY"), new AuthFailure("synthetic_auth_failure"), new OperationCanceledException() })
    {
        var fixture = NativeVault("quota-batch-" + sequence++); var fixtureAuth = new OAuth(authHttp, fixture);
        failure = error; calls.Clear(); var quota = new Quota(quotaHttp, fixtureAuth, fixture);
        await quota.RefreshAllAsync(["A", "B"], default);
        Check(quota.RefreshErrors.Single().AccountId == "A" && !JsonSerializer.Serialize(quota.RefreshErrors).Contains("CANARY"));
        Check(quota.Snapshot("B") is not null && calls.SequenceEqual(new[] { "backend-A", "backend-B" }));
        failure = null; await quota.RefreshAllAsync(["A", "B"], default);
        Check(quota.RefreshErrors.Length == 0 && quota.Snapshot("A") is not null);
    }
    var vault = NativeVault("quota-batch-cancel"); var auth = new OAuth(authHttp, vault);
    var cancelled = new Quota(quotaHttp, auth, vault); using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); calls.Clear();
    try { await cancelled.RefreshAllAsync(["A", "B"], cancellation.Token); Check(false); } catch (OperationCanceledException) { }
    Check(calls.Count == 0 && cancelled.RefreshErrors.Length == 0);
    var blockingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var blockingHttp = new HttpClient(new AsyncHandler(async (_, token) => { blockingStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }));
    var blocking = new Quota(blockingHttp, auth, vault); using var midway = new CancellationTokenSource();
    var polling = blocking.RefreshAllAsync(["A", "B"], midway.Token);
    await blockingStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); midway.Cancel();
    try { await polling; Check(false); } catch (OperationCanceledException) { }
    Check(blocking.RefreshErrors.Length == 0);
}
async Task QuotaRecovery()
{
    var vault = Store("quota-recovery"); var account = Account("A"); account.ProviderKind = "native";
    account.BackendAccountId = "backend-A"; account.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1); vault.Add(account);
    using var issuer = new FakeIssuer(); using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
    var reset = DateTimeOffset.UtcNow.AddHours(3); var calls = 0; var unavailable = false;
    var payload = JsonSerializer.Serialize(new { rate_limit = new { allowed = true, limit_reached = false,
        primary_window = new { used_percent = 0, limit_window_seconds = 18000, reset_at = reset.ToUnixTimeSeconds() } } });
    using var quotaHttp = new HttpClient(new AsyncHandler((_, _) =>
    {
        calls++;
        return Task.FromResult(new HttpResponseMessage(unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(payload) });
    }));
    var quota = new Quota(quotaHttp, auth, vault); var pool = new PoolRuntime(vault, auth, quota, true);
    var rejection = ModelError.Quota("plus", reset.ToUnixTimeSeconds()) with { FailoverReason = "quota_rejected" };
    await quota.RefreshAllAsync(["A"], default);
    pool.RecordFailure("A", rejection);
    Check(pool.Availability()[0].Reason == "quota_exhausted" && vault.Find("A")!.CooldownUntil > DateTimeOffset.UtcNow);
    await quota.RefreshAllAsync(["A"], default); pool.SelectAfterQuotaRefresh();
    Check(calls == 1 && pool.Availability()[0].Reason == "quota_exhausted"); // Cached 100% is not recovery.
    unavailable = true;
    await quota.RefreshAllAsync(["A"], default, force: true); pool.SelectAfterQuotaRefresh();
    Check(calls == 2 && pool.Availability()[0].Reason == "quota_exhausted");
    unavailable = false;
    await quota.RefreshAllAsync(["A"], default, force: true); pool.SelectAfterQuotaRefresh();
    Check(calls == 3 && pool.Availability()[0].Available && vault.Find("A")!.CooldownUntil is null);
    Check(!pool.Settings.AutoSwitch && pool.Active == "A"); // Recovery also works with automatic switching disabled.
    var restored = Store("quota-recovery");
    Check(restored.Find("A")!.CooldownUntil is null && restored.Quotas.Single().RequestedAt is not null);

    pool.RecordFailure("A", rejection);
    await quota.RefreshAllAsync(["A"], default, force: true);
    using (var locked = new FileStream(Path.Combine(root, "quota-recovery", "vault.dpapi"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        pool.SelectAfterQuotaRefresh();
        Check(pool.QuotaSelectionError == "quota_selection_persistence_failed" && pool.Availability()[0].Reason == "quota_exhausted");
    }
    pool.SelectAfterQuotaRefresh();
    Check(pool.QuotaSelectionError is null && pool.Availability()[0].Available && vault.Find("A")!.CooldownUntil is null);

    // Rejection metadata survives restart; reconciliation never changes the manual pause.
    pool.RecordFailure("A", rejection); vault.Control("A", "pause");
    restored = Store("quota-recovery"); var restoredAuth = new OAuth(authHttp, restored);
    var restoredQuota = new Quota(quotaHttp, restoredAuth, restored); var restoredPool = new PoolRuntime(restored, restoredAuth, restoredQuota, true);
    await restoredQuota.RefreshAllAsync(["A"], default, force: true); restoredPool.SelectAfterQuotaRefresh();
    Check(restored.Find("A")!.CooldownUntil is null && restoredPool.Availability()[0].Reason == "paused");
    vault.Control("A", "resume");

    foreach (var error in new[]
    {
        ModelError.Terminal("account_auth_required", "fixture") with { FailoverReason = "auth_rejected" },
        new ModelError(429, "insufficient_quota", "credit_balance_exhausted", "fixture", "quota_rejected"),
        new ModelError(429, "usage_not_included", "usage_not_included", "fixture", "access_rejected")
    })
    {
        pool.RecordFailure("A", error);
        var before = vault.Find("A")!.CooldownUntil;
        await quota.RefreshAllAsync(["A"], default, force: true); pool.SelectAfterQuotaRefresh();
        Check(!pool.Availability()[0].Available && vault.Find("A")!.CooldownUntil == before);
    }
    // Unknown/legacy cooldowns are not treated as a quota rejection without evidence.
    pool.RecordSuccess("A"); vault.Cooldown("A", reset);
    await quota.RefreshAllAsync(["A"], default, force: true); pool.SelectAfterQuotaRefresh();
    Check(pool.Availability()[0].Reason == "cooldown" && vault.Find("A")!.CooldownUntil == reset);

    var now = DateTimeOffset.UtcNow; var since = now.AddSeconds(-2);
    var healthy = new QuotaSnapshot("A", "fixture", "plus", now,
        [new("5h", 18000, 0, 100, reset)], Allowed: true, WindowState: "reported", RequestedAt: since.AddSeconds(1));
    Check(healthy.ConfirmsRecovery(since, now));
    foreach (var invalid in new[]
    {
        healthy with { RequestedAt = null }, healthy with { RequestedAt = since },
        healthy with { UpdatedAt = now.AddMinutes(-3) }, healthy with { Allowed = null },
        healthy with { Allowed = false }, healthy with { LimitReached = true },
        healthy with { SpendControlReached = true }, healthy with { WindowState = "unknown" },
        healthy with { Windows = [new("weekly", 604800, 100, 0, reset)] },
        healthy with { Windows = [new("5h", 18000, 0, 100, now.AddSeconds(-1))] },
        healthy with { Windows = [new("5h", 18000, 100, 0, reset, "additional_0", "gpt-6-luna")] }
    }) Check(!invalid.ConfirmsRecovery(since, now));
}
async Task LegacyQuotaRecovery()
{
    var vault = Store("legacy-quota-recovery"); var reset = DateTimeOffset.UtcNow.AddDays(5);
    foreach (var id in new[] { "recovered", "exhausted", "auth", "new-failure", "paused", "cached" })
    {
        var account = Account(id); account.ProviderKind = "native"; account.BackendAccountId = id;
        account.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1); vault.Add(account); vault.Cooldown(id, reset);
    }
    vault.Control("paused", "pause");
    using var issuer = new FakeIssuer(); using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
    using var quotaHttp = new HttpClient(new AsyncHandler((request, _) =>
    {
        var denied = request.Headers.GetValues("ChatGPT-Account-Id").Single() == "exhausted";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
            rate_limit = new { allowed = !denied, limit_reached = denied,
                primary_window = new { used_percent = denied ? 100 : 0, limit_window_seconds = 18000, reset_at = reset.ToUnixTimeSeconds() } } })) });
    }));
    var quota = new Quota(quotaHttp, auth, vault); var pool = new PoolRuntime(vault, auth, quota, true);
    await quota.RefreshAllAsync(vault.Accounts.Select(x => x.Id), default, force: true);
    pool.SelectAfterQuotaRefresh();
    Check(vault.Accounts.All(x => x.CooldownUntil == reset));
    // A later model rejection and reauthorization state take precedence over legacy recovery.
    pool.RecordFailure("new-failure", new(429, "insufficient_quota", "credit_balance_exhausted", "fixture", "quota_rejected"));
    var failureUntil = vault.Find("new-failure")!.CooldownUntil;
    vault.RequireReauthorization("auth");
    await Task.Delay(TimeSpan.FromSeconds(31));
    pool.SelectAfterQuotaRefresh();
    Check(vault.Find("cached")!.CooldownUntil == reset); // A pre-boundary query still cannot recover it.
    await quota.RefreshAllAsync(["recovered", "exhausted", "new-failure", "paused"], default, force: true);
    pool.SelectAfterQuotaRefresh();
    Check(pool.Availability().Single(x => x.AccountId == "recovered").Available && vault.Find("recovered")!.CooldownUntil is null);
    Check(pool.Availability().Single(x => x.AccountId == "exhausted").Reason == "quota_exhausted" && vault.Find("exhausted")!.CooldownUntil == reset);
    Check(vault.Find("new-failure")!.CooldownUntil == failureUntil && pool.Availability().Single(x => x.AccountId == "new-failure").ErrorCode == "credit_balance_exhausted");
    Check(vault.Find("auth")!.CooldownUntil == reset && pool.Availability().Single(x => x.AccountId == "auth").Reason == "auth_unavailable");
    Check(vault.Find("paused")!.CooldownUntil is null && pool.Availability().Single(x => x.AccountId == "paused").Reason == "paused");
    Check(Store("legacy-quota-recovery").Find("recovered")!.CooldownUntil is null);
    // Compare-and-clear cannot erase a typed failure that replaced the legacy deadline.
    vault.Cooldown("cached", reset, "invalid_token", DateTimeOffset.UtcNow);
    vault.ClearLegacyCooldown("cached", reset);
    Check(vault.Find("cached")!.CooldownUntil == reset);
}
async Task QuotaRecoveryRace()
{
    var vault = Store("quota-recovery-race"); var account = Account("A"); account.ProviderKind = "native";
    account.BackendAccountId = "backend-A"; account.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1); vault.Add(account);
    using var issuer = new FakeIssuer(); using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var quotaHttp = new HttpClient(new AsyncHandler(async (_, token) =>
    {
        started.TrySetResult(); await release.Task.WaitAsync(token);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"rate_limit\":{\"allowed\":true}}") };
    }));
    var quota = new Quota(quotaHttp, auth, vault); var pool = new PoolRuntime(vault, auth, quota, true);
    var polling = quota.RefreshAllAsync(["A"], default, force: true);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
    pool.RecordFailure("A", ModelError.Quota() with { FailoverReason = "quota_rejected" });
    release.TrySetResult(); await polling; pool.SelectAfterQuotaRefresh();
    Check(pool.Availability()[0].Reason == "quota_exhausted" && vault.Find("A")!.CooldownUntil > DateTimeOffset.UtcNow);
    await quota.RefreshAllAsync(["A"], default, force: true); pool.SelectAfterQuotaRefresh();
    Check(pool.Availability()[0].Available && vault.Find("A")!.CooldownUntil is null);
}
Task QuotaSelectionFailure()
{
    var vault = Store("quota-selection-io"); vault.Add(Account("A")); vault.Add(Account("B")); vault.SaveSettings(new(true)); vault.Control("A", "pause");
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
    var pool = new PoolRuntime(vault, auth, new Quota(http, auth, vault), true);
    using (var locked = new FileStream(Path.Combine(root, "quota-selection-io", "vault.dpapi"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        pool.SelectAfterQuotaRefresh();
        Check(pool.QuotaSelectionError == "quota_selection_persistence_failed" && pool.Active == "A" && pool.History.Length == 0);
    }
    pool.SelectAfterQuotaRefresh(); Check(pool.QuotaSelectionError is null && pool.Active == "B" && vault.ActiveId == "B");
    return Task.CompletedTask;
}
Task SettingsRestoration()
{
    foreach (var original in new[] { new PoolSettings(true, 83, 91, 88), new PoolSettings(false, 81, 89), new PoolSettings(false, 80, 90, 87.5) })
    {
        var restored = LocalCommands.ParseSettings([original.AutoSwitch ? "on" : "off", original.FiveHourThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
            original.WeeklyThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture), original.WindowThreshold?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"]);
        Check(restored == original);
    }
    Check(LocalCommands.ParseSettings(["on", "90"]) == new PoolSettings(true, WindowThreshold: 90));
    Check(LocalCommands.ParseSettings(["off", "80", "90"]) == new PoolSettings(false, 80, 90));
    Check(LocalCommands.ParseSettings(["off", "80", "90", "NaN"]) is null);
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
    pool.RecordFailure("A", ModelError.Quota() with { FailoverReason = "quota_rejected" });
    pool.Complete(a, new("B", 200, 2, true, "stream_forwarded")); Check(vault.ActiveId == "B");
    var b = pool.Select(); Check(b.AccountId == "B");
    pool.RecordSuccess("A"); vault.Cooldown("A", DateTimeOffset.UtcNow.AddSeconds(-1)); pool.Activate("A");
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
Task CompactionRouting()
{
    var vault = Store("compaction-routing"); vault.Add(Account("A")); vault.Add(Account("B"));
    vault.SaveSettings(new(true)); var now = DateTimeOffset.UtcNow;
    vault.SaveQuota(new("A", "fixture", "plus", now, [new("5h", 18000, 92, 8, now.AddHours(1))]));
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
    var quota = new Quota(http, auth, vault);
    Check(!new PoolRuntime(vault, auth, quota, true).CompactionEnabled);
    Check(new PoolRuntime(vault, auth, quota, false, true).CompactionAdvice("model") is null);
    var pool = new PoolRuntime(vault, auth, quota, true, true);
    var advice = pool.CompactionAdvice("model")!; var hold = pool.BeginCompaction(advice);
    using (var request = pool.ReserveModelRequest("model"))
        Check(request.CandidateIds.SequenceEqual(["A"]) && !request.AutoSwitch && request.Selection.AccountId == "A");
    Check(pool.FinishCompaction(hold.Id, true) && pool.Active == "B");
    Check(!pool.FinishCompaction(hold.Id, true));
    pool.Activate("A"); hold = pool.BeginCompaction(pool.CompactionAdvice("model")!);
    pool.RecordFailure("A", ModelError.Quota() with { FailoverReason = "quota_rejected" });
    Check(pool.Select().AccountId == "B" && !pool.FinishCompaction(hold.Id, true));
    return Task.CompletedTask;
}
async Task DaemonShutdown()
{
    var builder = WebApplication.CreateBuilder([]); builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
    await using var app = builder.Build();
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    const string key = "LOCAL-SHUTDOWN-CANARY";
    DaemonControlEndpoints.Map(app, key, () => stopped.TrySetResult());
    await app.StartAsync();
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    using (var guest = await http.PostAsJsonAsync("/control/shutdown", new { })) Check(guest.StatusCode == HttpStatusCode.Unauthorized);
    http.DefaultRequestHeaders.Authorization = new("Bearer", "MODEL-ONLY-CANARY");
    using (var model = await http.PostAsJsonAsync("/control/shutdown", new { })) Check(model.StatusCode == HttpStatusCode.Unauthorized);
    http.DefaultRequestHeaders.Authorization = null; http.DefaultRequestHeaders.Add("Cookie", "pool_session=BROWSER-CANARY");
    using (var browser = await http.PostAsJsonAsync("/control/shutdown", new { })) Check(browser.StatusCode == HttpStatusCode.Unauthorized);
    Check(!stopped.Task.IsCompleted);
    http.DefaultRequestHeaders.Authorization = new("Bearer", key);
    using var response = await http.PostAsJsonAsync("/control/shutdown", new { });
    Check(response.StatusCode == HttpStatusCode.Accepted);
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Check(document.RootElement.GetProperty("state").GetString() == "stopping");
    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
    await app.StopAsync();
}
async Task CompactionApi()
{
    var vault = Store("compaction-api"); var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
    vault.Add(Account(a)); vault.Add(Account(b)); vault.SaveSettings(new(true)); var now = DateTimeOffset.UtcNow;
    vault.SaveQuota(new(a, "fixture", "plus", now, [new("5h", 18000, 92, 8, now.AddHours(1))]));
    using var issuer = new FakeIssuer(); using var upstream = new HttpClient(issuer); var auth = new OAuth(upstream, vault);
    var pool = new PoolRuntime(vault, auth, new Quota(upstream, auth, vault), true, true);
    var builder = WebApplication.CreateBuilder([]); builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
    await using var app = builder.Build(); CompactionEndpoints.Map(app, pool, vault.LocalKey); await app.StartAsync();
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    using (var guest = await http.GetAsync("/compaction/advice?model=model")) Check(guest.StatusCode == HttpStatusCode.Unauthorized);
    http.DefaultRequestHeaders.Authorization = new("Bearer", "model-only-canary");
    using (var model = await http.PostAsJsonAsync("/compaction/reserve", new { })) Check(model.StatusCode == HttpStatusCode.Unauthorized);
    http.DefaultRequestHeaders.Authorization = null; http.DefaultRequestHeaders.Add("Cookie", "pool_session=browser-canary");
    using (var browser = await http.GetAsync("/compaction/advice?model=model")) Check(browser.StatusCode == HttpStatusCode.Unauthorized);
    http.DefaultRequestHeaders.Authorization = new("Bearer", vault.LocalKey);
    using var adviceResponse = await http.GetAsync("/compaction/advice?model=model"); adviceResponse.EnsureSuccessStatusCode();
    var body = await adviceResponse.Content.ReadAsStringAsync(); Check(!body.Contains(vault.LocalKey));
    using var document = JsonDocument.Parse(body);
    var advice = document.RootElement.GetProperty("advice").Deserialize<CompactionAdvice>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    using (var invalid = await http.PostAsJsonAsync("/compaction/reserve", advice with { Model = "bad model" })) Check(invalid.StatusCode == HttpStatusCode.BadRequest);
    using var reserved = await http.PostAsJsonAsync("/compaction/reserve", advice); reserved.EnsureSuccessStatusCode();
    var hold = (await reserved.Content.ReadFromJsonAsync<CompactionHold>())!;
    using (var duplicate = await http.PostAsJsonAsync("/compaction/reserve", advice)) Check(duplicate.StatusCode == HttpStatusCode.Conflict);
    using (var invalid = await http.PostAsJsonAsync("/compaction/" + hold.Id + "/finish", new { outcome = "invented" })) Check(invalid.StatusCode == HttpStatusCode.BadRequest);
    using (var finished = await http.PostAsJsonAsync("/compaction/" + hold.Id + "/finish", new { outcome = "completed" })) finished.EnsureSuccessStatusCode();
    Check(pool.Active == b);
    using var late = await http.PostAsJsonAsync("/compaction/" + hold.Id + "/finish", new { outcome = "completed" });
    using var lateBody = JsonDocument.Parse(await late.Content.ReadAsStringAsync()); Check(!lateBody.RootElement.GetProperty("switched").GetBoolean());
    await app.StopAsync();
}
Task DynamicQuotaParser()
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(new { plan_type = "pro", rate_limit = new { allowed = true, limit_reached = false,
        primary_window = new { used_percent = 40, limit_window_seconds = 25200, reset_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
        secondary_window = new { used_percent = 60, limit_window_seconds = 32400, reset_at = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() } },
        credits = new { has_credits = true, unlimited = false, balance = "12.50" }, spend_control = new { reached = false },
        additional_rate_limits = new[] { new { metered_feature = "special", normal_model_slug = "gpt-6-luna", rate_limit = new {
            primary_window = new { used_percent = 100, limit_window_seconds = 3600, reset_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } } } } });
    var parsed = Quota.Parse("A", payload);
    Check(parsed.Windows.Count == 3 && parsed.Windows[0].WindowSeconds == 25200 && parsed.Windows[1].WindowSeconds == 32400
        && parsed.Windows[2].Model == "gpt-6-luna" && parsed.Windows[2].LimitId != "codex");
    Check(parsed.Credits == new QuotaCredits(true, false, 12.5m) && parsed.Allowed == true && parsed.SpendControlReached == false);
    Check(parsed.Applicable("gpt-6.1-sol").Count() == 2 && parsed.Applicable("gpt-6-luna").Count() == 3);
    var none = Quota.Parse("A", "{\"rate_limit\":{\"allowed\":true,\"primary_window\":null}}"u8.ToArray());
    Check(none.Windows.Count == 0 && none.WindowState == "not_reported");
    Check(Quota.Parse("A", "{}"u8.ToArray()).WindowState == "unknown");
    var third = Quota.Parse("A", "{\"rate_limit\":{\"monthly_window\":{\"used_percent\":20,\"limit_window_seconds\":2592000,\"reset_at\":253402300798}}}"u8.ToArray());
    Check(third.Windows.Single().WindowSeconds == 2592000 && third.Windows.Single().Kind == "monthly_window");
    var optional = Quota.Parse("A", "{\"rate_limit\":{\"primary_window\":{\"used_percent\":20}}}"u8.ToArray()).Windows.Single();
    Check(optional.WindowSeconds is null && optional.ResetAt is null && optional.RemainingPercent == 80);
    Check(Quota.Parse("A", "{\"rate_limit\":{\"allowed\":true,\"primary_window\":{\"used_percent\":-1}}}"u8.ToArray()).WindowState == "unknown");
    return Task.CompletedTask;
}
Task DynamicQuotaRouting()
{
    var vault = Store("dynamic-quota-routing"); vault.Add(Account("A")); vault.Add(Account("B"));
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
    vault.SaveSettings(new(true, WindowThreshold: 90)); var now = DateTimeOffset.UtcNow;
    PoolRuntime Pool(QuotaSnapshot snapshot)
    { vault.SaveQuota(snapshot); return new(vault, auth, new Quota(http, auth, vault), true); }
    var baseline = new QuotaSnapshot("A", "fixture", "pro", now, [new("primary_window", 25200, 95, 5, now.AddHours(1))]);
    Check(Pool(baseline).Select().AccountId == "B"); vault.ActivateForProbe("A");
    Check(Pool(baseline with { Windows = [new("primary_window", 32400, 100, 0, now.AddHours(1))], Allowed = true }).Select().AccountId == "A");
    Check(Pool(baseline with { Windows = [new("primary_window", 32400, 100, 0, now.AddHours(1))], Credits = new(true, false, 10) }).Availability()[0].Available);
    Check(Pool(baseline with { Windows = [], Allowed = false }).Availability()[0].Reason == "quota_exhausted");
    Check(Pool(baseline with { Windows = [], Credits = new(true, false, 10), SpendControlReached = true }).Availability()[0].ErrorCode == "insufficient_quota");
    Check(Pool(baseline with { Windows = [], Allowed = false, UpdatedAt = now.AddMinutes(-3) }).Availability()[0].Available);
    Check(Pool(baseline with { Windows = [new("primary_window", 25200, 100, 0, now.AddSeconds(-1))], Allowed = false, LimitReached = true }).Availability()[0].Available);
    var scoped = baseline with { Windows = [new("primary_window", 32400, 100, 0, now.AddHours(1), "additional_0", "gpt-6-luna")] };
    var pool = Pool(scoped); Check(pool.Availability()[0].Available && pool.Availability("gpt-6.1-sol")[0].Available);
    Check(pool.Availability("gpt-6-luna")[0].Reason == "quota_exhausted");
    using var reservation = pool.ReserveModelRequest("gpt-6-luna"); Check(reservation.Selection.AccountId == "B");
    Check(PoolAvailability.Aggregate([new("A", "quota_threshold")]).Code == "account_pool_threshold_reached");
    return Task.CompletedTask;
}
Task WhamUsageRewrite()
{
    var empty = WhamUsage.Response(false);
    using (var emptyJson = JsonDocument.Parse(JsonSerializer.Serialize(empty)))
    {
        var rate = emptyJson.RootElement.GetProperty("rate_limit");
        Check(emptyJson.RootElement.GetProperty("plan_type").GetString() == "unknown");
        Check(rate.GetProperty("allowed").GetBoolean() == false);
        Check(rate.GetProperty("limit_reached").GetBoolean() == true);
    }
    var usable = WhamUsage.Response(true, "acct-1", "plus");
    using (var usableJson = JsonDocument.Parse(JsonSerializer.Serialize(usable)))
    {
        var root = usableJson.RootElement;
        Check(root.GetProperty("account_id").GetString() == "acct-1");
        Check(root.GetProperty("plan_type").GetString() == "plus");
        Check(root.GetProperty("rate_limit").GetProperty("allowed").GetBoolean());
        Check(root.GetProperty("rate_limit").GetProperty("limit_reached").GetBoolean() == false);
    }
    var exhausted = WhamUsage.Response(false);
    using (var exhaustedJson = JsonDocument.Parse(JsonSerializer.Serialize(exhausted)))
        Check(exhaustedJson.RootElement.GetProperty("rate_limit").GetProperty("allowed").GetBoolean() == false);
    using (var invalidPlan = JsonDocument.Parse(JsonSerializer.Serialize(WhamUsage.Response(false, planType: "invalid plan"))))
        Check(invalidPlan.RootElement.GetProperty("plan_type").GetString() == "unknown");
    var vault = Store("wham-routing");
    using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer);
    var auth = new OAuth(http, vault);
    var pool = new PoolRuntime(vault, auth, new Quota(http, auth, vault), true);
    Check(!pool.CanRouteModelRequest());
    vault.Add(Account("A")); vault.Add(Account("B"));
    pool = new(vault, auth, new Quota(http, auth, vault), true);
    Check(pool.CanRouteModelRequest());
    Check(vault.Control("A", "pause"));
    Check(pool.Availability().Any(x => x.Available) && !pool.CanRouteModelRequest());
    pool.UpdateSettings(new(true));
    Check(pool.CanRouteModelRequest() && pool.Active == "A" && vault.ActiveId == "A" && pool.History.Length == 0);
    using (var reservation = pool.ReserveModelRequest()) Check(reservation.Selection.AccountId == "B");
    Check(vault.Control("B", "pause"));
    Check(!pool.CanRouteModelRequest());
    var mixed = Store("wham-provider-routing"); mixed.Add(Account("A"));
    var native = Account("N"); native.ProviderKind = "native"; native.BackendAccountId = "backend-N"; mixed.Add(native);
    mixed.SaveSettings(new(true)); Check(mixed.Control("A", "pause"));
    var mixedAuth = new OAuth(http, mixed);
    var mixedPool = new PoolRuntime(mixed, mixedAuth, new Quota(http, mixedAuth, mixed), true);
    Check(mixedPool.Availability().Any(x => x.Available) && !mixedPool.CanRouteModelRequest());
    Check(!new PoolRuntime(vault, auth, new Quota(http, auth, vault), false).CanRouteModelRequest());
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
        using var whamAnon = await http.GetAsync("http://127.0.0.1:3210/backend-api/wham/usage");
        using var whamDoc = JsonDocument.Parse(await whamAnon.Content.ReadAsStringAsync());
        Check(whamAnon.IsSuccessStatusCode && whamDoc.RootElement.GetProperty("rate_limit").TryGetProperty("allowed", out _));
        using var codexUsage = await http.GetAsync("http://127.0.0.1:3210/api/codex/usage");
        using var codexDoc = JsonDocument.Parse(await codexUsage.Content.ReadAsStringAsync());
        Check(codexUsage.IsSuccessStatusCode && codexDoc.RootElement.GetProperty("rate_limit").GetProperty("allowed").ValueKind is JsonValueKind.True or JsonValueKind.False);
        using var unauthorized = await http.GetAsync("http://127.0.0.1:3210/status"); Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized);
        using var unauthorizedStatistics = await http.GetAsync("http://127.0.0.1:3210/statistics"); Check(unauthorizedStatistics.StatusCode == HttpStatusCode.Unauthorized);
        using var unauthorizedCompaction = await http.GetAsync("http://127.0.0.1:3210/compaction/advice?model=model"); Check(unauthorizedCompaction.StatusCode == HttpStatusCode.Unauthorized);
        using var official = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        official.DefaultRequestHeaders.Authorization = new("Bearer", "OFFICIAL-HTTP-CANARY");
        using var officialModel = await official.PostAsJsonAsync("http://127.0.0.1:3210/v1/responses", new { });
        Check(officialModel.StatusCode == HttpStatusCode.BadRequest);
        using var officialWs = await official.GetAsync("http://127.0.0.1:3210/v1/responses");
        Check(officialWs.StatusCode == HttpStatusCode.UpgradeRequired);
        using var officialControl = await official.PostAsJsonAsync("http://127.0.0.1:3210/settings", new { });
        Check(officialControl.StatusCode == HttpStatusCode.Unauthorized);
        using var officialCompaction = await official.PostAsJsonAsync("http://127.0.0.1:3210/compaction/reserve", new { });
        Check(officialCompaction.StatusCode == HttpStatusCode.Unauthorized);
        using var officialStatus = await official.GetAsync("http://127.0.0.1:3210/status");
        Check(officialStatus.StatusCode == HttpStatusCode.Unauthorized);
        using var officialStatistics = await official.GetAsync("http://127.0.0.1:3210/statistics"); Check(officialStatistics.StatusCode == HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new("Bearer", vault.LocalKey);
        // Exercise the production backend dispatch without sending any credential upstream.
        using var backendControl = await http.GetAsync("http://127.0.0.1:3210/backend-api/wham/accounts/check");
        Check(backendControl.StatusCode == HttpStatusCode.Forbidden);
        using var backendOriginRequest = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:3210/backend-api/wham/accounts/check");
        backendOriginRequest.Headers.Add("Origin", "https://untrusted.invalid");
        using var backendOrigin = await http.SendAsync(backendOriginRequest); Check(backendOrigin.StatusCode == HttpStatusCode.Forbidden);
        using var backendHostRequest = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:3210/backend-api/wham/accounts/check");
        backendHostRequest.Headers.Host = "untrusted.invalid:3210";
        using var backendHost = await http.SendAsync(backendHostRequest); Check(backendHost.StatusCode == HttpStatusCode.Forbidden);
        using var status = await http.GetAsync("http://127.0.0.1:3210/status"); var text = await status.Content.ReadAsStringAsync();
        Check(status.IsSuccessStatusCode && !text.Contains(vault.LocalKey) && !text.Contains("accessToken"));
        using var statistics = await http.GetAsync("http://127.0.0.1:3210/statistics");
        using var statsJson = JsonDocument.Parse(await statistics.Content.ReadAsStringAsync());
        Check(statistics.IsSuccessStatusCode && statsJson.RootElement.GetProperty("allTime").GetProperty("totals").GetProperty("requests").GetInt64() == 0);
        using var ticketResponse = await http.PostAsync("http://127.0.0.1:3210/dashboard/session", null);
        using var ticketJson = JsonDocument.Parse(await ticketResponse.Content.ReadAsStringAsync());
        var ticketUrl = ticketJson.RootElement.GetProperty("url").GetString()!;
        using var browser = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = true });
        using var redeemed = await browser.GetAsync(ticketUrl); Check(redeemed.StatusCode == HttpStatusCode.Redirect);
        using var replay = await browser.GetAsync(ticketUrl); Check(replay.StatusCode == HttpStatusCode.Unauthorized);
        using var browserStatus = await browser.GetAsync("http://127.0.0.1:3210/status"); Check(browserStatus.IsSuccessStatusCode);
        using var browserCompaction = await browser.GetAsync("http://127.0.0.1:3210/compaction/advice?model=model"); Check(browserCompaction.StatusCode == HttpStatusCode.Unauthorized);
        using var browserStatistics = await browser.GetAsync("http://127.0.0.1:3210/statistics"); Check(browserStatistics.IsSuccessStatusCode);
        using var noCsrf = await browser.PostAsJsonAsync("http://127.0.0.1:3210/settings", new { autoSwitch = false, fiveHourThreshold = 90, weeklyThreshold = 90 });
        Check(noCsrf.StatusCode == HttpStatusCode.Forbidden);
        using var sessionResponse = await browser.GetAsync("http://127.0.0.1:3210/browser-session");
        using var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        browser.DefaultRequestHeaders.Add("X-Codex-Pool-CSRF", sessionJson.RootElement.GetProperty("csrf").GetString());
        using var settings = await browser.PostAsJsonAsync("http://127.0.0.1:3210/settings", new { autoSwitch = false, fiveHourThreshold = 90, weeklyThreshold = 90 });
        Check(settings.IsSuccessStatusCode);
        using var genericSettings = await browser.PostAsJsonAsync("http://127.0.0.1:3210/settings", new { autoSwitch = false, windowThreshold = 88 });
        using var genericSettingsJson = JsonDocument.Parse(await genericSettings.Content.ReadAsStringAsync());
        Check(genericSettings.IsSuccessStatusCode && genericSettingsJson.RootElement.GetProperty("windowThreshold").GetDouble() == 88);
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
        Check(noAccount.StatusCode == HttpStatusCode.BadRequest);
        using var callback = await http.GetAsync("http://127.0.0.1:3210/auth/callback?state=invalid&code=CODE-CANARY");
        Check(callback.StatusCode == HttpStatusCode.BadRequest && !(await callback.Content.ReadAsStringAsync()).Contains("CODE-CANARY"));
        using var second = Start(); await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Check(second.ExitCode != 0);
    }
    finally { if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); } }
}
Task PoolErrors()
{
    var reset = DateTimeOffset.UtcNow.AddHours(1);
    var quota = PoolAvailability.Aggregate([new("A", "quota_exhausted", ResetAt: reset, Plan: "plus", WindowMinutes: 300), new("B", "quota_exhausted", ResetAt: reset.AddHours(1))]);
    Check(quota.Status == 429 && quota.Type == "usage_limit_reached" && quota.ResetsAt == reset.ToUnixTimeSeconds());
    Check(PoolAvailability.Aggregate([new("A","quota_exhausted","credit_balance_exhausted"),new("B","quota_exhausted","project_spend_limit_exceeded")]).Type == "insufficient_quota");
    Check(PoolAvailability.Aggregate([new("A", "auth_unavailable"), new("B", "auth_unavailable")]).Code == "account_pool_auth_required");
    Check(PoolAvailability.Aggregate([new("A", "quota_exhausted"), new("B", "auth_unavailable")]).Code == "account_pool_unavailable");
    Check(PoolAvailability.Aggregate([new("A", "5h_threshold"), new("B", "weekly_threshold")]).Code == "account_pool_threshold_reached");
    Check(PoolAvailability.Aggregate([new("A", "paused"), new("B", "disabled")]).Code == "account_pool_disabled");
    Check(PoolAvailability.Aggregate([new("A", "quota_exhausted"), new("B", null)]).Code == "request_accounts_unavailable");
    Check(PoolAvailability.Aggregate([new("A", "auth_temporarily_unavailable"), new("B", "auth_temporarily_unavailable")]).RetryAfter == "30");
    Check(PoolAvailability.Auth("reauth_required").Status == 400 && PoolAvailability.Auth("auth_temporarily_unavailable").Status == 503);
    return Task.CompletedTask;
}
async Task PreparedPoolErrors()
{
    var vault = Store("prepared-pool-errors"); vault.Add(Account("A")); vault.Add(Account("B"));
    using var issuer = new FakeIssuer { RejectRefreshAOnly = true }; using var authHttp = new HttpClient(issuer);
    var auth = new OAuth(authHttp, vault); var quota = new Quota(authHttp, auth, vault); var pool = new PoolRuntime(vault, auth, quota, true); pool.UpdateSettings(new(true));
    using (var request = pool.ReserveModelRequest())
    {
        var leases = await pool.PrepareAsync(request, default);
        Check(leases.Length == 1 && leases[0].AccountId == "B" && vault.Find("A")!.AuthStatus == "reauth_required");
        pool.Complete(request.Selection, new("B", 200, 1, true, "stream_forwarded"));
        Check(pool.Active == "B");
    }
    Check(pool.Availability().Single(x => x.AccountId == "A").Reason == "auth_unavailable");
    var catalogVault = Store("catalog-auth-fallback"); catalogVault.Add(Account("A")); catalogVault.Add(Account("B"));
    var catalogAuth = new OAuth(authHttp, catalogVault); var catalogPool = new PoolRuntime(catalogVault,catalogAuth,new Quota(authHttp,catalogAuth,catalogVault),true);
    using (var catalog = catalogPool.ReserveCatalog())
    {
        var catalogLeases = await catalogPool.PrepareAsync(catalog,default);
        Check(catalogLeases.Length==1 && catalogLeases[0].AccountId=="B" && catalogPool.Active=="A" && catalogVault.ActiveId=="A");
        try{catalogPool.Delete("B",out _);Check(false);}catch(AccountDeletionRejected ex){Check(ex.Code=="account_busy");}
    }
    var errors = new[] { ModelErrors.FromUpstream(HttpStatusCode.TooManyRequests, "{\"error\":{\"type\":\"usage_limit_reached\"}}"u8.ToArray(), null),
        ModelErrors.FromUpstream(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"invalid_token\"}}"u8.ToArray(), null) };
    var mixture = Store("mixed-error-pool"); var a = Account("A"); a.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1); mixture.Add(a); var b = Account("B"); b.ExpiresAt = a.ExpiresAt; mixture.Add(b);
    var mixtureAuth = new OAuth(authHttp, mixture); var mixturePool = new PoolRuntime(mixture, mixtureAuth, new Quota(authHttp, mixtureAuth, mixture), true); mixturePool.UpdateSettings(new(true));
    for (var trial = 0; trial < 2; trial++)
    {
        // In trial 0 A fails but B succeeds; no error reaches downstream and sticky follows B.
        using var reserved = mixturePool.ReserveModelRequest(); var leases = await mixturePool.PrepareAsync(reserved, default);
        var count = 0;
        using var modelHttp = new HttpClient(new AsyncHandler((_, _) =>
        {
            var index = count++;
            return Task.FromResult(index == 1 && trial == 0 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.completed\"}\n\n", Encoding.UTF8, "text/event-stream") }
                : new HttpResponseMessage(index == 0 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized) { Content = new StringContent(index == 0 ? "{\"error\":{\"type\":\"usage_limit_reached\"}}" : "{\"error\":{\"code\":\"invalid_token\"}}") });
        }));
        var sink = new ProbeSink();
        var result = await new ResponsesRelay(modelHttp).ForwardAsync("{}"u8.ToArray(), "application/json", leases, new Dictionary<string,string>(), sink, default,
            classified: (lease, error) => mixturePool.RecordFailure(lease.AccountId, error), exhausted: error => mixturePool.Unavailable(error, reserved.CandidateIds.ToArray()));
        mixturePool.Complete(reserved.Selection, result);
        if (trial == 0)
        {
            Check(result.StatusCode == 200 && sink.Text.Contains("response.completed") && !sink.Text.Contains("usage_limit_reached") && count == 2 && mixturePool.Active == "B");
            mixturePool.RecordSuccess("A"); mixture.Cooldown("A", DateTimeOffset.UtcNow.AddSeconds(-1)); mixturePool.Activate("A");
        }
        else Check(result.StatusCode == 400 && sink.Text.Contains("account_pool_unavailable") && count == 2);
    }
    var allQuota = Store("all-quota-pool"); allQuota.Add(Account("A")); allQuota.Add(Account("B"));
    var allAuth = new OAuth(authHttp, allQuota); var whole = new PoolRuntime(allQuota, allAuth, new Quota(authHttp, allAuth, allQuota), true); whole.UpdateSettings(new(true));
    whole.RecordFailure("A", errors[0]); whole.RecordFailure("B", errors[0]);
    Check(whole.Unavailable().Status == 429 && whole.Unavailable().Type == "usage_limit_reached");
    try { whole.ReserveModelRequest(); Check(false); } catch (NoEligibleAccountException) { }
    var thresholdVault = Store("threshold-error-pool"); thresholdVault.Add(Account("A")); thresholdVault.Add(Account("B"));
    foreach (var id in new[] { "A", "B" }) thresholdVault.SaveQuota(new(id, "native_codex_usage", "plus", DateTimeOffset.UtcNow,[new("5h",18000,96,4,DateTimeOffset.UtcNow.AddHours(1))]));
    var thresholdAuth = new OAuth(authHttp, thresholdVault); var thresholds = new PoolRuntime(thresholdVault, thresholdAuth, new Quota(authHttp, thresholdAuth, thresholdVault), true); thresholds.UpdateSettings(new(true));
    Check(thresholds.Unavailable().Code == "account_pool_threshold_reached");
    thresholds.UpdateSettings(new(false)); Check(thresholds.Select().AccountId == "A");
    var unknown = Store("unknown-refresh-error"); unknown.Add(Account("A")); unknown.Add(Account("B"));
    using var uncertainIssuer = new FakeIssuer { Barrier = _ => throw new HttpRequestException("SYNTHETIC-NETWORK-CANARY") }; using var uncertainHttp = new HttpClient(uncertainIssuer);
    var uncertainAuth = new OAuth(uncertainHttp, unknown); var uncertainPool = new PoolRuntime(unknown, uncertainAuth, new Quota(uncertainHttp, uncertainAuth, unknown), true); uncertainPool.UpdateSettings(new(true));
    using var pending = uncertainPool.ReserveModelRequest();
    try { await uncertainPool.PrepareAsync(pending, default); Check(false); } catch (AuthFailure ex) { Check(ex.Code == "refresh_outcome_unknown"); }
    Check(uncertainIssuer.RefreshCalls == 0 && unknown.Find("B")!.RefreshToken == "REFRESH-CANARY-B");
    var standby = Store("unknown-standby-refresh"); var healthy = Account("A"); healthy.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1); standby.Add(healthy); standby.Add(Account("B"));
    var standbyAuth = new OAuth(uncertainHttp, standby); var standbyPool = new PoolRuntime(standby, standbyAuth, new Quota(uncertainHttp, standbyAuth, standby), true); standbyPool.UpdateSettings(new(true));
    using var protectedReady = standbyPool.ReserveModelRequest();
    var preserved = await standbyPool.PrepareAsync(protectedReady, default);
    Check(preserved.Length == 1 && preserved[0].AccountId == "A" && standbyPool.Active == "A" && standby.Find("B")!.AuthStatus == "reauth_required");
    var excluded = Store("preexcluded-account-summary"); excluded.Add(Account("A")); excluded.Add(Account("B")); excluded.RequireReauthorization("A");
    var excludedAuth = new OAuth(authHttp, excluded); var excludedPool = new PoolRuntime(excluded, excludedAuth, new Quota(authHttp, excludedAuth, excluded), true); excludedPool.UpdateSettings(new(true));
    using var onlyB = excludedPool.ReserveModelRequest(); Check(onlyB.CandidateIds.SequenceEqual(new[]{"B"}));
    excludedPool.RecordFailure("B", errors[0]);
    Check(excludedPool.Unavailable(errors[0], onlyB.CandidateIds.ToArray(), onlyB.AutoSwitch).Code == "account_pool_unavailable");
    var three = Store("three-account-serving");
    foreach (var id in new[]{"A","B","C"}) { var credential = Account(id); credential.ExpiresAt=DateTimeOffset.UtcNow.AddHours(1); three.Add(credential); }
    var threeAuth = new OAuth(authHttp, three); var threePool = new PoolRuntime(three, threeAuth, new Quota(authHttp, threeAuth, three), true); threePool.UpdateSettings(new(true));
    using var threeRequest = threePool.ReserveModelRequest(); var threeLeases = await threePool.PrepareAsync(threeRequest, default); var threeCount=0;
    using var threeHttp=new HttpClient(new AsyncHandler((_,_)=>Task.FromResult<HttpResponseMessage>(++threeCount switch {
        1 => new(HttpStatusCode.TooManyRequests){Content=new StringContent("{\"error\":{\"type\":\"usage_limit_reached\"}}")},
        2 => new(HttpStatusCode.Unauthorized){Content=new StringContent("{\"error\":{\"code\":\"invalid_token\"}}")},
        _ => new(HttpStatusCode.OK){Content=new StringContent("data: {\"type\":\"response.completed\"}\n\n",Encoding.UTF8,"text/event-stream")}
    })));
    var threeSink=new ProbeSink();
    var threeResult=await new ResponsesRelay(threeHttp).ForwardAsync("{}"u8.ToArray(),"application/json",threeLeases,new Dictionary<string,string>(),threeSink,default,
        classified:(lease,error)=>threePool.RecordFailure(lease.AccountId,error),exhausted:error=>threePool.Unavailable(error,threeRequest.CandidateIds.ToArray(),threeRequest.AutoSwitch));
    threePool.Complete(threeRequest.Selection,threeResult);
    Check(threeCount==3 && threeResult.StatusCode==200 && threePool.Active=="C" && threeSink.Text.Contains("response.completed") && !threeSink.Text.Contains("usage_limit_reached"));
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
    pool.RecordFailure("A", ModelError.Quota() with { FailoverReason = "quota_rejected" });
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
    public bool RejectRefreshAOnly { get; set; }
    public bool OmitNativeRefresh { get; set; }
    public string? NativeResponseBody { get; set; }
    public Func<CancellationToken, Task>? Barrier { get; set; }
    public Func<CancellationToken, Task>? DiscoveryBarrier { get; set; }
    public Func<CancellationToken, Task>? JwksBarrier { get; set; }
    public int RefreshCalls { get; private set; }
    public int CodeCalls { get; private set; }
    public bool ValidPkce { get; private set; }
    public List<string> UsedRefreshTokens { get; } = [];
    public string Token(string audience, string subject, string? nonce, string issuer = "https://auth.openai.com", bool expired = false, bool forged = false, string? nativeAccountId = null, object? nativeMetadata = null)
    {
        using var other = RSA.Create(2048); var key = new RsaSecurityKey(forged ? other : signing) { KeyId = "fixture-key" };
        var claims = new Dictionary<string, object> { ["sub"] = subject }; if (nonce is not null) claims["nonce"] = nonce;
        if (nativeAccountId is not null) claims["https://api.openai.com/auth"] = new Dictionary<string, object> { ["chatgpt_account_id"] = nativeAccountId };
        if (nativeMetadata is not null) claims["https://api.openai.com/auth"] = nativeMetadata;
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = issuer, Audience = audience, Claims = claims,
            IssuedAt = now.AddHours(-2), NotBefore = now.AddHours(-2), Expires = expired ? now.AddMinutes(-10) : now.AddHours(1),
            SigningCredentials = new(key, SecurityAlgorithms.RsaSha256) });
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/.well-known/openid-configuration")
        {
            if (DiscoveryBarrier is not null) await DiscoveryBarrier(cancellationToken);
            return Json(new { issuer = "https://auth.openai.com", authorization_endpoint = "https://auth.openai.com/api/accounts/authorize", token_endpoint = "https://auth.openai.com/api/accounts/oauth/token", jwks_uri = "https://auth.openai.com/.well-known/jwks.json" });
        }
        if (path == "/.well-known/jwks.json")
        {
            if (JwksBarrier is not null) await JwksBarrier(cancellationToken);
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
            if (NativeResponseBody is not null) return new(HttpStatusCode.OK) { Content = new StringContent(NativeResponseBody, Encoding.UTF8, "application/json") };
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
            if (RejectRefresh || RejectRefreshAOnly && fields["client_id"] == "client-A") return Json(new { error = "invalid_grant" }, 400);
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

sealed class ProbeSink : IResponseSink
{
    private readonly MemoryStream bytes = new();
    public string Text => Encoding.UTF8.GetString(bytes.ToArray());
    public void Start(int statusCode, string contentType) { }
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token) => bytes.WriteAsync(data, token);
}
