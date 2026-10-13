using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexPool.Daemon;

public sealed record QuotaWindow(string Kind, int WindowSeconds, double UsedPercent, double RemainingPercent, DateTimeOffset ResetAt);
public sealed record QuotaSnapshot(string AccountId, string Source, string? Plan, DateTimeOffset UpdatedAt, IReadOnlyList<QuotaWindow> Windows);
public sealed class Quota(HttpClient http, OAuth auth, Vault vault)
{
    private readonly ConcurrentDictionary<string, QuotaSnapshot> snapshots = new(vault.Quotas.Select(x => new KeyValuePair<string, QuotaSnapshot>(x.AccountId, x)));
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();
    public object View(string id) => snapshots.TryGetValue(id, out var snapshot) ? snapshot : new { accountId = id, available = false, reason = "quota_source_unverified" };
    public object[] Views => vault.Accounts.Select(x => View(x.Id)).ToArray();
    public QuotaSnapshot? Snapshot(string id) => snapshots.GetValueOrDefault(id);
    public void Forget(string id) => snapshots.TryRemove(id, out _);
    public async Task<object> RefreshAsync(string id, CancellationToken cancellationToken)
    {
        using var use = vault.AcquireUse(id);
        var account = vault.Find(id) ?? throw new AuthFailure("account_not_found");
        if (account.ProviderKind != "native") return View(id); // Do not use SIWC grants against backend-api.
        var singleFlight = locks.GetOrAdd(id, _ => new(1, 1)); await singleFlight.WaitAsync(cancellationToken);
        try
        {
            if (snapshots.TryGetValue(id, out var current) && DateTimeOffset.UtcNow - current.UpdatedAt < TimeSpan.FromSeconds(30)) return current;
            account = await auth.ReadyAsync(id, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Authorization = new("Bearer", account.AccessToken);
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account.BackendAccountId);
            request.Headers.UserAgent.ParseAdd("codex_pool/0.1.0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return new { accountId = id, available = false, reason = "native_quota_unavailable", status = (int)response.StatusCode };
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream(); var bytes = new byte[8192];
            while (true) { var count = await stream.ReadAsync(bytes, cancellationToken); if (count == 0) break;
                if (buffer.Length + count > 512 * 1024) throw new AuthFailure("oversized_quota_response"); buffer.Write(bytes, 0, count); }
            var snapshot = Parse(id, buffer.ToArray()); vault.SaveQuota(snapshot); snapshots[id] = snapshot; return snapshot;
        }
        finally { singleFlight.Release(); }
    }
    public static QuotaSnapshot Parse(string id, ReadOnlyMemory<byte> payload)
    {
        using var json = JsonDocument.Parse(payload); var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new AuthFailure("unrecognized_quota_response");
        string? plan = null;
        if (root.TryGetProperty("plan_type", out var planValue) && planValue.ValueKind == JsonValueKind.String
            && Regex.IsMatch(planValue.GetString()!, "^[a-zA-Z0-9_-]{1,80}$")) plan = planValue.GetString();
        var windows = new List<QuotaWindow>();
        if (root.TryGetProperty("rate_limit", out var limit) && limit.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in new[] { "primary_window", "secondary_window" })
            {
                if (!limit.TryGetProperty(field, out var window) || window.ValueKind != JsonValueKind.Object) continue;
                if (!window.TryGetProperty("used_percent", out var usedValue) || usedValue.ValueKind != JsonValueKind.Number || !usedValue.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0 || used > 100
                    || !window.TryGetProperty("limit_window_seconds", out var durationValue) || durationValue.ValueKind != JsonValueKind.Number || !durationValue.TryGetInt32(out var duration) || duration <= 0
                    || !window.TryGetProperty("reset_at", out var resetValue) || resetValue.ValueKind != JsonValueKind.Number || !resetValue.TryGetInt64(out var reset)) continue;
                if (reset < 0 || reset > 253402300799) continue;
                windows.Add(new(duration == 18000 ? "5h" : duration == 604800 ? "weekly" : field, duration, used, 100 - used, DateTimeOffset.FromUnixTimeSeconds(reset)));
            }
        }
        return new(id, "native_codex_usage", plan, DateTimeOffset.UtcNow, windows);
    }
}
