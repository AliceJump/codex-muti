using System.Net;
using System.Text;
using CodexPool.Core;
using CodexPool.Daemon;

internal static class LazyPreparationTests
{
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException(); }
    private static Vault Store(string root, string name)
    {
        var vault = new Vault(Path.Combine(root, name));
        foreach (var id in new[] { "A", "B", "C" }) vault.Add(new AccountCredential
        {
            Id = id, Name = id, Subject = "sub-" + id, ClientId = NativeLogin.NativeClientId, ProviderKind = "native",
            BackendAccountId = "backend-" + id, AccessToken = "synthetic-access-" + id, RefreshToken = "synthetic-refresh-" + id,
            IdToken = "synthetic-id-" + id, Scope = "native_codex", ExpiresAt = DateTimeOffset.UtcNow.AddHours(id == "A" ? 1 : -1), UpdatedAt = DateTimeOffset.UtcNow
        });
        return vault;
    }
    private static HttpResponseMessage Completed() => new(HttpStatusCode.OK)
    { Content = new StringContent("data: {\"type\":\"response.completed\"}\n\n", Encoding.UTF8, "text/event-stream") };
    public static async Task Healthy(string root)
    {
        var vault = Store(root, "lazy-healthy"); var authCalls = 0;
        using var authHttp = new HttpClient(new AsyncHandler((_, _) => { authCalls++; throw new InvalidOperationException(); }));
        var auth = new OAuth(authHttp, vault); var pool = new PoolRuntime(vault, auth, new Quota(authHttp, auth, vault), true); pool.UpdateSettings(new(true));
        using var models = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(Completed())));
        using (var reservation = pool.ReserveModelRequest())
        {
            var result = await new ResponsesRelay(models).ForwardAsync("{}"u8.ToArray(), "application/json",
                pool.PrepareCandidatesAsync(reservation, default), new Dictionary<string, string>(), new ProbeSink(), default).WaitAsync(TimeSpan.FromSeconds(2));
            Check(result.AccountId == "A" && result.Attempts == 1 && result.StatusCode == 200 && authCalls == 0);
        }
        Check(pool.Delete("B", out _));
        var catalogCalls = 0;
        using var catalogHttp = new HttpClient(new AsyncHandler((_, _) =>
        { catalogCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[]}") }); }));
        var catalog = await new ModelCatalog(catalogHttp, pool).ReadAsync("0.162.1", default);
        Check(catalog.AccountId == "A" && catalogCalls == 1 && authCalls == 0);
    }
    public static async Task Failover(string root)
    {
        var vault = Store(root, "lazy-failover"); var modelCalls = 0;
        using var issuer = new FakeIssuer { Barrier = _ => { Check(modelCalls == 1); return Task.CompletedTask; } };
        using var authHttp = new HttpClient(issuer); var auth = new OAuth(authHttp, vault);
        var pool = new PoolRuntime(vault, auth, new Quota(authHttp, auth, vault), true); pool.UpdateSettings(new(true));
        using var models = new HttpClient(new AsyncHandler((request, _) =>
        {
            modelCalls++;
            if (modelCalls == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent("{\"error\":{\"code\":\"usage_limit_reached\"}}") });
            Check(request.Headers.GetValues("ChatGPT-Account-Id").Single() == "backend-B");
            return Task.FromResult(Completed());
        }));
        using var reservation = pool.ReserveModelRequest();
        var result = await new ResponsesRelay(models).ForwardAsync("{}"u8.ToArray(), "application/json",
            pool.PrepareCandidatesAsync(reservation, default), new Dictionary<string, string>(), new ProbeSink(), default);
        Check(result.AccountId == "B" && result.Attempts == 2 && modelCalls == 2 && issuer.RefreshCalls == 1);
        Check(vault.Find("C")!.RefreshToken == "synthetic-refresh-C");
    }
    public static async Task Unknown(string root)
    {
        var vault = Store(root, "lazy-unknown"); var modelCalls = 0; var refreshCalls = 0;
        using var authHttp = new HttpClient(new AsyncHandler((_, _) => { refreshCalls++; throw new HttpRequestException("synthetic"); }));
        var auth = new OAuth(authHttp, vault); var pool = new PoolRuntime(vault, auth, new Quota(authHttp, auth, vault), true); pool.UpdateSettings(new(true));
        using var models = new HttpClient(new AsyncHandler((_, _) =>
        { modelCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"code\":\"usage_limit_reached\"}}") }); }));
        using var reservation = pool.ReserveModelRequest();
        try
        {
            await new ResponsesRelay(models).ForwardAsync("{}"u8.ToArray(), "application/json", pool.PrepareCandidatesAsync(reservation, default),
                new Dictionary<string, string>(), new ProbeSink(), default); Check(false);
        }
        catch (AuthFailure ex) { Check(ex.Code == "refresh_outcome_unknown"); }
        Check(modelCalls == 1 && refreshCalls == 1 && vault.Find("B")!.AuthStatus == "reauth_required" && vault.Find("C")!.AuthStatus == "ready");
    }
}
