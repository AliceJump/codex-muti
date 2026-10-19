using System.Security.Cryptography;
using System.Text.Json;
using CodexPool.Core;
using Microsoft.Data.Sqlite;

namespace CodexPool.Daemon;

public sealed record TokenTotals(long Requests = 0, long ReportedRequests = 0, decimal InputTokens = 0,
    decimal OutputTokens = 0, decimal TotalTokens = 0, decimal CachedInputTokens = 0, decimal ReasoningOutputTokens = 0,
    long CachedReportedRequests = 0, long ReasoningReportedRequests = 0)
{
    public long UnreportedRequests => Requests - ReportedRequests;
    public static TokenTotals operator +(TokenTotals a, TokenTotals b) => new(a.Requests + b.Requests,
        a.ReportedRequests + b.ReportedRequests, a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens,
        a.TotalTokens + b.TotalTokens, a.CachedInputTokens + b.CachedInputTokens, a.ReasoningOutputTokens + b.ReasoningOutputTokens,
        a.CachedReportedRequests + b.CachedReportedRequests, a.ReasoningReportedRequests + b.ReasoningReportedRequests);
    public static TokenTotals Request(ResponseTokenUsage? usage) => usage is null ? new(Requests: 1)
        : new(1, 1, usage.InputTokens, usage.OutputTokens, usage.TotalTokens, usage.CachedInputTokens ?? 0,
            usage.ReasoningOutputTokens ?? 0, usage.CachedInputTokens.HasValue ? 1 : 0, usage.ReasoningOutputTokens.HasValue ? 1 : 0);
}
public sealed record UsageBucket(string AccountId, string Model, DateOnly? Day, TokenTotals Totals);
public sealed record AccountTokenTotals(string AccountId, TokenTotals Totals);
public sealed record ModelTokenTotals(string Model, TokenTotals Totals);
public sealed record DailyTokenTotals(DateOnly Day, TokenTotals Totals);
public sealed record TokenPeriod(TokenTotals Totals, AccountTokenTotals[] Accounts, ModelTokenTotals[] Models);
public sealed record TokenStatisticsView(DateTimeOffset StartedAt, DateTimeOffset? UpdatedAt, string TimeZone,
    int? RetainedDays, string Persistence, TokenPeriod Today, TokenPeriod Last7Days, TokenPeriod AllTime, DailyTokenTotals[] Daily,
    TokenPeriod Selected, DateOnly? From, DateOnly? To, DateOnly? EarliestDailyDay, DateOnly CurrentDay);

public sealed class UsageStatisticsDocument
{
    public int Version { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<UsageBucket> Buckets { get; set; } = [];
}

// Numeric model-access metadata only. Stored separately so statistics never rewrite auth credentials.
// The daemon owns this file under the vault directory's CurrentUser ACL and single-instance lock.
public sealed class UsageStatistics : IDisposable
{
    private readonly object gate = new();
    private readonly UsageHistoryStore? store;
    private readonly Dictionary<(string Account, string Model, DateOnly? Day), UsageBucket> buckets = new();
    private readonly Dictionary<(string Account, string Model, DateOnly? Day), UsageBucket> changed = new();
    private readonly List<(DateOnly Day, RequestRecord Record)> pendingRequests = [];
    private readonly Queue<RequestRecord> recent = new();
    private readonly Queue<AccountSwitch> switches = new();
    private readonly List<AccountSwitch> pendingSwitches = [];
    private TokenStatisticsView? cached;
    public RequestRecord[] RecentRequests { get { lock (gate) return recent.ToArray(); } }
    public AccountSwitch[] RecentSwitches { get { lock (gate) return switches.ToArray(); } }
    private readonly TimeZoneInfo timeZone;
    private readonly Func<DateTimeOffset> utcNow;
    private UsageStatisticsDocument document;
    private bool dirty;
    private bool persistenceFailed;

