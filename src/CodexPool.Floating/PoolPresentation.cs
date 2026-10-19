using System.Text.Json;

namespace CodexPool.Floating;

internal sealed record PoolAccountView(string Id, string Name, bool Active, bool Enabled, bool Selectable,
    string Status, string Plan, DisplayQuotaWindow[] Windows, string QuotaState, string Tokens, string Updated)
{
    internal string TokenDetails { get; init; } = "";
    internal string Description => Name + " · " + Plan + " · " + Status + "\n"
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
            var tokens = ""; var tokenDetails = "";
            if (state.TryGetProperty("tokenStatistics", out var statistics) && statistics.TryGetProperty("today", out var today) && today.TryGetProperty("accounts", out var amounts))
            {
                var match = amounts.EnumerateArray().FirstOrDefault(x => String(x, "accountId") == id);
                var totals = match.ValueKind == JsonValueKind.Object ? match.GetProperty("totals") : default;
                var requests = totals.ValueKind == JsonValueKind.Object ? totals.GetProperty("requests").GetInt64() : 0;
                var reported = totals.ValueKind == JsonValueKind.Object ? totals.GetProperty("reportedRequests").GetInt64() : 0;
                var amount = requests > 0 && reported == 0 ? "未报告" : totals.ValueKind == JsonValueKind.Object ? totals.GetProperty("totalTokens").GetDecimal().ToString("N0") : "0";
                tokens = "今日处理 " + amount + " Token（含缓存）";
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
                    tokenDetails += "\n输入 " + Display(known ? input : null) + " · 输出 " + Display(known ? output : null)
                        + "\n缓存输入 " + (cachedReports is > 0 ? Display(cached) : "未报告") + "（已包含在输入中）";
                }
                tokenDetails += "\n累计每次请求的上下文与输出，不代表套餐额度扣减或费用。";
            }
            return new PoolAccountView(id, String(a, "name") ?? "账户", active, enabled, selectable && connected,
                label, String(a, "plan") ?? "Plan 待确认", QuotaDisplay.Windows(quota), QuotaDisplay.State(quota), tokens, updated) { TokenDetails = tokenDetails };
        }).ToArray();
    }
    internal static string? String(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
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
