using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexPool.Daemon;

public static class LocalCommands
{
    public static bool EqualSecret(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public static async Task<int> RunAsync(string command, string[] args, string directory)
    {
        if (command == "help")
        {
            Console.WriteLine("serve [--live-boundary-probe] | login <label> [account-id] | status | models | activate/pause/resume/drain/undrain/enable/disable <account-id> | codex [official arguments]"); return 0;
        }
        var key = Vault.ReadLocalKey(directory);
        if (command == "codex")
        {
            var start = new ProcessStartInfo("codex") { UseShellExecute = false };
            foreach (var option in new[] { "model_provider=\"codex_pool\"", "model_providers.codex_pool.name=\"Codex Pool\"",
                "model_providers.codex_pool.base_url=\"http://127.0.0.1:3210/v1\"", "model_providers.codex_pool.wire_api=\"responses\"",
                "model_providers.codex_pool.env_key=\"CODEX_POOL_KEY\"", "model_providers.codex_pool.requires_openai_auth=false",
                "model_providers.codex_pool.supports_websockets=false" })
            { start.ArgumentList.Add("-c"); start.ArgumentList.Add(option); }
            foreach (var argument in args) start.ArgumentList.Add(argument);
            start.Environment["CODEX_POOL_KEY"] = key;
            using var child = Process.Start(start) ?? throw new InvalidOperationException(); await child.WaitForExitAsync(); return child.ExitCode;
        }
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        if (command is "status" or "models")
        {
            using var response = await client.GetAsync("http://127.0.0.1:3210/" + command); response.EnsureSuccessStatusCode();
            Console.WriteLine(await response.Content.ReadAsStringAsync()); return 0;
        }
        if (command is "activate" or "pause" or "resume" or "drain" or "undrain" or "enable" or "disable" && args.Length == 1 && Guid.TryParseExact(args[0], "N", out _))
        {
            using var response = await client.PostAsync("http://127.0.0.1:3210/accounts/" + args[0] + "/" + command, null);
            Console.WriteLine(response.IsSuccessStatusCode ? "Account control applied for new requests." : "Control rejected; check account and live migration gate.");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        if (command == "login" && args.Length is 1 or 2)
        {
            using var response = await client.PostAsJsonAsync("http://127.0.0.1:3210/accounts/login", new { name = args[0], accountId = args.ElementAtOrDefault(1) });
            response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var url = json.RootElement.GetProperty("authorizationUrl").GetString()!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "auth.openai.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
                throw new InvalidOperationException();
            // The OS opens the authorization URL; it is never printed into console/log/chat.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Console.WriteLine("Complete Continue with ChatGPT in your system browser. Run status after the callback."); return 0;
        }
        Console.Error.WriteLine("Unknown command or missing arguments. Use help."); return 1;
    }
}
