using System.Net;
using System.Text;
using System.Text.Json;
using CodexPool.Daemon;
using Microsoft.AspNetCore.Http;

internal static class WhamUsageForwardTests
{
    private const string Official = """{"user_id":"official-user","account_id":"official-account","plan_type":"plus","rate_limit":{"allowed":false,"limit_reached":true,"primary_window":{"used_percent":100,"reset_at":1900000000},"secondary_window":{"used_percent":47}},"credits":{"has_credits":false,"balance":"12.3","overage_limit_reached":true},"spend_control":{"reached":true,"limit":75},"rate_limit_upsell":{"banner_type":"usage_limit"},"additional_rate_limits":[{"normal_model_slug":"reserve-model","rate_limit":{"allowed":false,"limit_reached":true,"primary_window":{"used_percent":100}}}],"future":{"opaque":[1,2,3]}}""";
    public static void Rewrite()
    {
        using var original = JsonDocument.Parse(Official);
        using var rewritten = JsonDocument.Parse(WhamUsage.Rewrite(Encoding.UTF8.GetBytes(Official), true, _ => true));
        var root = rewritten.RootElement; var source = original.RootElement;
        foreach (var name in new[] { "user_id", "account_id", "plan_type", "future" }) Require(JsonElement.DeepEquals(root.GetProperty(name), source.GetProperty(name)));
        var rate = root.GetProperty("rate_limit"); Require(rate.GetProperty("allowed").GetBoolean() && !rate.GetProperty("limit_reached").GetBoolean());
        foreach (var name in new[] { "primary_window", "secondary_window" }) Require(JsonElement.DeepEquals(rate.GetProperty(name), source.GetProperty("rate_limit").GetProperty(name)));
        Require(root.GetProperty("credits").GetProperty("balance").GetString() == "12.3" && !root.GetProperty("credits").GetProperty("has_credits").GetBoolean());
        Require(!root.GetProperty("credits").GetProperty("overage_limit_reached").GetBoolean() && !root.GetProperty("spend_control").GetProperty("reached").GetBoolean());
        Require(root.GetProperty("rate_limit_upsell").ValueKind == JsonValueKind.Null);
        Require(root.GetProperty("additional_rate_limits")[0].GetProperty("rate_limit").GetProperty("allowed").GetBoolean());
        using var blocked = JsonDocument.Parse(WhamUsage.Rewrite(Encoding.UTF8.GetBytes(Official), false, _ => false));
        Require(!blocked.RootElement.GetProperty("rate_limit").GetProperty("allowed").GetBoolean());
        Require(JsonElement.DeepEquals(blocked.RootElement, source));
        foreach (var invalid in new[] { "{}", "[]", "{\"rate_limit\":{}}", Official.Replace("\"user_id\":\"official-user\",", "") })
        {
            try { WhamUsage.Rewrite(Encoding.UTF8.GetBytes(invalid), true); throw new Exception("Incomplete usage accepted"); }
            catch (JsonException) { }
        }
    }

    public static async Task RunAsync()
    {
        var calls = 0; var body = Official; var status = HttpStatusCode.OK; var failure = false;
        using var http = new HttpClient(new Handler((request, token) =>
        {
            calls++; if (failure) throw new HttpRequestException("UPSTREAM-ERROR-CANARY");
            Require(request.RequestUri!.AbsoluteUri == "https://chatgpt.com/backend-api/wham/usage?preserve=yes");
            Require(request.Headers.Authorization?.Parameter == "DESKTOP-CANARY");
            Require(request.Headers.GetValues("ChatGPT-Account-Id").Single() == "caller-account");
            Require(request.Headers.GetValues("OAI-App-Brand").Single() == "codex");
            Require(!request.Headers.Contains("Cookie"));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }));
        var backend = new ChatGptBackend(http, "LOCAL-CANARY");
        DefaultHttpContext Context(string path = "/backend-api/wham/usage", string? bearer = "DESKTOP-CANARY")
        {
            var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Request.Path = path;
            context.Request.QueryString = new("?preserve=yes"); context.Response.Body = new MemoryStream();
            if (bearer is not null) context.Request.Headers.Authorization = "Bearer " + bearer;
            context.Request.Headers["ChatGPT-Account-Id"] = "caller-account";
            context.Request.Headers["OAI-App-Brand"] = "codex"; context.Request.Headers.Cookie = "LOCAL-COOKIE-CANARY";
            return context;
        }
        foreach (var path in new[] { "/backend-api/wham/usage", "/api/codex/usage" })
        {
            var context = Context(path); var before = calls; await backend.ForwardUsageAsync(context, true);
            Require(context.Response.StatusCode == 200 && calls == before + 1);
            using var result = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
            Require(result.RootElement.GetProperty("account_id").GetString() == "official-account");
            Require(result.RootElement.GetProperty("user_id").GetString() == "official-user");
            Require(result.RootElement.GetProperty("rate_limit").GetProperty("allowed").GetBoolean());
        }
        foreach (var (bearer, expected) in new[] { ((string?)null, 401), ("LOCAL-CANARY", 403) })
        {
            var context = Context(bearer: bearer); var before = calls; await backend.ForwardUsageAsync(context, true);
            Require(context.Response.StatusCode == expected && calls == before);
        }
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect })
        {
            status = code; body = "UPSTREAM-ERROR-CANARY"; var context = Context(); var before = calls;
            await backend.ForwardUsageAsync(context, true);
            Require(context.Response.StatusCode == (code == HttpStatusCode.Redirect ? 502 : (int)code) && calls == before + 1);
            Require(!Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()).Contains(body));
        }
        status = HttpStatusCode.OK;
        foreach (var invalid in new[] { "{}", new string(' ', WhamUsage.MaxResponseBytes + 1), "not-json" })
        {
            body = invalid; var context = Context(); await backend.ForwardUsageAsync(context, true);
            Require(context.Response.StatusCode == 502);
        }
        failure = true; var unavailable = Context(); var callsBefore = calls;
        await backend.ForwardUsageAsync(unavailable, true); Require(unavailable.Response.StatusCode == 502 && calls == callsBefore + 1);
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("WHAM usage invariant failed"); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
