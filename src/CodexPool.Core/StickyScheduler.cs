namespace CodexPool.Core;

public sealed record PoolAccount(string Id, bool Enabled = true, bool Paused = false, bool Draining = false,
    bool AuthReady = true, DateTimeOffset? CooldownUntil = null, double? FiveHourUsed = null,
    double? WeeklyUsed = null, DateTimeOffset? QuotaUpdatedAt = null, IReadOnlyList<RoutingQuotaWindow>? Windows = null);
public sealed record RoutingQuotaWindow(int? WindowSeconds, double UsedPercent, DateTimeOffset? ResetAt);
public sealed record PoolSettings(bool AutoSwitch = false, double FiveHourThreshold = 95, double WeeklyThreshold = 95, double? WindowThreshold = null)
{
    // Preserve saved legacy values until the user saves the single common threshold.
    public double Threshold(int? seconds) => WindowThreshold ?? (seconds == 18000 ? FiveHourThreshold
        : seconds == 604800 ? WeeklyThreshold : Math.Min(FiveHourThreshold, WeeklyThreshold));
}
public sealed record AccountSelection(string AccountId, long Epoch, string? SwitchReason);
public sealed record AccountSwitch(DateTimeOffset At, string? From, string To, string Reason, long Epoch);
public sealed record CompactionAdvice(string AccountId, long Epoch, string Model);
public sealed record CompactionHold(string Id, string AccountId, long Epoch, string Model, DateTimeOffset ExpiresAt);
public sealed class CompactionRejectedException(string code) : Exception(code)
{ public string Code { get; } = code; }

public sealed class NoEligibleAccountException : Exception
{ public NoEligibleAccountException() : base("no_eligible_account") { } }

/// <summary>One global sticky owner. Selection and activation are atomic; no session/agent state.</summary>
public sealed class StickyScheduler(string? activeId, Action<string>? persistSelection = null)
{
    private readonly object gate = new();
    private string? active = activeId;
    private long epoch;
    private CompactionHold? compactionHold;
    private long? attemptedCompactionEpoch;
    private readonly Queue<AccountSwitch> history = new();
    public string? Active { get { lock (gate) return active; } }
    public AccountSelection Current { get { lock (gate) return new(active ?? "", epoch, null); } }
    public CompactionHold? Hold { get { lock (gate) return compactionHold; } }

