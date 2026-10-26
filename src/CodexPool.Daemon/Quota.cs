using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace CodexPool.Daemon;

public sealed record QuotaWindow(string Kind, int? WindowSeconds, double UsedPercent, double RemainingPercent, DateTimeOffset? ResetAt,
    string LimitId = "codex", string? Model = null, string? LimitName = null);
public sealed record QuotaCredits(bool HasCredits, bool Unlimited, decimal? Balance);
public sealed record QuotaRefreshFailure(string AccountId, string Code, DateTimeOffset At);
public sealed record QuotaSnapshot(string AccountId, string Source, string? Plan, DateTimeOffset UpdatedAt, IReadOnlyList<QuotaWindow> Windows,
    bool? Allowed = null, bool? LimitReached = null, QuotaCredits? Credits = null, bool? SpendControlReached = null,
    string WindowState = "unknown", DateTimeOffset? RequestedAt = null)
{
    public bool Fresh(DateTimeOffset now) => now - UpdatedAt <= TimeSpan.FromMinutes(2) && UpdatedAt <= now.AddSeconds(30);
    // A response to a query already in flight when rejection occurred is not recovery evidence.
    public bool ConfirmsRecovery(DateTimeOffset rejectedAt, DateTimeOffset now) => Fresh(now)
        && RequestedAt > rejectedAt && RequestedAt <= UpdatedAt && Allowed == true && LimitReached != true
        && SpendControlReached != true && WindowState is "reported" or "not_reported"
        && Windows.All(w => w.ResetAt > now && w.UsedPercent < 100);
    public IEnumerable<QuotaWindow> Applicable(string? model) => Windows.Where(w => w.LimitId == "codex" || model is not null && w.Model == model);
    public bool CreditsAvailable => Credits is { HasCredits: true } or { Unlimited: true };
    public IEnumerable<QuotaWindow> RoutingWindows(string? model, DateTimeOffset now) => Applicable(model).Where(w => w.ResetAt > now
        && !(w.LimitId == "codex" && w.UsedPercent >= 100 && (Allowed == true || CreditsAvailable)));
}
public sealed class Quota(HttpClient http, OAuth auth, Vault vault)
{
    private readonly ConcurrentDictionary<string, QuotaSnapshot> snapshots = new(vault.Quotas.Select(x => new KeyValuePair<string, QuotaSnapshot>(x.AccountId, x)));
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();
    private readonly ConcurrentDictionary<string, QuotaRefreshFailure> refreshFailures = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> renewalSyncAt = new(StringComparer.Ordinal);
    public QuotaRefreshFailure[] RefreshErrors => refreshFailures.Values.Where(x => vault.Find(x.AccountId) is not null).ToArray();
    public object View(string id) => snapshots.TryGetValue(id, out var snapshot) ? snapshot : new { accountId = id, available = false, reason = "quota_source_unverified" };
    public object[] Views => vault.Accounts.Select(x => View(x.Id)).ToArray();
    public QuotaSnapshot? Snapshot(string id) => snapshots.GetValueOrDefault(id);
    public void Forget(string id) { snapshots.TryRemove(id, out _); refreshFailures.TryRemove(id, out _); renewalSyncAt.TryRemove(id, out _); }
    public async Task RefreshAllAsync(IEnumerable<string> accountIds, CancellationToken cancellationToken, bool force = false)
    {
        foreach (var id in accountIds.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await RefreshAsync(id, cancellationToken, force); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (AuthFailure ex) when (ex.Code == "account_not_found") { Forget(id); }
            catch (Exception ex) when (ex is AuthFailure or HttpRequestException or JsonException or IOException
                or UnauthorizedAccessException or CryptographicException or OperationCanceledException)
            {
                // Only fixed classifications leave this boundary; never exception
                // text, token payloads or upstream response bodies.
                var code = ex switch
                {
                    AuthFailure => "quota_auth_unavailable", HttpRequestException => "quota_network_unavailable",
                    JsonException => "invalid_quota_response", OperationCanceledException => "quota_refresh_timeout",
                    _ => "quota_storage_or_io_failed"
                };
                refreshFailures[id] = new(id, code, DateTimeOffset.UtcNow);
            }
        }
    }
    public async Task<object> RefreshAsync(string id, CancellationToken cancellationToken, bool force = false)
    {
        using var use = vault.AcquireUse(id);
        var account = vault.Find(id) ?? throw new AuthFailure("account_not_found");
        if (account.ProviderKind != "native") return View(id); // Do not use SIWC grants against backend-api.
        var singleFlight = locks.GetOrAdd(id, _ => new(1, 1)); await singleFlight.WaitAsync(cancellationToken);
        try
        {
            if (!force && snapshots.TryGetValue(id, out var current) && DateTimeOffset.UtcNow - current.UpdatedAt < TimeSpan.FromSeconds(30)) return current;
            account = await ReadyForQuotaAsync(id, account, force, cancellationToken);
            var first = await QueryUsageAsync(id, account, cancellationToken);
            if (first.Response.IsSuccessStatusCode)
                return await CommitUsageAsync(id, first.Response, first.RequestedAt, cancellationToken);

            var status = first.Response.StatusCode;
            first.Response.Dispose();
            // Auth failure may mean a still-valid local access token was rejected after renew/revoke.
            // Recover credentials once under the same single-flight lock, then retry usage once.
            if (status == HttpStatusCode.Unauthorized)
            {
                try { account = await auth.ReadyAsync(id, cancellationToken, forceRefresh: true); }
                catch (AuthFailure ex)
                {
                    var authCode = ex.Code is "reauth_required" or "refresh_outcome_unknown" or "rotated_token_persistence_failed"
                        or "account_identity_mismatch" or "refresh_validation_timeout" ? "quota_auth_unavailable" : "quota_auth_temporarily_unavailable";
                    return Fail(id, authCode, status);
                }
                var retry = await QueryUsageAsync(id, account, cancellationToken);
                if (retry.Response.IsSuccessStatusCode)
                    return await CommitUsageAsync(id, retry.Response, retry.RequestedAt, cancellationToken);
                status = retry.Response.StatusCode;
                retry.Response.Dispose();
                return Fail(id, ClassifyHttp(status), status);
            }
            return Fail(id, ClassifyHttp(status), status);
        }
        finally { singleFlight.Release(); }
    }
    private async Task<AccountCredential> ReadyForQuotaAsync(string id, AccountCredential account, bool force, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        // Only a past reported deadline triggers renewal sync. Missing/unknown claims stay
        // pending without inventing a refresh; 401 recovery still covers auth failures.
        var staleSubscription = SubscriptionExpiry.DeadlineState(account.IdToken, now) == "stale";
        // Manual refresh may perform one bounded renewal sync so an upstream Plus renew
        // can replace a stale ID-token deadline/plan without waiting for access-token expiry.
        var allowRenewalSync = force && staleSubscription
            && (!renewalSyncAt.TryGetValue(id, out var last) || now - last >= TimeSpan.FromMinutes(15));
        if (allowRenewalSync)
        {
            renewalSyncAt[id] = now;
            try { return await auth.ReadyAsync(id, cancellationToken, forceRefresh: true); }
            catch (AuthFailure ex) when (ex.Code is "auth_temporarily_unavailable" or "refresh_not_yet_allowed")
            { return await auth.ReadyAsync(id, cancellationToken); }
        }
        return await auth.ReadyAsync(id, cancellationToken);
    }
    private async Task<(HttpResponseMessage Response, DateTimeOffset RequestedAt)> QueryUsageAsync(string _, AccountCredential account, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new("Bearer", account.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account.BackendAccountId);
        request.Headers.UserAgent.ParseAdd("codex_pool/0.1.0");
        var requestedAt = DateTimeOffset.UtcNow;
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return (response, requestedAt);
    }
    private async Task<object> CommitUsageAsync(string id, HttpResponseMessage response, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream(); var bytes = new byte[8192];
            while (true) { var count = await stream.ReadAsync(bytes, cancellationToken); if (count == 0) break;
                if (buffer.Length + count > 512 * 1024) throw new AuthFailure("oversized_quota_response"); buffer.Write(bytes, 0, count); }
            var snapshot = Parse(id, buffer.ToArray()) with { RequestedAt = requestedAt }; vault.SaveQuota(snapshot); snapshots[id] = snapshot;
            refreshFailures.TryRemove(id, out _); return snapshot;
        }
    }
    private object Fail(string id, string code, HttpStatusCode status)
    {
        // Keep the last successful snapshot as historical cache; only the error lane changes.
        refreshFailures[id] = new(id, code, DateTimeOffset.UtcNow);
        return new { accountId = id, available = false, reason = code, status = (int)status,
            cached = snapshots.ContainsKey(id) };
    }
    private static string ClassifyHttp(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "quota_auth_rejected",
        HttpStatusCode.Forbidden => "quota_forbidden",
        HttpStatusCode.TooManyRequests => "quota_rate_limited",
        _ => "native_quota_unavailable"
    };
    public static QuotaSnapshot Parse(string id, ReadOnlyMemory<byte> payload)
    {
        using var json = JsonDocument.Parse(payload); var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new AuthFailure("unrecognized_quota_response");
        string? plan = null;
        if (root.TryGetProperty("plan_type", out var planValue) && planValue.ValueKind == JsonValueKind.String
            && Regex.IsMatch(planValue.GetString()!, "^[a-zA-Z0-9_-]{1,80}$")) plan = planValue.GetString();
        static bool? Bool(JsonElement value, string field) => value.TryGetProperty(field, out var item)
            && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : null;
        static string? SafeName(JsonElement value, string field) => value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String
            && Regex.IsMatch(item.GetString()!, "^[a-zA-Z0-9._:/-]{1,128}$") ? item.GetString() : null;
        var windows = new List<QuotaWindow>(); var malformed = false;
        void AddWindows(JsonElement limit, string limitId, string? model = null, string? name = null)
        {
            foreach (var field in limit.EnumerateObject().Where(p => Regex.IsMatch(p.Name, "^[a-zA-Z0-9_]{1,64}_window$")).Take(64).Select(p => p.Name))
            {
                if (!limit.TryGetProperty(field, out var window) || window.ValueKind == JsonValueKind.Null) continue;
                if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("used_percent", out var usedValue)
                    || usedValue.ValueKind != JsonValueKind.Number || !usedValue.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0 || used > 100)
                { malformed = true; continue; }
                int? duration = null; DateTimeOffset? reset = null;
                if (window.TryGetProperty("limit_window_seconds", out var durationValue) && durationValue.ValueKind != JsonValueKind.Null)
                {
                    if (durationValue.ValueKind != JsonValueKind.Number || !durationValue.TryGetInt32(out var seconds) || seconds <= 0)
                    { malformed = true; continue; }
                    duration = seconds;
                }
                if (window.TryGetProperty("reset_at", out var resetValue) && resetValue.ValueKind != JsonValueKind.Null)
                {
                    if (resetValue.ValueKind != JsonValueKind.Number || !resetValue.TryGetInt64(out var epoch) || epoch < 0 || epoch > 253402300799)
                    { malformed = true; continue; }
                    reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
                }
                windows.Add(new(duration == 18000 ? "5h" : duration == 604800 ? "weekly" : field, duration, used, 100 - used, reset, limitId, model, name));
            }
        }
        bool? allowed = null; bool? reached = null;
        if (root.TryGetProperty("rate_limit", out var baseLimit) && baseLimit.ValueKind == JsonValueKind.Object)
        { allowed = Bool(baseLimit, "allowed"); reached = Bool(baseLimit, "limit_reached"); AddWindows(baseLimit, "codex"); }
        if (root.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var family in additional.EnumerateArray().Take(64))
            {
                if (family.ValueKind != JsonValueKind.Object || !family.TryGetProperty("rate_limit", out var limit) || limit.ValueKind != JsonValueKind.Object) continue;
                // Preserve a distinct identity even if upstream repeats a feature name.
                AddWindows(limit, "additional_" + index++, SafeName(family, "normal_model_slug") ?? SafeName(family, "normal_model"), SafeName(family, "limit_name") ?? SafeName(family, "metered_feature"));
            }
        }
        QuotaCredits? credits = null;
        if (root.TryGetProperty("credits", out var creditValue) && creditValue.ValueKind == JsonValueKind.Object
            && Bool(creditValue, "has_credits") is { } hasCredits && Bool(creditValue, "unlimited") is { } unlimited)
        {
            decimal? balance = null;
            if (creditValue.TryGetProperty("balance", out var amount))
            {
                if (amount.ValueKind == JsonValueKind.String && decimal.TryParse(amount.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var textBalance) && textBalance >= 0) balance = textBalance;
                else if (amount.ValueKind == JsonValueKind.Number && amount.TryGetDecimal(out var numberBalance) && numberBalance >= 0) balance = numberBalance;
            }
            credits = new(hasCredits, unlimited, balance);
        }
        var spendReached = root.TryGetProperty("spend_control", out var spend) && spend.ValueKind == JsonValueKind.Object ? Bool(spend, "reached") : null;
        // A missing window is never evidence of unlimited use. Only explicit availability makes a no-window response usable as such.
        var state = windows.Count > 0 ? "reported" : malformed ? "unknown" : allowed == true || credits is { HasCredits: true } or { Unlimited: true } ? "not_reported" : "unknown";
        return new(id, "native_codex_usage", plan, DateTimeOffset.UtcNow, windows, allowed, reached, credits, spendReached, state);
    }
}
