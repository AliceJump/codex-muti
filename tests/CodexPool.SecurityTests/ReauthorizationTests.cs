using System.Net;
using System.Text;
using System.Text.Json;
using CodexPool.Core;
using CodexPool.Daemon;

internal static class ReauthorizationTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException(); }
    private static AccountCredential Grant(string id, bool expired = false) => new()
    {
        Id = id, Name = id, Subject = "sub-" + id, ClientId = NativeLogin.NativeClientId,
        ProviderKind = "native", BackendAccountId = "backend-" + id, Plan = "plus",
        AccessToken = "SYNTHETIC-ACCESS-" + id, RefreshToken = "synthetic-refresh-" + id,
        IdToken = "SYNTHETIC-ID-" + id, Scope = "native_codex",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1), UpdatedAt = DateTimeOffset.UtcNow
    };
    private static Vault Store(string root, string name, bool expired = false)
    {
        var vault = new Vault(Path.Combine(root, name)); vault.Add(Grant("A", expired)); vault.Add(Grant("B")); return vault;
    }
    private static AccountCredential Returning()
    {
        var grant = Grant("A");
        return new() { Id = Guid.NewGuid().ToString("N"), Name = "other-name", Subject = grant.Subject, ClientId = grant.ClientId,
            ProviderKind = "native", BackendAccountId = grant.BackendAccountId, Plan = "pro", Scope = grant.Scope,
            AccessToken = "SYNTHETIC-NEW-ACCESS", RefreshToken = "synthetic-new-refresh-A", IdToken = "SYNTHETIC-NEW-ID",
            ExpiresAt = grant.ExpiresAt, UpdatedAt = DateTimeOffset.UtcNow };
    }
    private static ModelError AuthError => ModelErrors.FromUpstream(HttpStatusCode.Unauthorized,
        "{\"error\":{\"code\":\"invalid_token\"}}"u8.ToArray(), null);
    private static string Payload(string plan = "plus") => JsonSerializer.Serialize(new
    {
        plan_type = plan, rate_limit = new { allowed = true, limit_reached = false,
            primary_window = new { used_percent = 20, limit_window_seconds = 18000, reset_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }
    });
    public static async Task Recovery(string root)
    {
        var vault = Store(root, "reauthorization-live", expired: true);
        var other = JsonSerializer.Serialize(vault.Find("B")); var host = vault.HostId; var key = vault.LocalKey;
        var oldVersion = vault.Find("A")!.AuthorizationVersion;
        vault.SaveQuota(Quota.Parse("A", Encoding.UTF8.GetBytes(Payload())));
        using var issuer = new FakeIssuer { NativeResponseStatus = HttpStatusCode.Unauthorized, NativeResponseBody = "SYNTHETIC-ERROR-CANARY" };
        using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
        using var quotaHttp = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Payload()) })));
        var quota = new Quota(quotaHttp, auth, vault); var pool = new PoolRuntime(vault, auth, quota, true);
        try { await auth.ReadyAsync("A", default); Check(false); } catch (AuthFailure ex) { Check(ex.Code == "reauth_required"); }
        pool.RecordFailure("A", AuthError, oldVersion);
        try { await quota.RefreshAsync("A", default, force: true); Check(false); } catch (AuthFailure) { }
        Check(auth.Health("A") == "reauth_required" && !pool.Availability()[0].Available);
        Check(await auth.SaveValidatedLoginAsync(Returning(), default) == "A");
        Check(vault.Find("A")!.AuthStatus == "ready" && auth.Health("A") is null && pool.Availability()[0].Available);
        Check(vault.Find("A")!.CooldownUntil is null && vault.Find("A")!.AuthorizationVersion != oldVersion);
        Check(quota.Snapshot("A")!.CacheState == "historical" && quota.RefreshErrors.Single().Code == "quota_refresh_required");
        await quota.RefreshAsync("A", default, force: true);
        Check(quota.Snapshot("A")!.Fresh(DateTimeOffset.UtcNow) && quota.RefreshErrors.Length == 0);
        using var request = pool.ReserveModelRequest(); var leases = await pool.PrepareAsync(request, default);
        Check(leases[0].AccountId == "A" && leases[0].AccessToken == "SYNTHETIC-NEW-ACCESS" && issuer.RefreshCalls == 1);
        Check(vault.ActiveId == "A" && pool.History.Length == 0 && vault.Find("A")!.Name == "A");
        Check(JsonSerializer.Serialize(vault.Find("B")) == other && vault.HostId == host && vault.LocalKey == key);
        var restored = new Vault(Path.Combine(root, "reauthorization-live"));
        Check(restored.Find("A")!.AuthorizationVersion == vault.Find("A")!.AuthorizationVersion && restored.Find("A")!.AuthStatus == "ready");
    }
    public static async Task InFlightRefresh(string root)
    {
        var vault = Store(root, "reauthorization-inflight", expired: true);
        using var issuer = new FakeIssuer { NativeResponseStatus = HttpStatusCode.Unauthorized, NativeResponseBody = "SYNTHETIC-ERROR-CANARY" };
        using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        issuer.Barrier = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var rotating = auth.ReadyAsync("A", default);
        Task<string>? publishing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            publishing = auth.SaveValidatedLoginAsync(Returning(), default);
            Check(!publishing.IsCompleted && vault.Find("A")!.AuthStatus == "refresh_pending");
        }
        finally { release.TrySetResult(); }
        try { await rotating; Check(false); } catch (AuthFailure) { }
        Check(await publishing! == "A");
        Check((await auth.ReadyAsync("A", default)).AccessToken == "SYNTHETIC-NEW-ACCESS");
        Check(auth.Health("A") is null && vault.Find("A")!.AuthStatus == "ready" && issuer.RefreshCalls == 1);
    }
    public static async Task LateRejection(string root)
    {
        var vault = Store(root, "reauthorization-late-rejection");
        using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
        var pool = new PoolRuntime(vault, auth, new Quota(http, auth, vault), true);
        using var request = pool.ReserveModelRequest(); var oldLease = (await pool.PrepareAsync(request, default))[0];
        pool.RecordFailure("A", AuthError, oldLease.AuthorizationVersion);
        await auth.SaveValidatedLoginAsync(Returning(), default);
        pool.RecordFailure("A", AuthError, oldLease.AuthorizationVersion);
        Check(pool.Availability()[0].Available && vault.Find("A")!.CooldownUntil is null);
        Check(!vault.Cooldown("A", DateTimeOffset.UtcNow.AddMinutes(1), AuthError.Code, DateTimeOffset.UtcNow, oldLease.AuthorizationVersion));
        pool.RecordFailure("A", AuthError, vault.Find("A")!.AuthorizationVersion);
        Check(pool.Availability()[0].Reason == "auth_unavailable");
    }
    public static async Task PreserveRestrictions(string root)
    {
        foreach (var code in new[] { "usage_limit_reached", "credit_balance_exhausted", "usage_not_included" })
        {
            var vault = Store(root, "reauthorization-restrictions-" + code);
            using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer); var auth = new OAuth(http, vault);
            var quota = new Quota(http, auth, vault); var pool = new PoolRuntime(vault, auth, quota, true);
            var error = ModelErrors.FromUpstream(HttpStatusCode.TooManyRequests, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = new { code } })), null);
            pool.RecordFailure("A", error);
            var cooldown = vault.Find("A")!.CooldownUntil;
            foreach (var op in new[] { "pause", "drain", "disable" }) vault.Control("A", op);
            await auth.SaveValidatedLoginAsync(Returning(), default);
            var account = vault.Find("A")!;
            Check(account.Paused && account.Draining && !account.Enabled && account.CooldownUntil == cooldown);
            foreach (var op in new[] { "resume", "undrain", "enable" }) vault.Control("A", op);
            Check(!pool.Availability()[0].Available && pool.Availability()[0].ErrorCode == error.Code);
        }
    }
    public static async Task QuotaRace(string root)
    {
        var vault = Store(root, "reauthorization-quota-race");
        using var issuer = new FakeIssuer(); using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AsyncHandler(async (_, ct) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(ct); return new(HttpStatusCode.OK) { Content = new StringContent(Payload("free")) }; }));
        var quota = new Quota(http, auth, vault); var oldVersion = vault.Find("A")!.AuthorizationVersion;
        var polling = quota.RefreshAsync("A", default, force: true);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await auth.SaveValidatedLoginAsync(Returning(), default); }
        finally { release.TrySetResult(); }
        await polling;
        Check(vault.Find("A")!.Plan == "pro" && quota.Snapshot("A") is null && vault.Quotas.Length == 0);
        Check(!vault.SaveQuota(Quota.Parse("A", Encoding.UTF8.GetBytes(Payload("free"))), oldVersion));
    }
    public static async Task QuotaBackoff(string root)
    {
        var vault = Store(root, "reauthorization-quota-backoff");
        using var issuer = new FakeIssuer(); using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
        using var http = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        var quota = new Quota(http, auth, vault);
        var version = vault.Find("A")!.AuthorizationVersion;
        await quota.RefreshAsync("A", default, force: true);
        Check(issuer.RefreshCalls == 1 && vault.Find("A")!.AuthorizationVersion == version);
        await quota.RefreshAsync("A", default, force: true);
        Check(issuer.RefreshCalls == 1); // Same grant is still protected by recovery backoff.
        await auth.SaveValidatedLoginAsync(Returning(), default);
        Check(quota.RefreshErrors.Single().Code == "quota_refresh_required");
        await quota.RefreshAsync("A", default, force: true);
        Check(issuer.RefreshCalls == 2 && quota.RefreshErrors.Single().Code == "quota_unauthorized");
    }
}
