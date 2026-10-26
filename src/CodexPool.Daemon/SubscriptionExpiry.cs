using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexPool.Core;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CodexPool.Daemon;

// Display metadata only, read from the daemon-owned ID token after login validation.
// Deriving this from the stored token also covers existing accounts and refreshes.
public static class SubscriptionExpiry
{
    public sealed record Metadata(DateTimeOffset? ActiveUntil, string State, DateTimeOffset? ObservedAt, bool TokenCurrent = false, string? Reason = null);
    public static Metadata Inspect(string idToken, bool stale = false, DateTimeOffset? now = null)
    {
        var until = FromIdToken(idToken);
        try
        {
            var jwt = new JsonWebToken(idToken);
            var observedAt = jwt.IssuedAt == DateTime.MinValue ? (DateTimeOffset?)null : new DateTimeOffset(jwt.IssuedAt, TimeSpan.Zero);
            var checkedAt = now ?? DateTimeOffset.UtcNow;
            var tokenCurrent = !stale && jwt.ValidTo > checkedAt.UtcDateTime;
            if (until is null) return new(null, "not_reported", observedAt, tokenCurrent, "upstream_not_reported");
            if (!tokenCurrent || until <= checkedAt)
                return new(null, "stale", observedAt, tokenCurrent, stale ? "id_token_not_refreshed" : !tokenCurrent ? "id_token_expired" : "deadline_in_past");
            return new(until, "current", observedAt, tokenCurrent);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        { return new(null, "unknown", null); }
    }
    public static DateTimeOffset? ActiveUntil(string idToken, DateTimeOffset now) => Inspect(idToken, now: now).ActiveUntil;
    public static string DeadlineState(string idToken, DateTimeOffset now) => Inspect(idToken, now: now).State switch
    { "current" => "reported", "not_reported" => "unknown", var state => state };
    public static string? PlanFromIdToken(string idToken)
    {
        try
        {
            var jwt = new JsonWebToken(idToken);
            if (!jwt.TryGetClaim("https://api.openai.com/auth", out var metadata)) return null;
            using var json = JsonDocument.Parse(metadata.Value);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("chatgpt_plan_type", out var plan) && plan.ValueKind == JsonValueKind.String
                && plan.GetString() is { } value && Regex.IsMatch(value, "^[a-zA-Z0-9_-]{1,80}$") && ModelErrors.ValidPlan(value) ? value : null;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or InvalidOperationException)
        { return null; }
    }
    public static DateTimeOffset? FromIdToken(string idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;
        try
        {
            var jwt = new JsonWebToken(idToken);
            if (!jwt.TryGetClaim("https://api.openai.com/auth", out var metadata)) return null;
            using var json = JsonDocument.Parse(metadata.Value);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("chatgpt_subscription_active_until", out var until)) return null;
            if (until.ValueKind == JsonValueKind.Number && until.TryGetInt64(out var seconds))
                return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
            if (until.ValueKind == JsonValueKind.String && until.GetString() is { } value)
            {
                if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
                    return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
                // Require a wire timestamp with an explicit offset, never the daemon's local zone.
                if (until.TryGetDateTimeOffset(out var at)
                    && (value.EndsWith('Z') || value.LastIndexOf('+') > 9 || value.LastIndexOf('-') > 9))
                    return at.ToUniversalTime();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or InvalidOperationException or OverflowException)
        { /* Optional metadata must not break account status or expose token contents. */ }
        return null;
    }
}