    // One attempt per routing epoch: weekly windows warn two percentage points
    // before the switch threshold, other windows five. Unknown/stale quota cannot warn.
    public CompactionAdvice? AdviseCompaction(string model, IReadOnlyList<PoolAccount> accounts, PoolSettings settings, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!settings.AutoSwitch || attemptedCompactionEpoch == epoch || compactionHold is not null) return null;
            var account = accounts.FirstOrDefault(x => x.Id == active);
            if (account is null || !CanHold(account, now) || account.Windows is null
                || !account.Windows.Any(w => w.ResetAt > now && double.IsFinite(w.UsedPercent)
                    && w.UsedPercent >= Math.Max(0, settings.Threshold(w.WindowSeconds) - (w.WindowSeconds == 604800 ? 2 : 5)))) return null;
            if (!accounts.Any(x => x.Id != active && ExclusionReason(x, settings, now) is null)) return null;
            return new(account.Id, epoch, model);
        }
    }

    public CompactionHold ReserveCompaction(CompactionAdvice expected, IReadOnlyList<PoolAccount> accounts, PoolSettings settings, DateTimeOffset now)
    {
        lock (gate)
        {
            if (expected.AccountId != active || expected.Epoch != epoch) throw new CompactionRejectedException("selection_changed");
            if (AdviseCompaction(expected.Model, accounts, settings, now) is null) throw new CompactionRejectedException("compaction_not_available");
            attemptedCompactionEpoch = epoch;
            return compactionHold = new(Guid.NewGuid().ToString("N"), expected.AccountId, epoch, expected.Model, now.AddMinutes(3));
        }
    }

    public bool FinishCompaction(string id, bool completed, IReadOnlyList<PoolAccount> accounts, PoolSettings settings, DateTimeOffset now)
    {
        lock (gate)
        {
            if (compactionHold is not { } hold || hold.Id != id) return false;
            compactionHold = null;
            if (!completed || !settings.AutoSwitch || hold.ExpiresAt <= now || hold.AccountId != active || hold.Epoch != epoch
                || !accounts.Any(x => x.Id == hold.AccountId && CanHold(x, now))) return false;
            var next = accounts.FirstOrDefault(x => x.Id != active && ExclusionReason(x, settings, now) is null);
            if (next is null) return false;
            Switch(next.Id, "compaction_completed", now);
            return true;
        }
    }

    private static bool CanHold(PoolAccount account, DateTimeOffset now) => AdmissionReason(account, now) is null
        && account.QuotaUpdatedAt is { } updated && now - updated <= TimeSpan.FromMinutes(2) && updated <= now.AddSeconds(30)
        && account.Windows is { Count: > 0 } windows && !windows.Any(w => w.ResetAt > now
            && (!double.IsFinite(w.UsedPercent) || w.UsedPercent is < 0 or >= 100));
    public void AccountDeleted(string id, string? replacement, DateTimeOffset now)
    {
        lock (gate)
        {
            if (active != id) return;
            active = replacement; epoch++;
            compactionHold = null;
            if (replacement is not null)
            {
                history.Enqueue(new(now, id, replacement, "account_deleted", epoch));
                while (history.Count > 100) history.Dequeue();
            }
        }
    }
    public AccountSwitch[] History { get { lock (gate) return history.ToArray(); } }
    public AccountSelection Activate(string id, IReadOnlyList<PoolAccount> accounts, PoolSettings settings, DateTimeOffset now)
    {
        lock (gate)
        {
            var account = accounts.FirstOrDefault(x => x.Id == id);
            // Manual activation respects admission/auth, but thresholds only drive auto routing.
            if (account is null || AdmissionReason(account, now) is not null) throw new NoEligibleAccountException();
            Switch(id, "manual", now); return new(id, epoch, "manual");
        }
    }
    public AccountSelection Select(IReadOnlyList<PoolAccount> accounts, PoolSettings settings, DateTimeOffset now)
    {
        Validate(settings);
        lock (gate)
        {
            var current = accounts.FirstOrDefault(x => x.Id == active);
            if (compactionHold is { } hold)
            {
                if (settings.AutoSwitch && hold.ExpiresAt > now && hold.AccountId == active && hold.Epoch == epoch
                    && current is not null && CanHold(current, now)) return new(current.Id, epoch, null);
                compactionHold = null;
            }
            var reason = current is null ? "no_active_account" : ExclusionReason(current, settings, now);
            if (reason is null) return new(current!.Id, epoch, null);
            if (active is not null && !settings.AutoSwitch) throw new NoEligibleAccountException();
            var next = accounts.FirstOrDefault(x => ExclusionReason(x, settings, now) is null);
            if (next is null) throw new NoEligibleAccountException();
            Switch(next.Id, reason, now); return new(next.Id, epoch, reason);
        }
    }
    public void CompleteFailover(AccountSelection lease, string replacement, string reason, IReadOnlyList<PoolAccount> accounts, PoolSettings settings, DateTimeOffset now)
    {
        lock (gate)
        {
            // A late request cannot undo a manual command or newer automatic routing decision.
            if (active != lease.AccountId || epoch != lease.Epoch || !settings.AutoSwitch) return;
            if (!accounts.Any(x => x.Id == replacement && ExclusionReason(x, settings, now) is null)) return;
            Switch(replacement, reason, now);
        }
    }
    public static void Validate(PoolSettings settings)
    {
        if (!double.IsFinite(settings.FiveHourThreshold) || !double.IsFinite(settings.WeeklyThreshold)
            || settings.WindowThreshold is { } threshold && (!double.IsFinite(threshold) || threshold is <= 0 or > 100)
            || settings.FiveHourThreshold is <= 0 or > 100 || settings.WeeklyThreshold is <= 0 or > 100)
            throw new ArgumentException("Thresholds must be finite values greater than 0 and at most 100.");
    }
    public static string? ExclusionReason(PoolAccount account, PoolSettings settings, DateTimeOffset now)
    {
        var admission = AdmissionReason(account, now); if (admission is not null) return admission;
        if (!settings.AutoSwitch) return null;
        if (account.QuotaUpdatedAt is { } updated && now - updated <= TimeSpan.FromMinutes(2) && updated <= now.AddSeconds(30))
        {
            if (account.Windows is not null)
                return account.Windows.Any(w => double.IsFinite(w.UsedPercent) && w.UsedPercent is >= 0 and <= 100
                    && w.ResetAt > now && w.UsedPercent >= settings.Threshold(w.WindowSeconds)) ? "quota_threshold" : null;
            if (account.FiveHourUsed >= settings.FiveHourThreshold) return "5h_threshold";
            if (account.WeeklyUsed >= settings.WeeklyThreshold) return "weekly_threshold";
        }
        return null;
    }
    private static string? AdmissionReason(PoolAccount account, DateTimeOffset now) =>
        !account.Enabled ? "disabled" : account.Paused ? "paused" : account.Draining ? "draining"
        : !account.AuthReady ? "auth_unavailable" : account.CooldownUntil > now ? "cooldown" : null;
    private void Switch(string replacement, string reason, DateTimeOffset now)
    {
        // Publish only after durable selection succeeds; failures leave the epoch untouched.
        persistSelection?.Invoke(replacement);
        var old = active; active = replacement; epoch++;
        compactionHold = null;
        if (old == replacement) return;
        history.Enqueue(new(now, old, replacement, reason, epoch)); while (history.Count > 100) history.Dequeue();
    }
}
