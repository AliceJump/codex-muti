using CodexPool.Core;

namespace CodexPool.Daemon;

// Coordinates durable active selection with the in-memory epoch. It never owns Codex state.
public sealed class PoolRuntime(Vault vault, OAuth auth, Quota quota, bool boundaryProbe, Action<AccountSwitch>? recordSwitch = null)
{
    private readonly object gate = new();
    private long requestsStarted;
    private string? quotaSelectionError;
    public string? QuotaSelectionError { get { lock (gate) return quotaSelectionError; } }
    private readonly Dictionary<string, (ModelError Error, DateTimeOffset Until, DateTimeOffset ObservedAt, Guid AuthorizationVersion)> failures = new(StringComparer.Ordinal);
    // Pre-metadata versions persisted quota/auth cooldowns without their cause. Retain the
    // old 30-second auth backoff, then require a successful query begun after this boundary.
    private readonly DateTimeOffset legacyRecoveryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
    private readonly Dictionary<string, DateTimeOffset> legacyCooldowns = vault.Accounts
        .Where(x => x.ProviderKind == "native" && x.CooldownUntil > DateTimeOffset.UtcNow
            && x.CooldownErrorCode is null && x.CooldownObservedAt is null)
        .ToDictionary(x => x.Id, x => x.CooldownUntil!.Value, StringComparer.Ordinal);
    private readonly StickyScheduler scheduler = new(vault.ActiveId, id =>
    { if (!vault.ActivateForProbe(id, auth.RoutingStatus(id) == "refreshing")) throw new NoEligibleAccountException(); }, recordSwitch);
    public PoolSettings Settings => boundaryProbe ? vault.Settings : vault.Settings with { AutoSwitch = false };
    public AccountSwitch[] History => scheduler.History;
    public string? Active => scheduler.Active;
    public AccountAvailability[] Availability(string? model = null) => Availability(model, waitForRefresh: false);
    private AccountAvailability[] Availability(string? model, bool waitForRefresh)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            return vault.Accounts.Select(x =>
            {
                var usage = quota.Snapshot(x.Id);
                var authStatus = auth.RoutingStatus(x.Id);
                if (failures.TryGetValue(x.Id, out var previous) && previous.AuthorizationVersion != x.AuthorizationVersion
                    && previous.Error.FailoverReason is null or "auth_rejected") failures.Remove(x.Id);
                var fresh = usage?.Fresh(now) == true;
                var exhausted = fresh ? usage!.RoutingWindows(model, now).Where(w => w.UsedPercent >= 100).OrderByDescending(w => w.ResetAt).FirstOrDefault() : null;
                var allowanceCurrent = fresh && !usage!.Windows.Any(w => w.LimitId == "codex" && w.ResetAt <= now);
                var denied = fresh && usage!.SpendControlReached == true || allowanceCurrent && !usage!.CreditsAvailable
                    && (usage.Allowed == false || usage.LimitReached == true && usage.Allowed != true);
                var reason = !x.Enabled ? "disabled" : x.Paused ? "paused" : x.Draining ? "draining"
                    : authStatus == "reauth_required" ? "auth_unavailable"
                    : authStatus == "refreshing" && !waitForRefresh ? "auth_temporarily_unavailable"
                    : exhausted is not null || denied ? "quota_exhausted" : null;
                string? code = usage?.SpendControlReached == true && fresh ? "insufficient_quota" : null;
                DateTimeOffset? reset = exhausted?.ResetAt; int? minutes = exhausted?.WindowSeconds / 60;
                if (failures.TryGetValue(x.Id, out var observed) && observed.Until > now) code = observed.Error.Code;
                if (reason is null && failures.TryGetValue(x.Id, out var failure) && failure.Until > now)
                {
                    reason = failure.Error.FailoverReason switch { "quota_rejected" => "quota_exhausted", "auth_rejected" => "auth_unavailable", "access_rejected" => "access_unavailable", _ => "auth_temporarily_unavailable" };
                    code = failure.Error.Code;
                    if (failure.Error.ResetsAt is { } seconds) reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    minutes ??= failure.Error.WindowMinutes;
                }
                if (reason is null)
                {
                    var candidate = new PoolAccount(x.Id, x.Enabled, x.Paused, x.Draining, true, x.CooldownUntil,
                        QuotaUpdatedAt: usage?.UpdatedAt, Windows: fresh ? RoutingWindows(usage!, model, now) : []);
                    reason = StickyScheduler.ExclusionReason(candidate, Settings, now);
                }
                return new AccountAvailability(x.Id, reason, code, reset, usage?.Plan, minutes);
            }).ToArray();
        }
    }
    public ModelError Unavailable(ModelError? last = null, IReadOnlyCollection<string>? requestAccounts = null, bool? autoSwitch = null, string? model = null)
    {
        lock (gate)
        {
            var firstId = requestAccounts?.FirstOrDefault() ?? scheduler.Active;
            var provider = firstId is not null ? vault.Find(firstId)?.ProviderKind : vault.Selected?.ProviderKind;
            var registered = vault.Accounts.Where(x => provider is null || x.ProviderKind == provider).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            var all = Availability(model).Where(x => registered.Contains(x.AccountId)).ToArray();
            // A reservation controls which accounts may be sent this request. The final diagnosis
            // must also include accounts already excluded before reservation (auth/quota/threshold).
            var assessed = all;
            if (!(autoSwitch ?? Settings.AutoSwitch) && firstId is { } active)
            {
                if (all.Any(x => x.AccountId != active && x.Available))
                    return ModelError.Terminal("account_auto_switch_disabled", "当前账户不可用，请开启自动切换或在 Codex Pool 手动选择其他账户。");
            }
            var aggregate = PoolAvailability.Aggregate(assessed);
            // Retain the precise homogeneous balance/plan error, but never label mixed failures as quota.
            if (last is not null && assessed.Length > 0 && assessed.All(x => x.ErrorCode == last.Code) && aggregate.Status == 429) return last;
            return aggregate;
        }
    }
    public void RecordFailure(string id, ModelError error, Guid? authorizationVersion = null)
    {
        if (error.FailoverReason is null) return;
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var account = vault.Find(id);
            if (account is null || error.FailoverReason == "auth_rejected" && authorizationVersion is { } version && account.AuthorizationVersion != version) return;
            legacyCooldowns.Remove(id);
            var until = error.ResetsAt is { } reset ? DateTimeOffset.FromUnixTimeSeconds(reset) : now.AddMinutes(1);
            var usage = quota.Snapshot(id);
            var cooldown = error.FailoverReason == "quota_rejected"
                ? error.ResetsAt is not null ? until : usage?.Windows.Where(x => x.UsedPercent >= 100 && x.ResetAt > now)
                    .Select(x => x.ResetAt).Max() ?? until
                : now.AddSeconds(30);
            // Keep the classification and durable cooldown atomic relative to quota reconciliation.
            if (vault.Cooldown(id, cooldown, error.Code, now, error.FailoverReason == "auth_rejected" ? account.AuthorizationVersion : null))
                failures[id] = (error, until, now, account.AuthorizationVersion);
        }
    }
    public void RecordSuccess(string id) { lock (gate) failures.Remove(id); }
    private static RoutingQuotaWindow[] RoutingWindows(QuotaSnapshot snapshot, string? model, DateTimeOffset now) =>
        snapshot.RoutingWindows(model, now).Select(w => new RoutingQuotaWindow(w.WindowSeconds, w.UsedPercent, w.ResetAt)).ToArray();
    private PoolAccount[] Accounts(string? model = null)
    {
        var availabilityById = Availability(model, waitForRefresh: true).ToDictionary(x => x.AccountId, StringComparer.Ordinal);
        return vault.Accounts.Select(x =>
        {
            if (!availabilityById.TryGetValue(x.Id, out var availability)) availability = new(x.Id, null);
            var usage = quota.Snapshot(x.Id);
            var cooldown = availability.Reason is "quota_exhausted" or "auth_temporarily_unavailable"
                ? availability.ResetAt ?? (failures.TryGetValue(x.Id, out var failure) ? failure.Until : DateTimeOffset.UtcNow.AddMinutes(1)) : x.CooldownUntil;
            return new PoolAccount(x.Id, x.Enabled, x.Paused, x.Draining,
                availability.Reason is not ("auth_unavailable" or "access_unavailable" or "configuration_unavailable"),
                cooldown, QuotaUpdatedAt: usage?.UpdatedAt, Windows: usage?.Fresh(DateTimeOffset.UtcNow) == true ? RoutingWindows(usage, model, DateTimeOffset.UtcNow) : []);
        }).ToArray();
    }
    public AccountSelection Select(string? model = null)
    {
        lock (gate)
        {
            var selection = scheduler.Select(RoutingAccounts(model), Settings, DateTimeOffset.UtcNow);
            return selection;
        }
    }
    private PoolAccount[] RoutingAccounts(string? model)
    {
        var provider = scheduler.Active is { } current ? vault.Find(current)?.ProviderKind : null;
        return Accounts(model).Where(x => provider is null || vault.Find(x.Id)?.ProviderKind == provider).ToArray();
    }
    public void SelectAfterQuotaRefresh()
    {
        lock (gate)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var failedRefreshes = quota.RefreshErrors.Select(x => x.AccountId).ToHashSet(StringComparer.Ordinal);
                foreach (var account in vault.Accounts)
                {
                    if (failedRefreshes.Contains(account.Id) || quota.Snapshot(account.Id) is not { } snapshot) continue;
                    if (legacyCooldowns.TryGetValue(account.Id, out var legacyUntil) && account.CooldownUntil == legacyUntil
                        && account.CooldownErrorCode is null && account.CooldownObservedAt is null
                        && account.AuthStatus == "ready" && auth.Health(account.Id) != "reauth_required"
                        && !failures.ContainsKey(account.Id) && snapshot.ConfirmsRecovery(legacyRecoveryAfter, now))
                    {
                        vault.ClearLegacyCooldown(account.Id, legacyUntil);
                        legacyCooldowns.Remove(account.Id);
                    }
                    if (ModelErrors.IsUsageCode(account.CooldownErrorCode) && account.CooldownObservedAt is { } observedAt
                        && snapshot.ConfirmsRecovery(observedAt, now))
                        vault.ClearQuotaCooldown(account.Id, observedAt);
                    if (failures.TryGetValue(account.Id, out var failure) && ModelErrors.IsUsageCode(failure.Error.Code)
                        && snapshot.ConfirmsRecovery(failure.ObservedAt, now)) failures.Remove(account.Id);
                }
                if (Settings.AutoSwitch) Select();
                quotaSelectionError = null;
            }
            catch (NoEligibleAccountException) { quotaSelectionError = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            { quotaSelectionError = "quota_selection_persistence_failed"; }
        }
    }
    public void Activate(string id)
    {
        lock (gate)
        {
            if (scheduler.Active != id && requestsStarted > 0 && !boundaryProbe) throw new AuthFailure("live_migration_gate_pending");
            scheduler.Activate(id, Accounts(), Settings, DateTimeOffset.UtcNow);
        }
    }
    public ModelReservation ReserveModelRequest(string? model = null)
    {
        lock (gate)
        {
            var selection = Select(model);
            var available = Availability(model, waitForRefresh: true).Where(x => x.Available).Select(x => x.AccountId).ToHashSet(StringComparer.Ordinal);
            var ids = Candidates(selection, model).Where(available.Contains).ToArray();
            if (ids.Length == 0) throw new NoEligibleAccountException();
            var uses = new List<IDisposable>();
            try
            {
                foreach (var id in ids) uses.Add(vault.AcquireUse(id, model: true));
                requestsStarted++;
                return new(selection, ids, uses, Settings.AutoSwitch);
            }
            catch { foreach (var use in uses) use.Dispose(); throw; }
        }
    }
    public async Task<ModelLease[]> PrepareAsync(ModelReservation reservation, CancellationToken token)
    {
        var ready = new List<ModelLease>();
        foreach (var id in reservation.CandidateIds)
        {
            token.ThrowIfCancellationRequested();
            var version = vault.Find(id)?.AuthorizationVersion;
            try
            {
                var credential = await auth.ReadyAsync(id, token);
                ready.Add(new(id, new(credential.ProviderKind == "native" ? "https://chatgpt.com/backend-api/codex/responses" : "https://api.openai.com/v1/responses"), credential.AccessToken, credential.BackendAccountId, credential.AuthorizationVersion));
            }
            catch (AuthFailure ex)
            {
                token.ThrowIfCancellationRequested();
                // An uncertain refresh outcome must remain visible, not authorize a request replay.
                if (ex.Code == "refresh_outcome_unknown")
                {
                    // A broken standby must not prevent an already-ready account from serving.
                    // With no ready account, the ambiguous failure does not permit failover.
                    if (ready.Count > 0) break;
                    throw;
                }
                if (ex.Code is "auth_temporarily_unavailable" or "refresh_not_yet_allowed")
                { lock (gate) failures[id] = (PoolAvailability.Auth(ex.Code), DateTimeOffset.UtcNow.AddSeconds(30), DateTimeOffset.UtcNow, version ?? Guid.Empty); }
                else if (vault.Find(id)?.AuthStatus == "reauth_required" || ex.Code is "reauth_required" or "account_identity_mismatch" or "rotated_token_persistence_failed")
                    RecordFailure(id, PoolAvailability.Auth("reauth_required") with { FailoverReason = "auth_rejected" }, version);
                else throw;
            }
        }
        if (ready.Count == 0) throw new NoEligibleAccountException();
        return ready.ToArray();
    }
    public ModelReservation ReserveCatalog()
    {
        lock (gate)
        {
            var selected = vault.Selected ?? throw new AuthFailure("no_authorized_account");
            var ids = vault.Accounts.Where(x => x.ProviderKind == selected.ProviderKind && (x.Id == selected.Id || x.Enabled && !x.Paused && !x.Draining))
                .OrderBy(x => x.Id == selected.Id ? 0 : 1).Select(x => x.Id).ToArray();
            var uses = new List<IDisposable>();
            try
            {
                foreach (var id in ids) uses.Add(vault.AcquireUse(id));
                // Catalog metadata can be read even when model quota is exhausted; no sticky/epoch changes.
                return new(new(selected.Id, -1, null), ids, uses, Settings.AutoSwitch);
            }
            catch { foreach (var use in uses) use.Dispose(); throw; }
        }
    }
    public bool Delete(string id, out string? newActiveId)
    {
        lock (gate)
        {
            var provider = vault.Find(id)?.ProviderKind;
            if (!vault.Delete(id, out newActiveId, boundaryProbe || requestsStarted == 0,
                x => Vault.IsEligible(x) && auth.Health(x.Id) != "reauth_required" && x.ProviderKind == provider)) return false;
            scheduler.AccountDeleted(id, newActiveId, DateTimeOffset.UtcNow);
            quota.Forget(id);
            failures.Remove(id);
            legacyCooldowns.Remove(id);
            return true;
        }
    }
    public void UpdateSettings(PoolSettings settings)
    {
        if (settings.AutoSwitch && !boundaryProbe) throw new AuthFailure("live_migration_gate_pending");
        lock (gate) vault.SaveSettings(settings);
    }
    public string[] Candidates(AccountSelection selection, string? model = null)
    {
        lock (gate)
        {
            if (!Settings.AutoSwitch)
                return [selection.AccountId];
            var provider = vault.Find(selection.AccountId)?.ProviderKind;
            return new[] { selection.AccountId }.Concat(Accounts(model).Where(x => x.Id != selection.AccountId
                && vault.Find(x.Id)?.ProviderKind == provider
                && StickyScheduler.ExclusionReason(x, Settings, DateTimeOffset.UtcNow) is null).Select(x => x.Id)).ToArray();
        }
    }
    public void Complete(AccountSelection lease, RelayResult result, string? failoverReason = null, string? model = null)
    {
        lock (gate)
        {
            if (result.AccountId != lease.AccountId)
                scheduler.CompleteFailover(lease, result.AccountId, failoverReason ?? "safe_precommit_failover", Accounts(model), Settings, DateTimeOffset.UtcNow);
            // After a rejected final attempt, only future requests may move to another account.
            if (result.Outcome is "quota_rejected" or "auth_rejected" or "access_rejected" && Settings.AutoSwitch)
                try { Select(model); } catch (NoEligibleAccountException) { }
        }
    }
}

public sealed class ModelReservation(AccountSelection selection, string[] candidateIds, List<IDisposable> uses, bool autoSwitch = false) : IDisposable
{
    public AccountSelection Selection { get; } = selection;
    public IReadOnlyList<string> CandidateIds { get; } = candidateIds;
    public bool AutoSwitch { get; } = autoSwitch;
    public void Dispose() { foreach (var use in uses) use.Dispose(); }
    public override string ToString() => "ModelReservation [REDACTED]";
}
