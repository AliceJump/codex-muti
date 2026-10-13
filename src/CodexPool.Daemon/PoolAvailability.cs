using CodexPool.Core;

namespace CodexPool.Daemon;

public sealed record AccountAvailability(string AccountId, string? Reason, string? ErrorCode = null,
    DateTimeOffset? ResetAt = null, string? Plan = null, int? WindowMinutes = null)
{ public bool Available => Reason is null; }

public static class PoolAvailability
{
    public static ModelError Aggregate(IReadOnlyList<AccountAvailability> accounts)
    {
        if (accounts.Count == 0) return ModelError.Terminal("account_pool_empty", "此模型提供方没有已注册账户。");
        var participating = accounts.Where(x => x.Reason is not ("disabled" or "paused" or "draining" or "incompatible_provider")).ToArray();
        if (participating.Length == 0) return ModelError.Terminal("account_pool_disabled", "账户均已禁用、暂停或排空，请在 Codex Pool 检查设置。");
        if (participating.Any(x => x.Available))
            return ModelError.Terminal("request_accounts_unavailable", "本次请求预留的账户均未完成请求，新账户可以处理下一次请求。");
        if (participating.All(x => x.Reason == "quota_exhausted"))
        {
            if (participating.All(x => ModelErrors.IsBalanceCode(x.ErrorCode)))
                return new(429, "insufficient_quota", "insufficient_quota", "所有参与调度的账户均缺少模型消费余额或支出额度。");
            var reset = participating.All(x => x.ResetAt is not null) ? participating.Min(x => x.ResetAt) : null;
            var first = participating.FirstOrDefault(x => x.ResetAt == reset) ?? participating[0];
            return ModelError.Quota(ModelErrors.ValidPlan(first.Plan) ? first.Plan : null, reset?.ToUnixTimeSeconds(), first.WindowMinutes);
        }
        if (participating.All(x => x.Reason == "auth_unavailable"))
            return ModelError.Terminal("account_pool_auth_required", "所有参与调度的池账户都需要在 Codex Pool 中重新授权，客户端登录保持不变。");
        if (participating.All(x => x.Reason == "access_unavailable"))
            return new(429, "usage_not_included", "usage_not_included", "参与调度的账户套餐均不包含此模型的使用权限。");
        if (participating.All(x => x.Reason == "auth_temporarily_unavailable"))
            return new(503, "server_is_overloaded", "server_is_overloaded", "各账户授权服务暂时不可用，请稍后再试。", RetryAfter: "30");
        if (participating.Any(x => x.Reason is "5h_threshold" or "weekly_threshold"))
            return ModelError.Terminal("account_pool_threshold_reached", "当前切换阈值下没有可用账户，请在 Codex Pool 检查各账户状态与阈值。");
        return ModelError.Terminal("account_pool_unavailable", "账户池因额度、授权、套餐权限或冷却等不同原因暂不可用，请在 Codex Pool 查看每个账户。");
    }
    public static ModelError Auth(string code) => code switch
    {
        "auth_temporarily_unavailable" or "refresh_not_yet_allowed" => new(503, "server_is_overloaded", "server_is_overloaded", "池账户授权暂时不可用，请稍后再试。", RetryAfter: "30"),
        "reauth_required" or "refresh_outcome_unknown" or "rotated_token_persistence_failed" or "account_identity_mismatch" =>
            ModelError.Terminal("account_auth_required", "池账户需要在 Codex Pool 中重新授权，客户端登录保持不变。"),
        "account_not_found" or "no_authorized_account" => ModelError.Terminal("account_pool_empty", "Codex Pool 没有可用的已授权账户。"),
        _ => ModelError.Terminal("account_configuration_error", "账户配置无法提供模型访问，请检查 Codex Pool。")
    };
}
