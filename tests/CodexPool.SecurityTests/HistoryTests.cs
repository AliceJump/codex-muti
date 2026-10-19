using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexPool.Core;
using CodexPool.Daemon;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class HistoryTests
{
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException(); }
    private static string DirectoryFor(string root, string name)
    { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static readonly ResponseTokenUsage Tokens = new(120, 30, 150, 80, 20);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("history-fixture", TimeSpan.FromHours(8), "fixture", "fixture");

    public static Task Persistence(string root)
    {
        var directory = DirectoryFor(root, "history-persistence"); var vault = new Vault(directory);
        var credentials = File.ReadAllBytes(Path.Combine(directory, "vault.dpapi"));
        using (var usage = new UsageStatistics(directory))
        {
            usage.RecordRequest(new(DateTimeOffset.UtcNow, "A", "gpt-6-luna", 123, 200, "stream_forwarded", 2, Tokens));
            var scheduler = new StickyScheduler("A", recordSwitch: usage.RecordSwitch);
            scheduler.Activate("B", [new("A"), new("B")], new(), DateTimeOffset.UtcNow);
            Check(usage.View().Persistence == "ready");
        }
        Check(credentials.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "vault.dpapi"))));
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "usage.sqlite"))).Contains("gpt-6-luna"));
        using (var restored = new UsageStatistics(directory))
        {
            Check(restored.View().AllTime.Totals == new TokenTotals(1, 1, 120, 30, 150, 80, 20, 1, 1));
            var request = restored.Requests().Items.Single();
            Check(request.TokenUsage == Tokens && request.DurationMs == 123 && request.Attempts == 2 && request.Status == 200 && request.Sequence > 0);
            Check(restored.RecentRequests.Single() == request);
            Check(restored.RecentSwitches.Single().To == "B" && restored.RecentSwitches.Single().Reason == "manual");
        }
        byte[] corrupted;
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "usage.sqlite"), Pooling = false }.ToString()))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "SELECT payload FROM buckets LIMIT 1"; corrupted = (byte[])command.ExecuteScalar()!;
            corrupted[^1] ^= 255; command.CommandText = "UPDATE buckets SET payload=$payload";
            command.Parameters.AddWithValue("$payload", corrupted); command.ExecuteNonQuery();
        }
        using (var unavailableDatabase = new UsageStatistics(directory))
        {
            unavailableDatabase.Record("A", "model", Tokens); unavailableDatabase.Flush();
            Check(unavailableDatabase.View().Persistence == "unavailable" && unavailableDatabase.View().AllTime.Totals.Requests == 1);
        }
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "usage.sqlite"), Pooling = false }.ToString()))
        {
            database.Open(); using var command = database.CreateCommand(); command.CommandText = "SELECT payload FROM buckets LIMIT 1";
            Check(((byte[])command.ExecuteScalar()!).SequenceEqual(corrupted));
        }
        // Invalid legacy files must never be reset or overwritten, even when new requests settle.
        var damagedPath = DirectoryFor(root, "history-damaged");
        var damaged = new byte[] { 0, 1, 2, 3, 4 }; File.WriteAllBytes(Path.Combine(damagedPath, "usage.dpapi"), damaged);
        using var unavailable = new UsageStatistics(damagedPath);
        unavailable.Record("A", "model", Tokens); unavailable.Flush();
        Check(unavailable.View().Persistence == "unavailable" && unavailable.Requests().Items.Single().TokenUsage == Tokens);
        Check(File.ReadAllBytes(Path.Combine(damagedPath, "usage.dpapi")).SequenceEqual(damaged));
        return Task.CompletedTask;
    }

    public static Task MigrationAndRetry(string root)
    {
        var directory = DirectoryFor(root, "history-migration");
        var now = DateTimeOffset.Parse("2026-10-13T15:59:59Z"); var originalDay = new DateOnly(2026, 10, 13);
        var document = new UsageStatisticsDocument { StartedAt = now.AddYears(-1), UpdatedAt = now,
            Buckets = [new("legacy", "old-model", null, TokenTotals.Request(Tokens)), new("legacy", "old-model", originalDay, TokenTotals.Request(Tokens))] };
        var legacy = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(document), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(directory, "usage.dpapi"), legacy);
        using (var usage = new UsageStatistics(directory, Zone, () => now))
        {
            Check(usage.View().AllTime.Totals.Requests == 1 && usage.Requests().Items.Length == 0);
            usage.Record("A", "model", null); now = now.AddSeconds(1); usage.Record("B", "model", Tokens);
            Check(usage.View(originalDay, originalDay).Selected.Totals.Requests == 2);
            Check(usage.Requests(originalDay, originalDay).Items.Single().TokenUsage is null);
            var nextDay = originalDay.AddDays(1);
            Check(usage.View(nextDay, nextDay).Selected.Totals.TotalTokens == 150);
            now = now.AddDays(400);
            usage.Record("A", "model", Tokens);
            Check(usage.View(originalDay, originalDay).Selected.Totals.Requests == 2 && usage.View().RetainedDays is null);
            for (var i = 0; i < 205; i++) usage.Record("A", "model", i % 2 == 0 ? null : Tokens);
            var page = usage.Requests(limit: 100); var second = usage.Requests(before: page.NextBefore, limit: 100);
            var third = usage.Requests(before: second.NextBefore, limit: 100);
            Check(page.Items.Length == 100 && second.Items.Length == 100 && third.Items.Length == 8 && third.NextBefore is null);
            Check(page.Items.Concat(second.Items).Concat(third.Items).Select(x => x.Sequence).Distinct().Count() == 208);
            using (var blocked = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "usage.sqlite"), Pooling = false }.ToString()))
            {
                blocked.Open(); using var transaction = blocked.BeginTransaction();
                usage.Record("B", "model", Tokens);
                Check(usage.View().Persistence == "pending");
                transaction.Rollback();
            }
            usage.Flush(); Check(usage.View().Persistence == "ready" && usage.Requests(limit: 1).Items[0].Account == "B");
        }
        using var restored = new UsageStatistics(directory, Zone, () => now);
        Check(restored.View().AllTime.Totals.Requests == 210 && restored.View(originalDay, originalDay).Selected.Totals.Requests == 2);
        Check(restored.Requests().Items.Length == 100 && File.ReadAllBytes(Path.Combine(directory, "usage.dpapi")).SequenceEqual(legacy));
        return Task.CompletedTask;
    }

    public static async Task Endpoints(string root)
    {
        using var statistics = new UsageStatistics(DirectoryFor(root, "history-api"), Zone);
        statistics.RecordRequest(new(DateTimeOffset.Parse("2025-01-01T16:00:00Z"), "A", "old-model", 10, 499, "client_cancelled", 1, null));
        statistics.RecordRequest(new(DateTimeOffset.Parse("2026-10-13T15:59:59Z"), "B", "new-model", 20, 200, "stream_forwarded", 1, Tokens));
        statistics.RecordRequest(new(DateTimeOffset.Parse("2026-10-13T16:00:00Z"), "B", "new-model", 30, 200, "stream_forwarded", 1, Tokens));
        var builder = WebApplication.CreateBuilder([]); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build(); UsageEndpoints.Map(app, statistics); await app.StartAsync();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        foreach (var invalid in new[] { "from=bad", "from=2026-10-14&to=2026-10-13", "from=2026-02-30", "from=2026-10-13&from=2026-10-14" })
            foreach (var route in new[] { "/statistics?", "/requests?" })
            { using var response = await http.GetAsync(route + invalid); Check(response.StatusCode == HttpStatusCode.BadRequest); }
        foreach (var invalid in new[] { "limit=0", "limit=201", "before=0", "before=xyz", "limit=1&limit=2", "before=-1" })
        { using var response = await http.GetAsync("/requests?" + invalid); Check(response.StatusCode == HttpStatusCode.BadRequest); }
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var all = await http.GetFromJsonAsync<TokenStatisticsView>("/statistics", options);
        Check(all!.Selected.Totals.Requests == 3 && all.Daily.Length == 3);
        var old = await http.GetFromJsonAsync<TokenStatisticsView>("/statistics?from=2025-01-02&to=2025-01-02", options);
        Check(old!.Selected.Totals.Requests == 1 && old.Selected.Totals.UnreportedRequests == 1);
        var range = await http.GetFromJsonAsync<RequestHistoryPage>("/requests?from=2026-10-13&to=2026-10-13", options);
        Check(range!.Items.Single().DurationMs == 20);
        var page = await http.GetFromJsonAsync<RequestHistoryPage>("/requests?limit=1", options);
        Check(page!.Items.Single().DurationMs == 30 && page.NextBefore is not null);
        var second = await http.GetFromJsonAsync<RequestHistoryPage>("/requests?limit=1&before=" + page.NextBefore, options);
        Check(second!.Items.Single().DurationMs == 20);
        var empty = await http.GetFromJsonAsync<TokenStatisticsView>("/statistics?from=2020-01-01&to=2020-01-01", options);
        Check(empty!.Selected.Totals.Requests == 0 && empty.Daily.Length == 0);
        await app.StopAsync();
    }
}
