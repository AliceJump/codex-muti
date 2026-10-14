namespace CodexPool.Daemon;

/// <summary>
/// ChatGPT Desktop 在 chatgpt_base_url 指向本机时会请求 /backend-api/wham/usage。
/// 此处按模型路由可用性改写 rate_limit.allowed，从而在池内仍有可路由账户时解除 Desktop 的发送硬阻塞，
/// 同时保留用户本机 ChatGPT 登录供远端能力使用。仅 loopback 可达。
/// </summary>
public static class WhamUsage
{
    public static object Response(bool allowed, string? accountId = null, string? planType = null)
    {
        // Desktop 以 rate_limit.allowed === false 作为 composer 硬阻塞条件。
        var rateLimit = new Dictionary<string, object?>
        {
            ["allowed"] = allowed,
            ["limit_reached"] = !allowed,
        };
        var body = new Dictionary<string, object?>
        {
            ["rate_limit"] = rateLimit,
            // Required by Codex's RateLimitStatusPayload even for an empty pool.
            ["plan_type"] = !string.IsNullOrWhiteSpace(planType) && System.Text.RegularExpressions.Regex.IsMatch(planType, "^[a-zA-Z0-9_-]{1,80}$")
                ? planType : "unknown",
        };
        if (!string.IsNullOrWhiteSpace(accountId) && accountId.Length <= 128)
            body["account_id"] = accountId;
        return body;
    }

    public static string? HeaderAccountId(Microsoft.AspNetCore.Http.HttpRequest request) =>
        request.Headers.TryGetValue("ChatGPT-Account-Id", out var values)
            ? values.FirstOrDefault()
            : null;
}
