using CodexPool.Core;

namespace CodexPool.Daemon;

// Coordinates durable active selection with the in-memory epoch. It never owns Codex state.
public sealed class PoolRuntime(Vault vault, OAuth auth, Quota quota, bool boundaryProbe)
{
    private readonly object gate = new();
    private long requestsStarted;
    private readonly StickyScheduler scheduler = new(vault.ActiveId, id =>
    { if (!vault.ActivateForProbe(id)) throw new NoEligibleAccountException(); });
    public PoolSettings Settings => boundaryProbe ? vault.Settings : vault.Settings with { AutoSwitch = false };
    public AccountSwitch[] History => scheduler.History;
    public string? Active => scheduler.Active;
    private PoolAccount[] Accounts() => vault.Accounts.Select(x =>
    {
        var usage = quota.Snapshot(x.Id);
        double? Used(string kind) => usage?.Windows.FirstOrDefault(w => w.Kind == kind && w.ResetAt > DateTimeOffset.UtcNow)?.UsedPercent;
        return new PoolAccount(x.Id, x.Enabled, x.Paused, x.Draining, x.AuthStatus == "ready" && auth.Health(x.Id) != "reauth_required",
            x.CooldownUntil, Used("5h"), Used("weekly"), usage?.UpdatedAt);
    }).ToArray();
    public AccountSelection Select()
    {
        lock (gate)
        {
            var provider = scheduler.Active is { } current ? vault.Find(current)?.ProviderKind : null;
            var accounts = Accounts().Where(x => provider is null || vault.Find(x.Id)?.ProviderKind == provider).ToArray();
            var selection = scheduler.Select(accounts, Settings, DateTimeOffset.UtcNow);
            return selection;
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
    public ModelReservation ReserveModelRequest()
    {
        lock (gate)
        {
            var selection = Select();
            var ids = Candidates(selection);
            var uses = new List<IDisposable>();
            try
            {
                foreach (var id in ids) uses.Add(vault.AcquireUse(id, model: true));
                requestsStarted++;
                return new(selection, ids, uses);
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
            return true;
        }
    }
    public void UpdateSettings(PoolSettings settings)
    {
        if (settings.AutoSwitch && !boundaryProbe) throw new AuthFailure("live_migration_gate_pending");
        lock (gate) vault.SaveSettings(settings);
    }
    public string[] Candidates(AccountSelection selection)
    {
        lock (gate)
        {
            if (!Settings.AutoSwitch) return [selection.AccountId];
            var provider = vault.Find(selection.AccountId)?.ProviderKind;
            return new[] { selection.AccountId }.Concat(Accounts().Where(x => x.Id != selection.AccountId
                && vault.Find(x.Id)?.ProviderKind == provider
                && StickyScheduler.ExclusionReason(x, Settings, DateTimeOffset.UtcNow) is null).Select(x => x.Id)).ToArray();
        }
    }
    public void Rejected(ModelLease lease, string reason)
    {
        // Only explicit structured quota/auth rejection reaches this callback; network errors never do.
        var usage = quota.Snapshot(lease.AccountId);
        var reset = usage?.Windows.Where(x => x.UsedPercent >= 100 && x.ResetAt > DateTimeOffset.UtcNow)
            .Select(x => (DateTimeOffset?)x.ResetAt).Max();
        vault.Cooldown(lease.AccountId, reason == "quota_rejected" ? reset ?? DateTimeOffset.UtcNow.AddMinutes(1) : DateTimeOffset.UtcNow.AddSeconds(30));
    }
    public void Complete(AccountSelection lease, RelayResult result, string? failoverReason = null)
    {
        lock (gate)
        {
            if (result.AccountId != lease.AccountId)
                scheduler.CompleteFailover(lease, result.AccountId, failoverReason ?? "safe_precommit_failover", Accounts(), Settings, DateTimeOffset.UtcNow);
            // After a rejected final attempt, only future requests may move to another account.
            if (result.Outcome is "quota_rejected" or "auth_rejected" && Settings.AutoSwitch)
                try { Select(); } catch (NoEligibleAccountException) { }
        }
    }
}

public sealed class ModelReservation(AccountSelection selection, string[] candidateIds, List<IDisposable> uses) : IDisposable
{
    public AccountSelection Selection { get; } = selection;
    public IReadOnlyList<string> CandidateIds { get; } = candidateIds;
    public void Dispose() { foreach (var use in uses) use.Dispose(); }
    public override string ToString() => "ModelReservation [REDACTED]";
}
