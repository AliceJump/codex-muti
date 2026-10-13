using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace CodexPool.Core;

public sealed record ModelError(int Status, string Type, string Code, string Message,
    string? FailoverReason = null, string? RetryAfter = null, string? Plan = null,
    long? ResetsAt = null, int? WindowMinutes = null)
{
    public byte[] Body() => JsonSerializer.SerializeToUtf8Bytes(new { error = new
    {
        type = Type, code = Code, message = Message,
        plan_type = Plan, resets_at = ResetsAt, limit_window_minutes = WindowMinutes
    } }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });
    public async Task SendAsync(IResponseSink sink, CancellationToken token)
    {
        if (RetryAfter is not null) sink.SetHeader("Retry-After", RetryAfter);
        sink.Start(Status, "application/json"); await sink.WriteAsync(Body(), token);
    }
    public static ModelError Terminal(string code, string message) => new(400, "invalid_request_error", code, message);
    public static ModelError Quota(string? plan = null, long? reset = null, int? minutes = null) =>
        new(429, "usage_limit_reached", "usage_limit_reached", "所有参与调度的账户均已没有可用模型额度。",
            Plan: plan, ResetsAt: reset, WindowMinutes: minutes);
}

// Only documented scalar protocol fields survive sanitization. No upstream messages/extra JSON/identity headers.
public static class ModelErrors
{
    public static async Task<ModelError> FromResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var bytes = new byte[8193]; var count = 0;
        try
        {
            while (count < bytes.Length)
            {
                var read = await input.ReadAsync(bytes.AsMemory(count), token); if (read == 0) break; count += read;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { count = 0; }
        var advice = response.Headers.TryGetValues("Retry-After", out var values) ? values.Take(2).ToArray() : [];
        return FromUpstream(response.StatusCode, count > 8192 ? ReadOnlyMemory<byte>.Empty : bytes.AsMemory(0, count), advice.Length == 1 ? advice[0] : null);
    }
    private static readonly HashSet<string> UsageCodes = new(StringComparer.Ordinal)
        { "usage_limit_reached", "quota_exhausted", "subscription_sharing_usage_limit_exceeded" };
    private static readonly HashSet<string> BalanceCodes = new(StringComparer.Ordinal)
        { "insufficient_quota", "credit_balance_exhausted", "organization_spend_limit_exceeded", "project_spend_limit_exceeded", "organization_usage_limit_exceeded" };
    private static readonly HashSet<string> AuthCodes = new(StringComparer.Ordinal)
        { "invalid_api_key", "invalid_token", "token_expired", "authentication_error", "unauthorized" };
    public static string? FailoverReason(string? code) => code is not null && (UsageCodes.Contains(code) || BalanceCodes.Contains(code))
        ? "quota_rejected" : code == "usage_not_included" ? "access_rejected" : code is not null && AuthCodes.Contains(code) ? "auth_rejected" : null;
    public static bool IsBalanceCode(string? code) => code is not null && BalanceCodes.Contains(code);

    public static ModelError FromUpstream(HttpStatusCode status, ReadOnlyMemory<byte> body, string? retryAfter)
    {
        var http = (int)status;
        var advice = http is 429 or 503 ? ValidateRetryAfter(retryAfter) : null;
        var unknown = new ModelError(http, "proxy_upstream_error", "unknown_rejection", "上游拒绝了模型请求。", RetryAfter: advice);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return unknown;
            foreach (var name in new[] { "code", "type" })
                if (error.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) return unknown;
            string? Text(string name) => error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var codes = new[] { Text("code"), Text("type") };
            if (http == 429)
            {
                if (codes.Any(x => x is not null && UsageCodes.Contains(x)))
                {
                    var plan = Text("plan_type");
                    if (!ValidPlan(plan)) plan = null;
                    long? reset = error.TryGetProperty("resets_at", out var time) && time.ValueKind == JsonValueKind.Number && time.TryGetInt64(out var seconds)
                        && seconds >= DateTimeOffset.UtcNow.ToUnixTimeSeconds() && seconds <= DateTimeOffset.UtcNow.AddDays(31).ToUnixTimeSeconds() ? seconds : null;
                    int? minutes = error.TryGetProperty("limit_window_minutes", out var window) && window.ValueKind == JsonValueKind.Number
                        && window.TryGetInt32(out var length) && length is > 0 and <= 65535 ? length : null;
                    return ModelError.Quota(plan, reset, minutes) with { FailoverReason = "quota_rejected" };
                }
                var balance = codes.FirstOrDefault(x => x is not null && BalanceCodes.Contains(x));
                if (balance is not null) return new(429, "insufficient_quota", balance, "该账户的模型额度或消费余额不足。", "quota_rejected");
                if (codes.Contains("usage_not_included")) return new(429, "usage_not_included", "usage_not_included", "该账户套餐不包含此模型的使用权限。", "access_rejected");
                if (codes.Contains("flex_unavailable")) return new(429, "flex_unavailable", "flex_unavailable", "Flex 模型容量暂不可用。");
                var rate = codes.FirstOrDefault(x => x is "rate_limit_exceeded" or "slow_down");
                if (rate is not null) return new(429, rate, rate, "触发了上游模型速率限制，请按服务器建议等待。", RetryAfter: advice);
            }
            if (http == 401 && codes.Any(x => x is not null && AuthCodes.Contains(x)))
                return ModelError.Terminal("account_auth_required", "上游池账户需要在 Codex Pool 中重新授权。") with { FailoverReason = "auth_rejected" };
            if (http == 503)
            {
                var temporary = codes.FirstOrDefault(x => x is "server_is_overloaded" or "slow_down");
                if (temporary is not null) return new(503, temporary, temporary, "上游模型服务暂时繁忙。", RetryAfter: advice);
            }
            if (http is 400 or 403)
            {
                var policy = codes.FirstOrDefault(x => x == "misalignment_policy_violation" || http == 400 && x is ("cyber_policy" or "bio_policy" or "invalid_prompt" or "context_length_exceeded"));
                if (policy is not null) return new(http, "invalid_request_error", policy, "上游根据输入或访问策略拒绝了该请求。");
            }
        }
        catch (JsonException) { }
        return unknown;
    }
    public static bool ValidPlan(string? value) => value is "free" or "go" or "plus" or "pro" or "team" or "business" or "enterprise" or "edu" or "education";
    public static string? ValidateRetryAfter(string? value)
    {
        if (value is null || value.Length > 64 || value.Any(char.IsControl)) return null;
        value = value.Trim();
        if (value.Length > 0 && value.All(char.IsAsciiDigit) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= 3600)
            return seconds.ToString(CultureInfo.InvariantCulture);
        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            && date >= DateTimeOffset.UtcNow && date <= DateTimeOffset.UtcNow.AddHours(1)) return date.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);
        return null;
    }
}
