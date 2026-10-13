using System.Security.Cryptography;
using System.Text.Json;
using CodexPool.Core;

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
    int RetainedDays, string Persistence, TokenPeriod Today, TokenPeriod Last7Days, TokenPeriod AllTime, DailyTokenTotals[] Daily);

public sealed class UsageStatisticsDocument
{
    public int Version { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<UsageBucket> Buckets { get; set; } = [];
}

// Numeric model-access metadata only. Stored separately so statistics never rewrite auth credentials.
// The daemon owns this file under the vault directory's CurrentUser ACL and single-instance lock.
public sealed class UsageStatistics
{
    private const int RetainedDays = 90;
    private readonly object gate = new();
    private readonly string path;
    private readonly TimeZoneInfo timeZone;
    private readonly Func<DateTimeOffset> utcNow;
    private UsageStatisticsDocument document;
    private bool dirty;
    private bool persistenceFailed;

    public UsageStatistics(string directory, TimeZoneInfo? timeZone = null, Func<DateTimeOffset>? utcNow = null)
    {
        path = Path.Combine(Path.GetFullPath(directory), "usage.dpapi");
        this.timeZone = timeZone ?? TimeZoneInfo.Local; this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        try
        {
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16 * 1024 * 1024)
                    throw new IOException("unsafe_statistics_file");
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                try { document = JsonSerializer.Deserialize<UsageStatisticsDocument>(plain) ?? throw new JsonException(); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (document.Version != 1) throw new JsonException();
            }
            else document = new() { StartedAt = this.utcNow() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            // Do not reset or overwrite unreadable history. Model access remains usable and the UI reports the limitation.
            document = new() { StartedAt = this.utcNow() }; persistenceFailed = true;
        }
    }
    private DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, timeZone).DateTime);
    public void Record(string accountId, string model, ResponseTokenUsage? usage)
    {
        lock (gate)
        {
            var now = utcNow(); var day = Day(now); var delta = TokenTotals.Request(usage);
            foreach (var bucketDay in new DateOnly?[] { null, day })
            {
                var index = document.Buckets.FindIndex(x => x.AccountId == accountId && x.Model == model && x.Day == bucketDay);
                if (index < 0) document.Buckets.Add(new(accountId, model, bucketDay, delta));
                else document.Buckets[index] = document.Buckets[index] with { Totals = document.Buckets[index].Totals + delta };
            }
            document.Buckets.RemoveAll(x => x.Day is { } old && old < day.AddDays(1 - RetainedDays));
            document.UpdatedAt = now; dirty = true;
        }
    }
    public void Flush()
    {
        lock (gate)
        {
            if (!dirty || persistenceFailed) return;
            var temp = path + "-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException();
                var plain = JsonSerializer.SerializeToUtf8Bytes(document); byte[] encrypted;
                try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { file.Write(encrypted); file.Flush(true); }
                File.Move(temp, path, true); dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    public TokenStatisticsView View()
    {
        lock (gate)
        {
            var today = Day(utcNow());
            var daily = document.Buckets.Where(x => x.Day is { } day && day >= today.AddDays(1 - RetainedDays) && day <= today).ToArray();
            return new(document.StartedAt, document.UpdatedAt, timeZone.Id, RetainedDays,
                persistenceFailed ? "unavailable" : dirty ? "pending" : "ready",
                Period(daily.Where(x => x.Day == today)), Period(daily.Where(x => x.Day >= today.AddDays(-6))),
                Period(document.Buckets.Where(x => x.Day is null)),
                Enumerable.Range(0, 7).Select(offset => today.AddDays(offset - 6))
                    .Select(day => new DailyTokenTotals(day, Sum(daily.Where(x => x.Day == day).Select(x => x.Totals)))).ToArray());
        }
    }
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
