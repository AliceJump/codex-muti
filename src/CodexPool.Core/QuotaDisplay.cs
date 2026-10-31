using System.Text.Json;

namespace CodexPool.Core;

public sealed record DisplayQuotaWindow(string Key, string Kind, string Label, JsonElement Window, double? Remaining, int? Seconds, string Family);
public static class QuotaDisplay
{
    public static bool Fresh(JsonElement quota) => quota.ValueKind == JsonValueKind.Object
        && (!quota.TryGetProperty("refreshError", out var error) || error.ValueKind == JsonValueKind.Null)
        && (!quota.TryGetProperty("cacheState", out var cache) || cache.GetString() != "historical")
        && quota.TryGetProperty("updatedAt", out var updated) && updated.ValueKind == JsonValueKind.String && updated.TryGetDateTimeOffset(out var at)
        && DateTimeOffset.UtcNow - at <= TimeSpan.FromMinutes(2) && at <= DateTimeOffset.UtcNow.AddSeconds(30);
    public static DisplayQuotaWindow[] Windows(JsonElement quota)
    {
        if (quota.ValueKind != JsonValueKind.Object || !quota.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Array) return [];
        var fresh = Fresh(quota);
        return windows.EnumerateArray().Select((w, index) =>
        {
            var kind = w.GetProperty("kind").GetString() ?? "window";
            int? seconds = w.TryGetProperty("windowSeconds", out var length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out var value) ? value
                : kind == "5h" ? 18000 : kind == "weekly" ? 604800 : null;
            var family = w.TryGetProperty("limitId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "codex" : "codex";
            var label = QuotaLabels.Duration(seconds);
            if (family != "codex") label = (w.TryGetProperty("limitName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : "附加额度") + " · " + label;
            double? remaining = fresh && w.TryGetProperty("resetAt", out var reset) && reset.ValueKind == JsonValueKind.String && reset.TryGetDateTimeOffset(out var at) && at > DateTimeOffset.UtcNow
                ? w.TryGetProperty("remainingPercent", out var amount) && amount.ValueKind == JsonValueKind.Number && amount.TryGetDouble(out var percent) && double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : null : null;
            return new DisplayQuotaWindow(family + ":" + kind + ":" + seconds + ":" + index, kind, label, w, remaining, seconds, family);
        }).ToArray();
    }
    public static DisplayQuotaWindow? TrayWindow(JsonElement quota, JsonElement settings = default)
    {
        var policy = settings.ValueKind == JsonValueKind.Object ? settings.Deserialize<PoolSettings>(JsonSerializerOptions.Web) ?? new() : new PoolSettings();
        var reported = Windows(quota).Where(w => w.Family == "codex").ToArray();
        var windows = reported.Where(w => !(w.Remaining <= 0 && AllowsContinuation(quota))).ToArray();
        bool Weekly(DisplayQuotaWindow w) => w.Seconds == 604800;
        bool Limited(DisplayQuotaWindow w) => w.Remaining is { } remaining
            && !(remaining <= 0 && AllowsContinuation(quota))
            && (remaining <= 0 || policy.AutoSwitch && 100 - remaining >= policy.Threshold(w.Seconds));
        // Weekly usage being numerically higher does not make it a routing limit.
        // Show a limiting window first; otherwise retain the ordinary short window.
        // Weekly-only plans use their weekly window, but an unknown or suppressed
        // short window must not be replaced by a healthy weekly percentage.
        return windows.Where(Limited).OrderByDescending(Weekly).ThenBy(w => w.Remaining).FirstOrDefault()
            ?? windows.Where(w => !Weekly(w)).OrderBy(w => w.Remaining is null)
                .ThenByDescending(w => w.Kind is "5h" or "primary_window").ThenBy(w => w.Seconds ?? int.MaxValue).FirstOrDefault()
            ?? (reported.All(Weekly) ? windows.FirstOrDefault(Weekly) : null);
    }
    public static string State(JsonElement quota)
    {
        if (quota.ValueKind == JsonValueKind.Object && quota.TryGetProperty("refreshError", out var error) && error.ValueKind == JsonValueKind.Object)
            return "历史缓存 · " + QuotaLabels.Failure(error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null);
        if (!Fresh(quota)) return "额度未知或已过期";
        if (quota.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
        {
            if (credits.TryGetProperty("unlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True) return "Credits 无额度上限";
            if (credits.TryGetProperty("balance", out var balance) && balance.ValueKind == JsonValueKind.Number) return "Credits " + balance.GetDecimal().ToString("0.##");
            if (credits.TryGetProperty("hasCredits", out var has) && has.ValueKind is JsonValueKind.True or JsonValueKind.False) return has.GetBoolean() ? "Credits 可用" : "Credits 不可用";
        }
        return quota.TryGetProperty("windowState", out var state) && state.GetString() == "not_reported" ? "未提供固定额度窗口" : "额度窗口未知";
    }
    public static bool AllowsContinuation(JsonElement quota) => Fresh(quota) &&
        (quota.TryGetProperty("allowed", out var allowed) && allowed.ValueKind == JsonValueKind.True
        || quota.TryGetProperty("creditsAvailable", out var credit) && credit.ValueKind == JsonValueKind.True);
    public static string Percent(DisplayQuotaWindow w) => w.Remaining is { } value ? value.ToString("0") + "%" : "—";
}