    public UsageStatistics(string directory, TimeZoneInfo? timeZone = null, Func<DateTimeOffset>? utcNow = null)
    {
        this.timeZone = timeZone ?? TimeZoneInfo.Local; this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        try
        {
            store = new UsageHistoryStore(Path.GetFullPath(directory), this.utcNow());
            document = store.Document;
            foreach (var bucket in document.Buckets) buckets.Add((bucket.AccountId, bucket.Model, bucket.Day), bucket);
            document.Buckets.Clear();
            foreach (var request in store.Requests(null, null, null, 100).Reverse()) recent.Enqueue(request);
            foreach (var change in store.RecentSwitches()) switches.Enqueue(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or SqliteException)
        {
            // Do not reset or overwrite unreadable history. Model access remains usable and the UI reports the limitation.
            store?.Dispose(); store = null; buckets.Clear(); recent.Clear(); switches.Clear();
            document = new() { StartedAt = this.utcNow() }; persistenceFailed = true;
        }
    }
    private DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, timeZone).DateTime);
    public void Record(string accountId, string model, ResponseTokenUsage? usage) =>
        RecordRequest(new(utcNow(), accountId, model, 0, 0, "unknown", 1, usage));
    public void RecordRequest(RequestRecord request)
    {
        lock (gate)
        {
            var now = request.Timestamp; var day = Day(now); var delta = TokenTotals.Request(request.TokenUsage);
            cached = null;
            foreach (var bucketDay in new DateOnly?[] { null, day })
            {
                var key = (request.Account, request.Model, bucketDay);
                var totals = buckets.TryGetValue(key, out var old) ? old.Totals + delta : delta;
                changed[key] = buckets[key] = new(request.Account, request.Model, bucketDay, totals);
            }
            pendingRequests.Add((day, request));
            recent.Enqueue(request); while (recent.Count > 100) recent.Dequeue();
            document.UpdatedAt = now; dirty = true;
            // Commit the request and both counters before completing settlement. Failed writes stay queued for retry.
            Flush();
        }
    }
    public void RecordSwitch(AccountSwitch change)
    {
        lock (gate)
        {
            pendingSwitches.Add(change); switches.Enqueue(change); while (switches.Count > 100) switches.Dequeue();
            dirty = true; Flush();
        }
    }
    public void Flush()
    {
        lock (gate)
        {
            if (!dirty || persistenceFailed || store is null) return;
            try
            {
                store.Save(document, changed.Values, pendingRequests, pendingSwitches);
                dirty = false; changed.Clear(); pendingRequests.Clear(); pendingSwitches.Clear();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or SqliteException) { }
        }
    }
    private string Persistence => persistenceFailed ? "unavailable" : dirty ? "pending" : "ready";
    public RequestHistoryPage Requests(DateOnly? from = null, DateOnly? to = null, long? before = null, int limit = 100)
    {
        if (from > to || before <= 0 || limit is < 1 or > 200) throw new ArgumentException("invalid_history_range");
        lock (gate)
        {
            Flush();
            RequestRecord[] saved = [];
            try { saved = store?.Requests(from, to, before, limit + 1) ?? []; }
            catch (Exception ex) when (ex is SqliteException or CryptographicException or JsonException) { persistenceFailed = true; }
            var pending = before is null ? pendingRequests.Where(x => (from is null || x.Day >= from) && (to is null || x.Day <= to))
                .Reverse().Select(x => x.Record) : [];
            var rows = pending.Concat(saved).Take(limit + 1).ToArray();
            var items = rows.Take(limit).ToArray();
            var cursor = rows.Length > limit && items[^1].Sequence > 0 ? (long?)items[^1].Sequence : null;
            return new(items, cursor, Persistence, store?.RequestsStartedAt ?? document.StartedAt);
        }
    }
    public TokenStatisticsView View(DateOnly? from = null, DateOnly? to = null)
    {
        if (from > to) throw new ArgumentException("invalid_statistics_range");
        lock (gate)
        {
            var today = Day(utcNow());
            if (from is null && to is null && cached is { } saved && saved.CurrentDay == today && saved.Persistence == Persistence) return saved;
            var daily = buckets.Values.Where(x => x.Day is not null).ToArray();
            var selected = daily.Where(x => (from is null || x.Day >= from) && (to is null || x.Day <= to)).ToArray();
            var allTime = Period(buckets.Values.Where(x => x.Day is null));
            var view = new TokenStatisticsView(document.StartedAt, document.UpdatedAt, timeZone.Id, null, Persistence,
                Period(daily.Where(x => x.Day == today)), Period(daily.Where(x => x.Day >= today.AddDays(-6) && x.Day <= today)),
                allTime, selected.GroupBy(x => x.Day!.Value).OrderBy(x => x.Key)
                    .Select(group => new DailyTokenTotals(group.Key, Sum(group.Select(x => x.Totals)))).ToArray(),
                from is null && to is null ? allTime : Period(selected), from, to, daily.Min(x => x.Day), today);
            if (from is null && to is null) cached = view;
            return view;
        }
    }
    public void Dispose() { lock (gate) { Flush(); store?.Dispose(); } }
    private static TokenTotals Sum(IEnumerable<TokenTotals> totals) => totals.Aggregate(new TokenTotals(), (sum, value) => sum + value);
    private static TokenPeriod Period(IEnumerable<UsageBucket> source)
    {
        var buckets = source.ToArray();
        return new(Sum(buckets.Select(x => x.Totals)),
            buckets.GroupBy(x => x.AccountId).Select(x => new AccountTokenTotals(x.Key, Sum(x.Select(b => b.Totals))))
                .OrderByDescending(x => x.Totals.TotalTokens).ThenBy(x => x.AccountId, StringComparer.Ordinal).ToArray(),
            buckets.GroupBy(x => x.Model).Select(x => new ModelTokenTotals(x.Key, Sum(x.Select(b => b.Totals))))
                .OrderByDescending(x => x.Totals.TotalTokens).ThenBy(x => x.Model, StringComparer.Ordinal).ToArray());
    }
}
