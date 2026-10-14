using CodexPool.Core;

internal static class CompactionRoutingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly PoolSettings Settings = new(true, WindowThreshold: 95);
    private static PoolAccount[] Accounts(double used = 92) =>
        [new("A", QuotaUpdatedAt: Now, Windows: [new(18000, used, Now.AddHours(1))]), new("B")];
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException(); }

    public static Task Lifecycle()
    {
        var scheduler = new StickyScheduler("A");
        var advice = scheduler.AdviseCompaction("model", Accounts(), Settings, Now)!;
        var hold = scheduler.ReserveCompaction(advice, Accounts(), Settings, Now);
        Check(scheduler.Select(Accounts(96), Settings, Now).AccountId == "A");
        Check(!scheduler.FinishCompaction("wrong", true, Accounts(), Settings, Now) && scheduler.Hold == hold);
        Check(scheduler.FinishCompaction(hold.Id, true, Accounts(), Settings, Now) && scheduler.Active == "B");
        Check(!scheduler.FinishCompaction(hold.Id, true, Accounts(), Settings, Now));
        Check(scheduler.History.Single().Reason == "compaction_completed");
        return Task.CompletedTask;
    }

    public static Task Invalidations()
    {
        foreach (var reason in new[] { "manual", "expired", "paused", "exhausted", "stale", "off", "auth", "failed" })
        {
            var scheduler = new StickyScheduler("A"); var accounts = Accounts(); var settings = Settings; var now = Now;
            var hold = scheduler.ReserveCompaction(scheduler.AdviseCompaction("model", accounts, settings, now)!, accounts, settings, now);
            switch (reason)
            {
                case "manual": scheduler.Activate("B", accounts, settings, now); break;
                case "expired": now = hold.ExpiresAt; break;
                case "paused": accounts[0] = accounts[0] with { Paused = true }; break;
                case "exhausted": accounts = Accounts(100); break;
                case "stale": accounts[0] = accounts[0] with { QuotaUpdatedAt = Now.AddMinutes(-3) }; break;
                case "off": settings = new(false); break;
                case "auth": accounts[0] = accounts[0] with { AuthReady = false }; break;
            }
            Check(!scheduler.FinishCompaction(hold.Id, reason != "failed", accounts, settings, now));
            Check(scheduler.Hold is null && (reason == "manual" ? scheduler.Active == "B" : scheduler.Active == "A"));
            Check(scheduler.AdviseCompaction("model", accounts, settings, now) is null);
        }
        return Task.CompletedTask;
    }

    public static Task Eligibility()
    {
        var scheduler = new StickyScheduler("A"); var accounts = Accounts();
        Check(scheduler.AdviseCompaction("model", Accounts(89), Settings, Now) is null);
        Check(scheduler.AdviseCompaction("model", Accounts(100), Settings, Now) is null);
        Check(scheduler.AdviseCompaction("model", [accounts[0]], Settings, Now) is null);
        Check(scheduler.AdviseCompaction("model", [accounts[0] with { QuotaUpdatedAt = null }, accounts[1]], Settings, Now) is null);
        var advice = scheduler.AdviseCompaction("model", accounts, Settings, Now)!;
        scheduler.Activate("A", accounts, Settings, Now);
        try { scheduler.ReserveCompaction(advice, accounts, Settings, Now); Check(false); }
        catch (CompactionRejectedException ex) { Check(ex.Code == "selection_changed"); }
        var hold = scheduler.ReserveCompaction(scheduler.AdviseCompaction("model", accounts, Settings, Now)!, accounts, Settings, Now);
        var current = scheduler.Current;
        try { scheduler.ReserveCompaction(new(current.AccountId, current.Epoch, "model"), accounts, Settings, Now); Check(false); }
        catch (CompactionRejectedException) { }
        Check(!scheduler.FinishCompaction(hold.Id, false, accounts, Settings, Now));
        Check(scheduler.AdviseCompaction("model", accounts, Settings, Now) is null);
        return Task.CompletedTask;
    }

    public static Task WarningWindows()
    {
        bool Warn(PoolSettings settings, params RoutingQuotaWindow[] windows) => new StickyScheduler("A")
            .AdviseCompaction("model", [new("A", QuotaUpdatedAt: Now, Windows: windows), new("B")], settings, Now) is not null;
        RoutingQuotaWindow Window(int? seconds, double used) => new(seconds, used, Now.AddDays(7));
        foreach (var settings in new[] { new PoolSettings(true, 98, 98), new PoolSettings(true, 80, 90, 98) })
        {
            Check(!Warn(settings, Window(604800, 95.99)));
            Check(Warn(settings, Window(604800, 96)));
            foreach (var seconds in new int?[] { 18000, 25200, null })
            {
                Check(!Warn(settings, Window(seconds, 92.99)));
                Check(Warn(settings, Window(seconds, 93)));
            }
            Check(!Warn(settings, Window(18000, 92), Window(604800, 95)));
            Check(Warn(settings, Window(18000, 93), Window(604800, 95)));
            Check(Warn(settings, Window(18000, 92), Window(604800, 96)));
        }
        var legacy = new PoolSettings(true, 80, 90);
        Check(!Warn(legacy, Window(604800, 87.99)) && Warn(legacy, Window(604800, 88)));
        Check(!Warn(legacy, Window(18000, 74.99)) && Warn(legacy, Window(18000, 75)));
        Check(Warn(new(true, WindowThreshold: 1), Window(604800, 0)));
        Check(!Warn(new(false, WindowThreshold: 98), Window(604800, 96)));
        return Task.CompletedTask;
    }
}
