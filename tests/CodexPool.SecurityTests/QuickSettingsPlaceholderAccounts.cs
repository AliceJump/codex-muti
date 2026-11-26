using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexPool.Daemon;

internal static class QuickSettingsPlaceholderAccounts
{
    private sealed record Account(string Id, string Name, int FiveHour, int Weekly);
    private static readonly Account Primary = new("demo-main", "示例主账户", 82, 66);
    private static readonly Account Backup = new("demo-backup", "示例备用账户", 37, 24);

    // All HTTP requests terminate in this handler, including accidental new routes.
    private sealed class PlaceholderService : HttpMessageHandler
    {
        private Account active = Primary;
        public int ActivationCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Account[] accounts = [Primary, Backup];
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/status")
            {
                var now = DateTimeOffset.UtcNow;
                var status = new
                {
                    activeAccountId = active.Id,
                    accounts = accounts.Select(a => new { id = a.Id, name = a.Name, enabled = true,
                        paused = false, drain = false, state = a == active ? "active" : "standby", auth = "ready" }),
                    accountAvailability = accounts.Select(a => new { accountId = a.Id, available = true }),
                    settings = new { autoSwitch = false },
                    usage = accounts.Select(a => new { accountId = a.Id, updatedAt = now, cacheState = "current",
                        windows = new[] {
                            new { kind = "5h", windowSeconds = 18000, remainingPercent = a.FiveHour, resetAt = now.AddHours(3) },
                            new { kind = "weekly", windowSeconds = 604800, remainingPercent = a.Weekly, resetAt = now.AddDays(4) }
                        } })
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(status) });
            }
            var next = accounts.SingleOrDefault(a => path == $"/accounts/{a.Id}/activate");
            if (request.Method != HttpMethod.Post || next is null)
                throw new InvalidOperationException("Unexpected placeholder request.");
            active = next;
            ActivationCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    internal static async Task RunAsync(string root, string? previewDirectory = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "placeholder-accounts")).FullName;
        _ = new Vault(folder); // Local management key for this empty, test-only directory.
        var service = Path.Combine(folder, "CodexPool.Daemon.exe");
        File.WriteAllBytes(service, []); // Path validation only. Never launched.
        using var handler = new PlaceholderService();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://placeholder.invalid/") };
        var proxy = new QuickSettingsProxy(service, folder, http, () => new ProxyIdentity(1, 1, service));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;

        async Task<JsonObject> VerifySelection(Account expected, ulong revision)
        {
            var projection = QuickSettingsRegistration.Presentation(await proxy.ReadAsync(token), true, "");
            var state = QuickSettingsRegistration.ContractState(projection, "placeholder-demo", revision);
            var tile = state["tile"]!;
            var list = state["components"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "accounts")!;
            var rows = list["items"]!.AsArray();
            if (rows.Count != 2 || list["action"]!.GetValue<string>() != "activate-account" ||
                tile["label"]!.GetValue<string>() != expected.Name ||
                tile["indicator"]!["known"]!.GetValue<bool>() != true ||
                tile["indicator"]!["value"]!.GetValue<double>() != expected.FiveHour)
                throw new InvalidOperationException("Incorrect placeholder tile or account list.");
            foreach (var account in new[] { Primary, Backup })
            {
                var row = rows.Single(r => r!["id"]!.GetValue<string>() == account.Id)!;
                var subtitle = row["subtitle"]!.GetValue<string>();
                if (row["selected"]!.GetValue<bool>() != (account == expected) ||
                    !row["enabled"]!.GetValue<bool>() || row["title"]!.GetValue<string>() != account.Name ||
                    !subtitle.Contains($"{account.FiveHour}%") || !subtitle.Contains($"{account.Weekly}%"))
                    throw new InvalidOperationException("Incorrect placeholder account row.");
            }
            return state;
        }

        var first = await VerifySelection(Primary, 1);
        if (!(await proxy.ActivateAccountAsync(Backup.Id, token)).Success) throw new InvalidOperationException("Switch to backup failed.");
        var second = await VerifySelection(Backup, 2);
        if (!(await proxy.ActivateAccountAsync(Primary.Id, token)).Success) throw new InvalidOperationException("Switch to primary failed.");
        await VerifySelection(Primary, 3);
        if (handler.ActivationCount != 2) throw new InvalidOperationException("Unexpected activation count.");
        if (previewDirectory is not null)
        {
            Directory.CreateDirectory(previewDirectory);
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            await File.WriteAllTextAsync(Path.Combine(previewDirectory, "demo-main.json"), first.ToJsonString(options));
            await File.WriteAllTextAsync(Path.Combine(previewDirectory, "demo-backup.json"), second.ToJsonString(options));
        }
    }
}
