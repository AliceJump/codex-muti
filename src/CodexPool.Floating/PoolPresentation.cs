using System.Text.Json;

namespace CodexPool.Floating;

internal sealed record TokenCacheSplit(decimal? Cached, decimal? Uncached)
{
    internal double? CachedPercent => Cached is { } cached && Uncached is { } uncached && cached + uncached > 0
        ? (double)(cached / (cached + uncached) * 100) : null;
    internal string CachedLabel => "缓存输入 " + Amount(Cached) + " · " + Percent(CachedPercent);
    internal string UncachedLabel => "未缓存输入 " + Amount(Uncached) + " · " + Percent(CachedPercent is { } percent ? 100 - percent : null);
    private static string Percent(double? value) => value?.ToString("0.0") + (value is null ? "—" : "%");
    private static string Amount(decimal? value) => value is not { } amount ? "待确认"
        : amount >= 100_000_000 ? (amount / 100_000_000).ToString("0.##") + "亿"
        : amount >= 10_000 ? (amount / 10_000).ToString("0.##") + "万" : amount.ToString("N0");
}

internal sealed record PoolAccountView(string Id, string Name, bool Active, bool Enabled, bool Selectable,
    string Status, string Plan, DisplayQuotaWindow[] Windows, string QuotaState, string Tokens, string Updated)
{
    internal string TokenDetails { get; init; } = "";
    internal TokenCacheSplit? TokenCache { get; init; }
    internal string SubscriptionDeadline { get; init; } = "订阅截止时间未知";
    internal string Description => Name + " · " + Plan + " · " + Status + "\n"
        + SubscriptionDeadline + "\n"
        + string.Join("\n", Windows.Select(w => w.Label + " " + QuotaDisplay.Percent(w) + " · " + PoolPresentation.Reset(w)))
        + "\n" + QuotaState + "\n" + Tokens + (TokenDetails.Length > 0 ? "\n" + TokenDetails : "") + "\n" + Updated;
}
internal static class PoolPresentation
{
    internal static PoolAccountView[] Accounts(JsonElement state, bool connected)
    {
        var usage = state.GetProperty("usage").EnumerateArray().ToArray();
        return state.GetProperty("accounts").EnumerateArray().Select(a =>
        {
            var id = a.GetProperty("id").GetString()!;
            var quota = usage.FirstOrDefault(q => q.GetProperty("accountId").GetString() == id);
            var auth = String(a, "auth"); var status = String(a, "state");
            var enabled = a.GetProperty("enabled").GetBoolean(); var active = a.GetProperty("active").GetBoolean();
            var selectable = enabled && !a.GetProperty("paused").GetBoolean() && !a.GetProperty("drain").GetBoolean()
                && status != "cooldown" && auth is not ("reauth_required" or "refresh_pending");
            var label = !connected ? "离线" : auth == "reauth_required" ? "需要重新授权" : auth == "refresh_pending" ? "授权待确认"
                : status switch { "active" => "使用中", "standby" => "待命", "paused" => "已暂停", "draining" => "排空中", "disabled" => "已禁用", "cooldown" => "冷却中", _ => "待确认" };
            var updated = quota.ValueKind == JsonValueKind.Object && quota.TryGetProperty("updatedAt", out var at) && at.TryGetDateTimeOffset(out var timestamp)
                ? "更新 " + timestamp.ToLocalTime().ToString("HH:mm:ss") + (QuotaDisplay.Fresh(quota) ? "" : " · 已过期") : "更新时间未知";
            var tokens = ""; var tokenDetails = ""; TokenCacheSplit? tokenCache = null;
            if (state.TryGetProperty("tokenStatistics", out var statistics) && statistics.TryGetProperty("today", out var today) && today.TryGetProperty("accounts", out var amounts))
            {
                var match = amounts.EnumerateArray().FirstOrDefault(x => String(x, "accountId") == id);
                var totals = match.ValueKind == JsonValueKind.Object ? match.GetProperty("totals") : default;
                var requests = totals.ValueKind == JsonValueKind.Object ? totals.GetProperty("requests").GetInt64() : 0;
                var reported = totals.ValueKind == JsonValueKind.Object ? totals.GetProperty("reportedRequests").GetInt64() : 0;
                var amount = requests > 0 && reported == 0 ? "未报告" : totals.ValueKind == JsonValueKind.Object ? totals.GetProperty("totalTokens").GetDecimal().ToString("N0") : "0";
                tokens = "今日 Token 用量 " + amount;
                tokenCache = requests == 0 ? new(0, 0) : new(null, null);
                tokenDetails = requests + " 个请求（已报告 " + reported + "）";
                if (totals.ValueKind == JsonValueKind.Object)
                {
                    static decimal? Count(JsonElement value, string field) => value.TryGetProperty(field, out var item)
                        && item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var count) ? count : null;
                    static string Display(decimal? value) => value?.ToString("N0") ?? "未报告";
                    var input = Count(totals, "inputTokens"); var output = Count(totals, "outputTokens");
                    var cached = Count(totals, "cachedInputTokens");
                    var cachedReports = Count(totals, "cachedReportedRequests");
                    var known = reported > 0 || requests == 0;
                    // Each card uses only this account's reported input. Missing
                    // cache detail must not be relabeled as uncached consumption.
                    if (known && input is >= 0 && cached is >= 0 && cached <= input && cachedReports == reported)
                        tokenCache = new(cached, input - cached);
                    tokenDetails += "\n输入 " + Display(known ? input : null) + " · 输出 " + Display(known ? output : null)
                        + "\n缓存输入 " + (cachedReports is > 0 ? Display(cached) : "未报告") + "（已包含在输入中）";
                }
                tokenDetails += "\n未缓存输入 " + (tokenCache.Uncached?.ToString("N0") ?? "待确认")
                    + " · 缓存占比 " + (tokenCache.CachedPercent is { } ratio ? ratio.ToString("0.0") + "%" : "—")
                    + "\n横条仅划分本账户输入，输出包含在上方今日总用量中。";
                tokenDetails += "\n累计每次请求的上下文与输出，不代表套餐额度扣减或费用。";
            }
            return new PoolAccountView(id, String(a, "name") ?? "账户", active, enabled, selectable && connected,
                label, (String(a, "plan") ?? "Plan 待确认") + (String(a, "planState") == "historical" ? " · 待同步" : ""), QuotaDisplay.Windows(quota), QuotaDisplay.State(quota), tokens, updated)
                { TokenDetails = tokenDetails, TokenCache = tokenCache, SubscriptionDeadline = SubscriptionDeadline(a) };
        }).ToArray();
    }
    internal static string? String(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static string SubscriptionDeadline(JsonElement account) => String(account, "subscriptionState") == "stale"
        ? "订阅截止时间待同步（旧授权资料）" : String(account, "subscriptionState") == "not_reported"
        ? "订阅截止时间未知（上游未提供）" : account.TryGetProperty("subscriptionActiveUntil", out var value)
        && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var until)
        ? "订阅截止 " + until.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss '(UTC'zzz')'", System.Globalization.CultureInfo.InvariantCulture)
        : "订阅截止时间未知";
    internal static string Reset(DisplayQuotaWindow window)
    {
        if (window.Remaining is null) return "额度待确认";
        if (!window.Window.TryGetProperty("resetAt", out var reset) || !reset.TryGetDateTimeOffset(out var at)) return "重置时间未知";
        var span = at - DateTimeOffset.UtcNow;
        if (span <= TimeSpan.Zero) return "等待新额度";
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h 后重置"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m 后重置" : $"{(int)span.TotalMinutes}m 后重置";
    }
}
