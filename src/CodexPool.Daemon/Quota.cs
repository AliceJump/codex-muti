using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Net;

namespace CodexPool.Daemon;

public sealed record QuotaWindow(string Kind, int? WindowSeconds, double UsedPercent, double RemainingPercent, DateTimeOffset? ResetAt,
    string LimitId = "codex", string? Model = null, string? LimitName = null);
public sealed record QuotaCredits(bool HasCredits, bool Unlimited, decimal? Balance);
public sealed record QuotaRefreshFailure(string AccountId, string Code, DateTimeOffset At, int? Status = null, string? RecoveryCode = null);
public sealed record QuotaSnapshot(string AccountId, string Source, string? Plan, DateTimeOffset UpdatedAt, IReadOnlyList<QuotaWindow> Windows,
    bool? Allowed = null, bool? LimitReached = null, QuotaCredits? Credits = null, bool? SpendControlReached = null,
    string WindowState = "unknown", DateTimeOffset? RequestedAt = null, QuotaRefreshFailure? RefreshError = null)
{
    public bool Fresh(DateTimeOffset now) => RefreshError is null && now - UpdatedAt <= TimeSpan.FromMinutes(2) && UpdatedAt <= now.AddSeconds(30);
    public string CacheState => Fresh(DateTimeOffset.UtcNow) ? "current" : "historical";
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
    private readonly ConcurrentDictionary<string, DateTimeOffset> recoveryBackoff = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> renewalSyncAt = new();
    public QuotaRefreshFailure[] RefreshErrors => refreshFailures.Values.Where(x => vault.Find(x.AccountId) is not null).ToArray();
    public object View(string id) => Snapshot(id) is { } snapshot ? snapshot : new { accountId = id, available = false, reason = "quota_source_unverified", refreshError = refreshFailures.GetValueOrDefault(id) };
    public object[] Views => vault.Accounts.Select(x => View(x.Id)).ToArray();
    public QuotaSnapshot? Snapshot(string id) => snapshots.TryGetValue(id, out var snapshot)
        ? snapshot with { RefreshError = refreshFailures.GetValueOrDefault(id) } : null;
    public void Forget(string id) { snapshots.TryRemove(id, out _); refreshFailures.TryRemove(id, out _); recoveryBackoff.TryRemove(id, out _); renewalSyncAt.TryRemove(id, out _); }
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
                // RefreshAsync records only fixed classifications, never exception text.
            }
        }
    }
    public async Task<object> RefreshAsync(string id, CancellationToken cancellationToken, bool force = false)
    {
        using var use = vault.AcquireUse(id);
        var account = vault.Find(id) ?? throw new AuthFailure("account_not_found");
        if (account.ProviderKind != "native") return View(id); // Do not use SIWC grants against backend-api.
        var startedAt = DateTimeOffset.UtcNow;
        var singleFlight = locks.GetOrAdd(id, _ => new(1, 1)); await singleFlight.WaitAsync(cancellationToken);
        int? status = null; var recovering = false;
        try
        {
            // Concurrent callers share the result completed after they arrived.
            // A later explicit read can retry a failed query immediately.
            if (refreshFailures.TryGetValue(id, out var concurrentFailure) && concurrentFailure.At >= startedAt) return View(id);
            if (Snapshot(id) is { RefreshError: null } current && (current.UpdatedAt >= startedAt
                || !force && current.Fresh(DateTimeOffset.UtcNow) && DateTimeOffset.UtcNow - current.UpdatedAt < TimeSpan.FromSeconds(30))) return current;
            var beforeReady = account;
            account = await auth.ReadyAsync(id, cancellationToken);
            var preparedRefresh = account.UpdatedAt != beforeReady.UpdatedAt || account.AccessToken != beforeReady.AccessToken
                || account.RefreshToken != beforeReady.RefreshToken;
            var now = DateTimeOffset.UtcNow;
            var subscription = SubscriptionExpiry.Inspect(account.IdToken, account.IdTokenMetadataStale, now);
            // Manual refresh also synchronizes expired/retained ID-token metadata.
            // A newly issued token still reporting a past date is upstream lag;
            // rotating it again immediately cannot provide fresher evidence.
            // Unknown/missing metadata alone never triggers a rotation.
            if (force && !preparedRefresh && subscription.State == "stale"
                && (!subscription.TokenCurrent || subscription.ObservedAt <= now.AddMinutes(-15))
                && (!renewalSyncAt.TryGetValue(id, out var lastSync) || now - lastSync >= TimeSpan.FromMinutes(15)))
            {
                renewalSyncAt[id] = now;
                try
                {
                    account = await auth.ReadyAsync(id, cancellationToken, forceRefresh: true, rejectedCredential: account);
                    preparedRefresh = true;
                }
                catch (AuthFailure ex) when (ex.Code is "auth_temporarily_unavailable" or "refresh_not_yet_allowed")
                { account = await auth.ReadyAsync(id, cancellationToken); }
            }
            async Task<HttpResponseMessage> SendAsync(AccountCredential credential)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
                request.Headers.Authorization = new("Bearer", credential.AccessToken);
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.BackendAccountId);
                request.Headers.UserAgent.ParseAdd("codex_pool/0.1.0");
                return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            var requestedAt = DateTimeOffset.UtcNow;
            var response = await SendAsync(account);
            try
            {
                status = (int)response.StatusCode;
                // Only explicit 401 is authentication evidence. No recovery on policy,
                // rate limits, network failures or arbitrary upstream response bodies.
                if (response.StatusCode == HttpStatusCode.Unauthorized
                    && !(recoveryBackoff.TryGetValue(id, out var retryAt) && retryAt > DateTimeOffset.UtcNow))
                {
                    // Invalidate display/routing evidence immediately, including if
                    // the caller cancels while the durable rotation is completing.
                    refreshFailures[id] = new(id, "quota_unauthorized", DateTimeOffset.UtcNow, status);
                    response.Dispose(); recovering = true;
                    recoveryBackoff[id] = DateTimeOffset.UtcNow.AddMinutes(15);
                    if (!preparedRefresh)
                    {
                        account = await auth.ReadyAsync(id, cancellationToken, forceRefresh: true, rejectedCredential: account);
                        requestedAt = DateTimeOffset.UtcNow;
                        response = await SendAsync(account);
                        status = (int)response.StatusCode;
                    }
                    recovering = false;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var code = response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "quota_unauthorized", HttpStatusCode.Forbidden => "quota_forbidden",
                        HttpStatusCode.TooManyRequests => "quota_rate_limited", _ when status >= 500 => "quota_upstream_unavailable",
                        _ => "quota_http_error"
                    };
                    refreshFailures[id] = new(id, code, DateTimeOffset.UtcNow, status);
                    return View(id);
                }
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream(); var bytes = new byte[8192];
                while (true) { var count = await stream.ReadAsync(bytes, cancellationToken); if (count == 0) break;
                    if (buffer.Length + count > 512 * 1024) throw new AuthFailure("oversized_quota_response"); buffer.Write(bytes, 0, count); }
                var snapshot = Parse(id, buffer.ToArray()) with { RequestedAt = requestedAt }; vault.SaveQuota(snapshot); snapshots[id] = snapshot;
                refreshFailures.TryRemove(id, out _); recoveryBackoff.TryRemove(id, out _); return snapshot;
            }
            finally { response.Dispose(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is AuthFailure or HttpRequestException or JsonException or IOException
            or UnauthorizedAccessException or CryptographicException or OperationCanceledException)
        {
            var code = ex switch
            {
                AuthFailure a when a.Code is "unrecognized_quota_response" or "oversized_quota_response" => "invalid_quota_response",
                AuthFailure => "quota_auth_unavailable", HttpRequestException => "quota_network_unavailable",
                JsonException => "invalid_quota_response", OperationCanceledException => "quota_refresh_timeout",
                _ => "quota_storage_or_io_failed"
            };
            // Allowlist recovery diagnostics: AuthFailure can also originate from a
            // transport/test boundary, so arbitrary exception codes cannot escape.
            var recoveryCode = recovering && ex is AuthFailure failure && failure.Code is
                ("reauth_required" or "auth_temporarily_unavailable" or "refresh_not_yet_allowed" or "refresh_outcome_unknown"
                or "refresh_validation_timeout" or "rotated_token_persistence_failed" or "invalid_token_response"
                or "invalid_id_token" or "account_identity_mismatch") ? failure.Code : null;
            refreshFailures[id] = new(id, code, DateTimeOffset.UtcNow, status, recoveryCode);
            throw;
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
