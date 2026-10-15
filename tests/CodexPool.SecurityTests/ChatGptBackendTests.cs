using System.Net;
using System.Text;
using CodexPool.Daemon;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

internal static class ChatGptBackendTests
{
    public static async Task RunAsync()
    {
        const string key = "LOCAL-CONTROL-CANARY";
        var calls = 0;
        var expectedBody = "";
        var status = HttpStatusCode.OK;
        var fault = "";
        string? expectedTarget = null;
        using var upstream = new HttpClient(new Handler(async (request, cancellationToken) =>
        {
            calls++;
            Require(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "chatgpt.com");
            if (expectedTarget is not null) Require(request.RequestUri.PathAndQuery == expectedTarget);
            Require(request.Headers.Authorization?.Parameter == "DESKTOP-CANARY");
            Require(request.Headers.GetValues("ChatGPT-Account-Id").Single() == "desktop-account");
            Require(request.Headers.GetValues("User-Agent").Single() == "desktop-fixture");
            foreach (var name in new[] { "Cookie", "Origin", "Host", "X-Codex-Pool-CSRF", "X-Forwarded-Host" })
                Require(!request.Headers.Contains(name));
            if (expectedBody.Length > 0)
            {
                Require(request.Method == HttpMethod.Post);
                Require(await request.Content!.ReadAsStringAsync(cancellationToken) == expectedBody);
                Require(request.Content.Headers.ContentType!.MediaType == "application/json");
            }
            if (fault == "network") throw new HttpRequestException("UPSTREAM-ERROR-CANARY");
            if (fault == "timeout") throw new TaskCanceledException("UPSTREAM-ERROR-CANARY");
            if (fault == "cancel") { cancellationToken.ThrowIfCancellationRequested(); }
            var response = new HttpResponseMessage(status)
            { Content = new StringContent(status == HttpStatusCode.OK ? "{\"account\":\"desktop-account\"}" : "UPSTREAM-ERROR-CANARY", Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "upstream-secret=CANARY");
            response.Headers.Location = new Uri("https://untrusted.invalid/");
            return response;
        }));
        var backend = new ChatGptBackend(upstream, key);
        DefaultHttpContext Context(string path, string? token = "DESKTOP-CANARY")
        {
            var context = new DefaultHttpContext(); context.Request.Method = "GET";
            context.Request.Path = path; context.Response.Body = new MemoryStream();
            if (token is not null) context.Request.Headers.Authorization = "Bearer " + token;
            context.Request.Headers["ChatGPT-Account-Id"] = "desktop-account";
            context.Request.Headers.UserAgent = "desktop-fixture";
            context.Request.Headers.Cookie = "pool_session=LOCAL-COOKIE-CANARY";
            context.Request.Headers.Origin = "http://127.0.0.1:3210";
            context.Request.Headers["X-Codex-Pool-CSRF"] = "LOCAL-CSRF-CANARY";
            context.Request.Headers["X-Forwarded-Host"] = "untrusted.invalid";
            return context;
        }
        static string Body(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        // These are the account/bootstrap reads that follow successful OAuth in Codex.
        foreach (var path in new[] { "/backend-api/wham/accounts/check", "/backend-api/wham/profiles/me", "/backend-api/wham/config/bundle", "/backend-api/me", "/api/codex/accounts/check" })
        {
            var context = Context(path); context.Request.QueryString = new("?client_version=0.162.1&cursor=a%2Fb");
            expectedTarget = path + context.Request.QueryString.Value;
            await backend.ForwardAsync(context);
            Require(context.Response.StatusCode == 200 && Body(context) == "{\"account\":\"desktop-account\"}");
            Require(!context.Response.Headers.ContainsKey("Set-Cookie") && !context.Response.Headers.ContainsKey("Location"));
        }
        expectedTarget = null;
        // Usage remains handled locally; controls and prefix lookalikes are never relayed.
        foreach (var path in new[] { "/backend-api/wham/usage", "/api/codex/usage", "/status", "/backend-api-evil/me", "/api/codex-evil/accounts/check" })
        {
            Require(!ChatGptBackend.IsPassthroughPath(path));
            var context = Context(path); var before = calls; await backend.ForwardAsync(context);
            Require(context.Response.StatusCode == 404 && calls == before);
        }
        foreach (var (token, expected) in new[] { ((string?)null, 401), ("", 401), (key, 403) })
        {
            var context = Context("/backend-api/wham/accounts/check", token); var before = calls;
            await backend.ForwardAsync(context); Require(context.Response.StatusCode == expected && calls == before);
        }
        var escape = Context("/backend-api/../../status"); var beforeEscape = calls;
        await backend.ForwardAsync(escape); Require(escape.Response.StatusCode == 400 && calls == beforeEscape);
        expectedBody = "{\"credit_type\":\"codex\"}";
        var post = Context("/backend-api/wham/credits/nudge"); post.Request.Method = "POST";
        post.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(expectedBody));
        post.Request.ContentLength = post.Request.Body.Length; post.Request.ContentType = "application/json";
        await backend.ForwardAsync(post); Require(post.Response.StatusCode == 200);
        post.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(expectedBody));
        post.Request.ContentLength = null; post.Features.Set<IHttpRequestBodyDetectionFeature>(new StreamingBody());
        await backend.ForwardAsync(post); Require(post.Response.StatusCode == 200); expectedBody = "";
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.Redirect })
        {
            status = code; var context = Context("/backend-api/wham/accounts/check"); var before = calls;
            await backend.ForwardAsync(context);
            Require(calls == before + 1 && context.Response.StatusCode == (code == HttpStatusCode.Redirect ? 502 : (int)code));
            Require(!Body(context).Contains("UPSTREAM-ERROR-CANARY") && !context.Response.Headers.ContainsKey("Location"));
        }
        status = HttpStatusCode.OK;
        foreach (var (kind, expected) in new[] { ("network", 502), ("timeout", 504) })
        {
            fault = kind; var context = Context("/backend-api/wham/accounts/check"); var before = calls;
            await backend.ForwardAsync(context);
            Require(calls == before + 1 && context.Response.StatusCode == expected && !Body(context).Contains("UPSTREAM-ERROR-CANARY"));
        }
        fault = "cancel";
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var aborted = Context("/backend-api/wham/accounts/check"); aborted.RequestAborted = canceled.Token;
        var beforeCancel = calls; await backend.ForwardAsync(aborted);
        Require(calls <= beforeCancel + 1 && aborted.Response.Body.Length == 0);
    }

    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("ChatGPT backend regression."); }
    private sealed class StreamingBody : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
