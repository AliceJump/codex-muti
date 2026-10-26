using System.Globalization;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CodexPool.Daemon;

// Display metadata only, read from the daemon-owned ID token after login validation.
// Deriving this from the stored token also covers existing accounts and refreshes.
public static class SubscriptionExpiry
{
    public sealed record Metadata(DateTimeOffset? ActiveUntil, string State, DateTimeOffset? ObservedAt);
    public static Metadata Inspect(string idToken, bool stale = false)
    {
        var until = FromIdToken(idToken);
        try
        {
            var jwt = new JsonWebToken(idToken);
            var observedAt = jwt.IssuedAt == DateTime.MinValue ? (DateTimeOffset?)null : new DateTimeOffset(jwt.IssuedAt, TimeSpan.Zero);
            if (until is null) return new(null, "not_reported", observedAt);
            if (stale || jwt.ValidTo <= DateTime.UtcNow || until <= DateTimeOffset.UtcNow)
                return new(null, "stale", observedAt);
            return new(until, "current", observedAt);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        { return new(null, "unknown", null); }
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
