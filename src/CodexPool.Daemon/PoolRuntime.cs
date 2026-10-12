using CodexPool.Core;

namespace CodexPool.Daemon;

// Coordinates durable active selection with the in-memory epoch. It never owns Codex state.
public sealed class PoolRuntime(Vault vault, OAuth auth, Quota quota, bool boundaryProbe)
{
    private readonly object gate = new();
    private readonly StickyScheduler scheduler = new(vault.ActiveId);
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
            var selection = scheduler.Select(Accounts(), Settings, DateTimeOffset.UtcNow);
            if (vault.ActiveId != selection.AccountId && !vault.ActivateForProbe(selection.AccountId)) throw new NoEligibleAccountException();
            return selection;
        }
    }
    public void Activate(string id)
    {
        lock (gate)
        {
            // Persist before changing the epoch: failed disk writes must not publish a new owner.
            if (!vault.ActivateForProbe(id)) throw new NoEligibleAccountException();
            scheduler.Activate(id, Accounts(), Settings, DateTimeOffset.UtcNow);
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
    public void Complete(AccountSelection lease, RelayResult result)
    {
        lock (gate)
        {
            if (result.AccountId != lease.AccountId)
                scheduler.CompleteFailover(lease, result.AccountId, "safe_precommit_failover", Accounts(), Settings, DateTimeOffset.UtcNow);
            if (scheduler.Active is { } active && vault.ActiveId != active) vault.ActivateForProbe(active);
            // After a rejected final attempt, only future requests may move to another account.
            if (result.Outcome is "quota_rejected" or "auth_rejected" && Settings.AutoSwitch)
                try { Select(); } catch (NoEligibleAccountException) { }
        }
    }
}
