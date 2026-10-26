using System.Net;
using System.Text;
using System.Text.Json;
using CodexPool.Daemon;

internal static class RenewalQuotaTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException(); }
    private static Vault Store(string root, string name)
    {
        var vault = new Vault(Path.Combine(root, name));
        foreach (var id in new[] { "A", "B" }) vault.Add(new AccountCredential
        {
            Id = id, Name = id, Subject = "sub-" + id, ClientId = NativeLogin.NativeClientId,
            ProviderKind = "native", BackendAccountId = "backend-" + id, Plan = "free",
            AccessToken = "old-access-" + id, RefreshToken = "old-refresh-" + id, IdToken = "old-id-CANARY",
            Scope = "native_codex", ExpiresAt = DateTimeOffset.UtcNow.AddHours(12), UpdatedAt = DateTimeOffset.UtcNow
        });
        return vault;
    }
    private static string Payload(string plan = "plus", bool exhausted = false) => JsonSerializer.Serialize(new
    {
        plan_type = plan, rate_limit = new { allowed = !exhausted, limit_reached = exhausted,
            primary_window = new { used_percent = exhausted ? 100 : 20, limit_window_seconds = 18000, reset_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }
    });
    private static HttpResponseMessage Reply(HttpStatusCode status, string body = "ERROR-BODY-CANARY") => new(status) { Content = new StringContent(body) };
    public static async Task Recovery(string root)
    {
        var vault = Store(root, "renewal-recovery"); var beforeB = JsonSerializer.Serialize(vault.Find("B"));
        vault.SaveQuota(Quota.Parse("A", Encoding.UTF8.GetBytes(Payload("free"))));
        using var issuer = new FakeIssuer(); using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
        var calls = 0;
        using var http = new HttpClient(new AsyncHandler((r, _) =>
        {
            calls++;
            Check(r.Headers.GetValues("ChatGPT-Account-Id").Single() == "backend-A");
            return Task.FromResult(r.Headers.Authorization!.Parameter == "old-access-A" ? Reply(HttpStatusCode.Unauthorized) : Reply(HttpStatusCode.OK, Payload()));
        }));
        var quota = new Quota(http, auth, vault);
        await quota.RefreshAsync("A", default, force: true);
        Check(calls == 2 && issuer.RefreshCalls == 1 && quota.RefreshErrors.Length == 0);
        Check(vault.Find("A")!.Plan == "plus" && vault.Find("A")!.AuthStatus == "ready");
        Check(vault.Find("A")!.PlanSource == "native_codex_usage" && vault.Find("A")!.PlanUpdatedAt == quota.Snapshot("A")!.UpdatedAt);
        Check(quota.Snapshot("A")!.Fresh(DateTimeOffset.UtcNow) && quota.Snapshot("A")!.Windows.Single().WindowSeconds == 18000);
        Check(JsonSerializer.Serialize(vault.Find("B")) == beforeB);
        Check(new Vault(Path.Combine(root, "renewal-recovery")).Find("A")!.Plan == "plus");
    }
    public static async Task Failures(string root)
    {
        foreach (var (status, code) in new[] { (401, "quota_unauthorized"), (403, "quota_forbidden"), (429, "quota_rate_limited"), (503, "quota_upstream_unavailable"), (418, "quota_http_error"), (200, "invalid_quota_response"), (0, "quota_network_unavailable") })
        {
            var vault = Store(root, "renewal-failure-" + status);
            vault.SaveQuota(Quota.Parse("A", Encoding.UTF8.GetBytes(Payload("free", exhausted: true))));
            using var issuer = new FakeIssuer(); using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
            var calls = 0; var healthy = false;
            using var http = new HttpClient(new AsyncHandler((r, _) =>
            {
                calls++;
                if (r.Headers.GetValues("ChatGPT-Account-Id").Single() == "backend-B" || healthy) return Task.FromResult(Reply(HttpStatusCode.OK, Payload()));
                if (status == 0) throw new HttpRequestException("TRANSPORT-CANARY");
                return Task.FromResult(Reply((HttpStatusCode)status, status == 200 ? "[]" : "ERROR-BODY-CANARY"));
            }));
            var quota = new Quota(http, auth, vault); var pool = new PoolRuntime(vault, auth, quota, true);
            var old = quota.Snapshot("A")!;
            await quota.RefreshAllAsync(["A", "B"], default, force: true);
            var failure = quota.RefreshErrors.Single();
            Check(failure.AccountId == "A" && failure.Code == code && failure.Status == (status == 0 ? null : status));
            Check(issuer.RefreshCalls == (status == 401 ? 1 : 0) && calls == (status == 401 ? 3 : 2));
            Check(quota.Snapshot("A")!.UpdatedAt == old.UpdatedAt && quota.Snapshot("A")!.Windows.SequenceEqual(old.Windows));
            Check(quota.Snapshot("A")!.CacheState == "historical" && !quota.Snapshot("A")!.Fresh(DateTimeOffset.UtcNow));
            Check(quota.Snapshot("B")!.Fresh(DateTimeOffset.UtcNow) && pool.Availability().All(x => x.Available));
            pool.SelectAfterQuotaRefresh(); Check(pool.Active == "A");
            Check(vault.Accounts.All(x => x.AuthStatus == "ready" && x.Enabled));
            Check(!JsonSerializer.Serialize(quota.Views).Contains("CANARY"));
            if (status == 401)
            {
                await quota.RefreshAllAsync(["A"], default, force: true);
                Check(issuer.RefreshCalls == 1 && calls == 4); // Repeated rejection cannot rotate again within backoff.
            }
            healthy = true; await quota.RefreshAllAsync(["A"], default, force: true);
            Check(quota.RefreshErrors.Length == 0 && quota.Snapshot("A")!.Fresh(DateTimeOffset.UtcNow) && vault.Find("A")!.Plan == "plus");
        }
    }
    public static async Task Concurrency(string root)
    {
        var vault = Store(root, "renewal-concurrency");
        using var issuer = new FakeIssuer(); using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new AsyncHandler(async (r, ct) =>
        {
            Interlocked.Increment(ref calls);
            if (r.Headers.Authorization!.Parameter == "old-access-A")
            { entered.TrySetResult(); await release.Task.WaitAsync(ct); return Reply(HttpStatusCode.Unauthorized); }
            return Reply(HttpStatusCode.OK, Payload());
        }));
        var quota = new Quota(http, auth, vault);
        var first = quota.RefreshAsync("A", default, force: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var waiters = Enumerable.Range(0, 20).Select(_ => quota.RefreshAsync("A", default, force: true)).ToArray();
        // Simulate a model/control refresh while the old quota request is in flight.
        await auth.ReadyAsync("A", default, forceRefresh: true);
        var refreshed = vault.Find("A")!.AccessToken;
        release.TrySetResult(); await first; await Task.WhenAll(waiters);
        Check(calls == 2 && issuer.RefreshCalls == 1 && vault.Find("A")!.AccessToken == refreshed);
        Check(quota.RefreshErrors.Length == 0);
        // Concurrent recovery callers also share the generation under OAuth's lock.
        var rejected = vault.Find("A")!;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => auth.ReadyAsync("A", default, forceRefresh: true, rejectedCredential: rejected)));
        Check(issuer.RefreshCalls == 2);
    }
    public static async Task Metadata(string root)
    {
        var vault = Store(root, "renewal-metadata"); using var issuer = new FakeIssuer(); using var http = new HttpClient(issuer);
        var until = DateTimeOffset.UtcNow.AddDays(25);
        string IdToken(DateTimeOffset date, bool expired = false) => issuer.Token(NativeLogin.NativeClientId, "sub-A", null, expired: expired,
            nativeMetadata: new Dictionary<string, object> { ["chatgpt_account_id"] = "backend-A", ["chatgpt_subscription_active_until"] = date.ToUnixTimeSeconds() });
        Check(SubscriptionExpiry.Inspect(IdToken(until)).State == "current");
        Check(SubscriptionExpiry.Inspect(IdToken(until, expired: true)).ActiveUntil is null);
        Check(SubscriptionExpiry.Inspect(IdToken(until.AddMonths(-2))).State == "stale");
        vault.ReplaceTokens("A", "old-refresh-A", new() { AccessToken = "new-access", RefreshToken = "new-refresh", IdToken = IdToken(until), ExpiresIn = 3600 });
        issuer.NativeResponseBody = JsonSerializer.Serialize(new { access_token = issuer.Token("native-access", "sub-A", null) });
        await new OAuth(http, vault).ReadyAsync("A", default, forceRefresh: true);
        var account = vault.Find("A")!; var view = JsonSerializer.SerializeToElement(account.PublicView(true));
        Check(account.IdTokenMetadataStale && view.GetProperty("subscriptionActiveUntil").ValueKind == JsonValueKind.Null);
        Check(view.GetProperty("subscriptionState").GetString() == "stale");
        Check(new Vault(Path.Combine(root, "renewal-metadata")).Find("A")!.IdTokenMetadataStale);
        issuer.NativeResponseBody = JsonSerializer.Serialize(new { access_token = issuer.Token("native-access", "sub-A", null),
            id_token = issuer.Token(NativeLogin.NativeClientId, "sub-A", null, nativeAccountId: "backend-A") });
        await new OAuth(http, vault).ReadyAsync("A", default, forceRefresh: true);
        view = JsonSerializer.SerializeToElement(vault.Find("A")!.PublicView(true));
        Check(view.GetProperty("subscriptionState").GetString() == "not_reported" && view.GetProperty("subscriptionActiveUntil").ValueKind == JsonValueKind.Null);
        issuer.NativeResponseBody = JsonSerializer.Serialize(new { access_token = issuer.Token("native-access", "sub-A", null),
            id_token = issuer.Token(NativeLogin.NativeClientId, "sub-A", null, nativeMetadata: new Dictionary<string, object>
                { ["chatgpt_account_id"] = "backend-A", ["chatgpt_plan_type"] = "plus" }) });
        await new OAuth(http, vault).ReadyAsync("A", default, forceRefresh: true);
        Check(vault.Find("A")!.Plan == "plus" && vault.Find("A")!.PlanSource == "validated_id_token");
        view = JsonSerializer.SerializeToElement(vault.Find("A")!.PublicView(true));
        Check(view.GetProperty("planState").GetString() == "current" && view.GetProperty("subscriptionActiveUntil").ValueKind == JsonValueKind.Null);
        vault.SaveQuota(Quota.Parse("A", Encoding.UTF8.GetBytes(Payload("pro"))));
        await new OAuth(http, vault).ReadyAsync("A", default, forceRefresh: true);
        Check(vault.Find("A")!.Plan == "pro" && vault.Find("A")!.PlanSource == "native_codex_usage");
        Check(SubscriptionExpiry.PlanFromIdToken(issuer.Token(NativeLogin.NativeClientId, "sub-A", null,
            nativeMetadata: new Dictionary<string, object> { ["chatgpt_plan_type"] = "INVALID-PLAN-CANARY<" })) is null);
    }
    public static async Task RecoveryFailure(string root)
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            var vault = Store(root, "renewal-recovery-failed-" + (int)status);
            using var issuer = new FakeIssuer { NativeResponseBody = "ERROR-BODY-CANARY", NativeResponseStatus = status };
            using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
            using var http = new HttpClient(new AsyncHandler((r, _) => Task.FromResult(
                r.Headers.GetValues("ChatGPT-Account-Id").Single() == "backend-A" ? Reply(HttpStatusCode.Unauthorized) : Reply(HttpStatusCode.OK, Payload()))));
            var quota = new Quota(http, auth, vault);
            await quota.RefreshAllAsync(["A", "B"], default, force: true);
            var permanent = status == HttpStatusCode.Unauthorized;
            var failure = quota.RefreshErrors.Single();
            Check(failure.Status == 401 && failure.Code == "quota_auth_unavailable"
                && failure.RecoveryCode == (permanent ? "reauth_required" : "auth_temporarily_unavailable"));
            Check(vault.Find("A")!.AuthStatus == (permanent ? "reauth_required" : "ready"));
            Check(vault.Find("B")!.AuthStatus == "ready" && quota.Snapshot("B")!.Fresh(DateTimeOffset.UtcNow));
            await quota.RefreshAllAsync(["A"], default, force: true);
            Check(issuer.RefreshCalls == 1 && !JsonSerializer.Serialize(quota.RefreshErrors).Contains("CANARY"));
        }
    }
    public static async Task Cancellation(string root)
    {
        var vault = Store(root, "renewal-quota-cancellation");
        vault.SaveQuota(Quota.Parse("A", Encoding.UTF8.GetBytes(Payload("free"))));
        using var issuer = new FakeIssuer(); using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
        using var http = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(Reply(HttpStatusCode.Unauthorized))));
        var quota = new Quota(http, auth, vault);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        issuer.Barrier = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        using var caller = new CancellationTokenSource();
        var refresh = quota.RefreshAsync("A", caller.Token, force: true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(quota.Snapshot("A")!.CacheState == "historical");
            caller.Cancel();
        }
        finally { release.TrySetResult(); }
        try { await refresh; Check(false); } catch (OperationCanceledException) { }
        Check(vault.Find("A")!.AuthStatus == "ready" && issuer.RefreshCalls == 1);
        Check(quota.Snapshot("A")!.CacheState == "historical" && quota.RefreshErrors.Single().Status == 401);
        Check(vault.Find("B")!.RefreshToken == "old-refresh-B");
    }
    public static async Task ExpiredMetadata(string root)
    {
        using var issuer = new FakeIssuer(); using var authHttp = new HttpClient(issuer);
        var until = DateTimeOffset.UtcNow.AddDays(20);
        string IdToken(DateTimeOffset date, bool expired = false, DateTimeOffset? issuedAt = null) => issuer.Token(NativeLogin.NativeClientId, "sub-A", null,
            expired: expired, nativeMetadata: new Dictionary<string, object> { ["chatgpt_account_id"] = "backend-A", ["chatgpt_plan_type"] = "plus",
                ["chatgpt_subscription_active_until"] = date.ToUnixTimeSeconds() }, issuedAt: issuedAt);
        var vault = Store(root, "renewal-expired-metadata");
        vault.ReplaceTokens("A", "old-refresh-A", new() { AccessToken = "old-access-A", RefreshToken = "old-refresh-A", IdToken = IdToken(until, expired: true), ExpiresIn = 86400 });
        var beforeB = JsonSerializer.Serialize(vault.Find("B"));
        Check(SubscriptionExpiry.Inspect(vault.Find("A")!.IdToken).Reason == "id_token_expired");
        issuer.NativeResponseBody = JsonSerializer.Serialize(new { access_token = issuer.Token("native-access", "sub-A", null), id_token = IdToken(until) });
        using var http = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(Reply(HttpStatusCode.OK, Payload()))));
        var quota = new Quota(http, new OAuth(authHttp, vault), vault);
        await quota.RefreshAsync("A", default, force: true);
        Check(issuer.RefreshCalls == 1 && SubscriptionExpiry.Inspect(vault.Find("A")!.IdToken).State == "current");
        await quota.RefreshAsync("A", default, force: true);
        Check(issuer.RefreshCalls == 1 && JsonSerializer.Serialize(vault.Find("B")) == beforeB);
        // Actual upstream behavior: even a newly issued ID token can report the
        // previous subscription period. Repeated rotations cannot invent a date.
        var freshVault = Store(root, "renewal-fresh-old-deadline");
        freshVault.ReplaceTokens("A", "old-refresh-A", new() { AccessToken = "new-access-A", RefreshToken = "new-refresh-A",
            IdToken = IdToken(DateTimeOffset.UtcNow.AddDays(-2), issuedAt: DateTimeOffset.UtcNow), ExpiresIn = 86400 });
        var freshQuota = new Quota(http, new OAuth(authHttp, freshVault), freshVault);
        await freshQuota.RefreshAsync("A", default, force: true);
        Check(issuer.RefreshCalls == 1 && freshQuota.Snapshot("A")!.Fresh(DateTimeOffset.UtcNow));
        var view = JsonSerializer.SerializeToElement(freshVault.Find("A")!.PublicView(true));
        Check(view.GetProperty("subscriptionSyncReason").GetString() == "deadline_in_past" && view.GetProperty("subscriptionActiveUntil").ValueKind == JsonValueKind.Null);
    }
}
