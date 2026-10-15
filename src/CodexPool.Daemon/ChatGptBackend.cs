using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;

namespace CodexPool.Daemon;

// chatgpt_base_url also carries account/bootstrap and cloud APIs, not just usage.
// Preserve the caller's identity: no pool selection, refresh, retries or credential writes.
public sealed class ChatGptBackend(HttpClient http, string localKey)
{
    public static bool IsPassthroughPath(string path) =>
        (path.StartsWith("/backend-api/", StringComparison.Ordinal) || path.StartsWith("/api/codex/", StringComparison.Ordinal))
        && path is not ("/backend-api/wham/usage" or "/api/codex/usage");

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    }) { Timeout = TimeSpan.FromSeconds(30) };

    public async Task ForwardAsync(HttpContext context)
    {
        var input = context.Request;
        var path = input.Path.Value ?? "";
        if (!IsPassthroughPath(path)) { context.Response.StatusCode = 404; return; }
        if (input.Headers.Authorization.Count != 1
            || !AuthenticationHeaderValue.TryParse(input.Headers.Authorization.ToString(), out var authorization)
            || !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(authorization.Parameter) || authorization.Parameter.Length > 16384)
        { context.Response.StatusCode = 401; return; }
        // A local control key must never leave the machine.
        if (LocalCommands.EqualSecret(authorization.Parameter, localKey))
        { context.Response.StatusCode = 403; return; }
        if (!Uri.TryCreate("https://chatgpt.com" + path + input.QueryString.Value, UriKind.Absolute, out var target)
            || target.Scheme != "https" || target.Host != "chatgpt.com" || target.Port != 443
            || !IsPassthroughPath(target.AbsolutePath) || target.Fragment.Length != 0)
        { context.Response.StatusCode = 400; return; }
        using var request = new HttpRequestMessage(new HttpMethod(input.Method), target);
        request.Headers.Authorization = authorization;
        // Do not forward local browser cookies, control/CSRF headers, Host or Origin.
        foreach (var name in new[] { "Accept", "User-Agent", "ChatGPT-Account-Id", "ChatGPT-User-Id", "X-OpenAI-Fedramp", "OpenAI-Beta", "originator", "version" })
            if (input.Headers.TryGetValue(name, out var value)) request.Headers.TryAddWithoutValidation(name, value.ToArray());
        // HTTP/2 request bodies can have neither Content-Length nor Transfer-Encoding.
        var hasBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
            ?? (input.ContentLength is > 0 || input.Headers.ContainsKey("Transfer-Encoding"));
        if (hasBody)
        {
            request.Content = new StreamContent(input.Body);
            if (input.ContentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", input.ContentType);
            if (input.Headers.TryGetValue("Content-Encoding", out var encoding)) request.Content.Headers.TryAddWithoutValidation("Content-Encoding", encoding.ToArray());
        }
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            // Never redirect the client (and its bearer) to an upstream-provided location.
            if ((int)response.StatusCode is >= 300 and < 400)
            { await ErrorAsync(context, 502, "chatgpt_backend_redirect"); return; }
            if (!response.IsSuccessStatusCode)
            {
                // Preserve genuine auth failures but never expose upstream error bodies.
                await ErrorAsync(context, (int)response.StatusCode, "chatgpt_backend_rejected"); return;
            }
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } type) context.Response.ContentType = type.ToString();
            // Stream once; a disconnect or partial response can never trigger a replay.
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (context.Response.HasStarted) context.Abort();
            else await ErrorAsync(context, ex is OperationCanceledException ? 504 : 502, "chatgpt_backend_unavailable");
        }
    }

    private static Task ErrorAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = new { code } }, context.RequestAborted);
    }
}
