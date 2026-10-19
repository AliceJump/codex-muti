using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexPool.Daemon;

public sealed record RequestRecord(DateTimeOffset Timestamp, string Account, string Model, long DurationMs,
    int Status, string Outcome, int Attempts, CodexPool.Core.ResponseTokenUsage? TokenUsage, long Sequence = 0);
public sealed record RequestHistoryPage(RequestRecord[] Items, long? NextBefore, string Persistence, DateTimeOffset StartedAt);

// Only dates and local sequence numbers are indexed in plaintext. Metadata payloads use CurrentUser DPAPI.
// The vault directory supplies the ACL and daemon lifetime lock. Each batch commits counters and requests together.
internal sealed class UsageHistoryStore : IDisposable
{
    private readonly SqliteConnection connection;
    public DateTimeOffset RequestsStartedAt { get; }
    public UsageStatisticsDocument Document { get; }

    public UsageHistoryStore(string directory, DateTimeOffset now)
    {
        var database = Path.Combine(directory, "usage.sqlite");
        if (File.Exists(database) && (File.GetAttributes(database) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("unsafe_statistics_file");
        connection = new(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, DefaultTimeout = 1 }.ToString());
        try
        {
            connection.Open();
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
            Execute("CREATE TABLE IF NOT EXISTS metadata (id INTEGER PRIMARY KEY, payload BLOB NOT NULL);"
                + "CREATE TABLE IF NOT EXISTS buckets (id TEXT PRIMARY KEY, payload BLOB NOT NULL);"
                + "CREATE TABLE IF NOT EXISTS switches (id INTEGER PRIMARY KEY AUTOINCREMENT, payload BLOB NOT NULL);"
                + "CREATE TABLE IF NOT EXISTS requests (id INTEGER PRIMARY KEY AUTOINCREMENT, day TEXT NOT NULL, payload BLOB NOT NULL);"
                + "CREATE INDEX IF NOT EXISTS requests_day ON requests(day, id);");
            using var query = connection.CreateCommand(); query.CommandText = "SELECT payload FROM metadata WHERE id=1";
            if (query.ExecuteScalar() is byte[] saved)
            {
                var metadata = Decode<HistoryMetadata>(saved);
                if (metadata.Version != 1) throw new JsonException();
                RequestsStartedAt = metadata.RequestsStartedAt;
                Document = new() { StartedAt = metadata.StartedAt, UpdatedAt = metadata.UpdatedAt };
                using var buckets = connection.CreateCommand(); buckets.CommandText = "SELECT payload FROM buckets";
                using var reader = buckets.ExecuteReader();
                while (reader.Read()) Document.Buckets.Add(Decode<UsageBucket>((byte[])reader[0]));
            }
            else
            {
                // Leave the old file untouched. The metadata marker makes import atomic and one-time.
                var legacy = Path.Combine(directory, "usage.dpapi");
                if (File.Exists(legacy) && (File.GetAttributes(legacy) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("unsafe_statistics_file");
                Document = File.Exists(legacy) ? Decode<UsageStatisticsDocument>(File.ReadAllBytes(legacy)) : new() { StartedAt = now };
                if (Document.Version != 1) throw new JsonException();
                RequestsStartedAt = now;
                Save(Document, Document.Buckets, []);
            }
        }
        catch { connection.Dispose(); throw; }
    }

    private sealed record HistoryMetadata(int Version, DateTimeOffset StartedAt, DateTimeOffset? UpdatedAt, DateTimeOffset RequestsStartedAt);
    private static byte[] Encode<T>(T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try { return ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static T Decode<T>(byte[] encrypted)
    {
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(plain) ?? throw new JsonException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void Execute(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Save(UsageStatisticsDocument document, IEnumerable<UsageBucket> buckets, IEnumerable<(DateOnly Day, RequestRecord Record)> requests,
        IEnumerable<CodexPool.Core.AccountSwitch>? switches = null)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO metadata(id,payload) VALUES(1,$payload)";
        command.Parameters.AddWithValue("$payload", Encode(new HistoryMetadata(1, document.StartedAt, document.UpdatedAt, RequestsStartedAt)));
        command.ExecuteNonQuery();
        foreach (var bucket in buckets)
        {
            command.Parameters.Clear(); command.CommandText = "INSERT OR REPLACE INTO buckets(id,payload) VALUES($id,$payload)";
            var key = JsonSerializer.Serialize(new { bucket.AccountId, bucket.Model, bucket.Day });
            command.Parameters.AddWithValue("$id", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
            command.Parameters.AddWithValue("$payload", Encode(bucket)); command.ExecuteNonQuery();
        }
        foreach (var (day, record) in requests)
        {
            command.Parameters.Clear(); command.CommandText = "INSERT INTO requests(day,payload) VALUES($day,$payload)";
            command.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$payload", Encode(record)); command.ExecuteNonQuery();
        }
        foreach (var change in switches ?? [])
        {
            command.Parameters.Clear(); command.CommandText = "INSERT INTO switches(payload) VALUES($payload)";
            command.Parameters.AddWithValue("$payload", Encode(change)); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public CodexPool.Core.AccountSwitch[] RecentSwitches()
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM switches ORDER BY id DESC LIMIT 100";
        using var reader = command.ExecuteReader(); var rows = new List<CodexPool.Core.AccountSwitch>();
        while (reader.Read()) rows.Add(Decode<CodexPool.Core.AccountSwitch>((byte[])reader[0]));
        return rows.AsEnumerable().Reverse().ToArray();
    }
    public RequestRecord[] Requests(DateOnly? from, DateOnly? to, long? before, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,payload FROM requests WHERE day >= $from AND day <= $to AND id < $before ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$from", (from ?? DateOnly.MinValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", (to ?? DateOnly.MaxValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$before", before ?? long.MaxValue); command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader(); var rows = new List<RequestRecord>();
        while (reader.Read()) rows.Add(Decode<RequestRecord>((byte[])reader[1]) with { Sequence = reader.GetInt64(0) });
        return rows.ToArray();
    }
    public void Dispose() => connection.Dispose();
}
