namespace CodexPool.Daemon;

/// <summary>
/// ChatGPT Desktop 在 chatgpt_base_url 指向本机时会请求 /backend-api/wham/usage。
/// 此处按账户池可用性改写 rate_limit.allowed，从而在池内仍有可用账户时解除 Desktop 的发送硬阻塞，
/// 同时保留用户本机 ChatGPT 登录供远端能力使用。仅 loopback 可达。
/// </summary>
public static class WhamUsage
{
    public static bool PoolAllowed(IReadOnlyList<AccountAvailability> availability) =>
        availability.Any(x => x.Available);

    public static object Response(IReadOnlyList<AccountAvailability> availability, string? accountId = null, string? planType = null)
    {
        var allowed = PoolAllowed(availability);
        // Desktop 以 rate_limit.allowed === false 作为 composer 硬阻塞条件；池内任一可用账户即放行。
        var rateLimit = new Dictionary<string, object?>
        {
            ["allowed"] = allowed,
            ["limit_reached"] = !allowed,
        };
        var body = new Dictionary<string, object?>
        {
            ["rate_limit"] = rateLimit,
        };
        if (!string.IsNullOrWhiteSpace(accountId) && accountId.Length <= 128)
            body["account_id"] = accountId;
        if (!string.IsNullOrWhiteSpace(planType) && System.Text.RegularExpressions.Regex.IsMatch(planType, "^[a-zA-Z0-9_-]{1,80}$"))
            body["plan_type"] = planType;
        return body;
    }

    public static string? HeaderAccountId(Microsoft.AspNetCore.Http.HttpRequest request) =>
        request.Headers.TryGetValue("ChatGPT-Account-Id", out var values)
            ? values.FirstOrDefault()
            : null;
}
