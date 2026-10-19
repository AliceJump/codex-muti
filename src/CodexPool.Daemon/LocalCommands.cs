using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using CodexPool.Core;

namespace CodexPool.Daemon;

public static class LocalCommands
{
    public static bool EqualSecret(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public static PoolSettings? ParseSettings(string[] args)
    {
        if (args.Length is not (2 or 3 or 4) || args[0] is not ("on" or "off")
            || !double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var five)) return null;
        var settings = new PoolSettings(args[0] == "on", WindowThreshold: five);
        if (args.Length >= 3)
        {
            if (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var weekly)) return null;
            settings = new(args[0] == "on", five, weekly);
        }
        if (args.Length == 4 && args[3] != "null")
        {
            if (!double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var window)) return null;
            settings = settings with { WindowThreshold = window };
        }
        try { StickyScheduler.Validate(settings); return settings; }
        catch (ArgumentException) { return null; }
    }
    public static async Task<int> RunAsync(string command, string[] args, string directory)
    {
        if (command == "help")
        {
            Console.WriteLine("serve [--live-boundary-probe] | login/native-login <label> | dashboard | status | usage | statistics/requests [yyyy-MM-dd [yyyy-MM-dd]] | models | settings [on/off threshold] | rename <account-id> <name> | activate/pause/resume/drain/undrain/enable/disable/refresh/delete <account-id> | codex [official arguments]"); return 0;
        }
        var key = Vault.ReadLocalKey(directory);
        if (command == "model-credential")
        {
            // Official provider auth.command captures this private stdout pipe. Never a log or API response.
            if (!Console.IsOutputRedirected) { Console.Error.WriteLine("此命令仅供 Codex 凭据管道调用。"); return 1; }
            Console.Write(key); return 0;
        }
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
        if (command == "settings" && args.Length is 0 or 2 or 3 or 4)
        {
            if (args.Length == 0) { Console.WriteLine(await client.GetStringAsync("http://127.0.0.1:3210/settings")); return 0; }
            var settings = ParseSettings(args); if (settings is null) return 1;
            using var response = await client.PostAsJsonAsync("http://127.0.0.1:3210/settings", settings); response.EnsureSuccessStatusCode();
            Console.WriteLine(await response.Content.ReadAsStringAsync()); return 0;
        }
        if (command == "dashboard")
        {
            using var response = await client.PostAsync("http://127.0.0.1:3210/dashboard/session", null); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var url = json.RootElement.GetProperty("url").GetString()!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port != 3210
                || !uri.AbsolutePath.StartsWith("/dashboard/open/", StringComparison.Ordinal)) throw new InvalidOperationException();
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); Console.WriteLine("Dashboard opened in your system browser."); return 0;
        }
        if (command is "status" or "models" or "usage" or "statistics" or "requests")
        {
            if (args.Length > 2 || args.Length > 0 && command is not ("statistics" or "requests")) return 2;
            var query = "";
            if (args.Length > 0)
            {
                if (!DateOnly.TryParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)) return 2;
                query = "?from=" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (args.Length == 2)
                {
                    if (!DateOnly.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) || to < from) return 2;
                    query += "&to=" + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
            }
            using var response = await client.GetAsync("http://127.0.0.1:3210/" + command + query); response.EnsureSuccessStatusCode();
            Console.WriteLine(await response.Content.ReadAsStringAsync()); return 0;
        }
        if (command == "rename" && args.Length == 2 && Guid.TryParseExact(args[0], "N", out _))
        {
            using var response = await client.PostAsJsonAsync("http://127.0.0.1:3210/accounts/" + args[0] + "/rename", new { name = args[1] });
            Console.WriteLine(response.IsSuccessStatusCode ? "账户名称已更新，登录和路由保持不变。" : "改名失败，请检查账户 ID 和名称（1–80 字符，不含控制字符）。");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        if (command is "activate" or "pause" or "resume" or "drain" or "undrain" or "enable" or "disable" or "refresh" or "delete" && args.Length == 1 && Guid.TryParseExact(args[0], "N", out _))
        {
            using var response = await client.PostAsync("http://127.0.0.1:3210/accounts/" + args[0] + "/" + command, null);
            if (command == "refresh" && response.IsSuccessStatusCode) { Console.WriteLine(await response.Content.ReadAsStringAsync()); return 0; }
            if (command == "delete")
            {
                if (response.IsSuccessStatusCode) { Console.WriteLine("Account permanently removed from the local vault."); return 0; }
                Console.WriteLine("Delete rejected; check account id, in-flight requests, or activate another eligible account first.");
                return 1;
            }
            Console.WriteLine(response.IsSuccessStatusCode ? "Account control applied for new requests." : "Control rejected; check account and live migration gate.");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        if (command == "native-login" && args.Length == 1)
        {
            using var response = await client.PostAsJsonAsync("http://127.0.0.1:3210/accounts/native-login", new { name = args[0], accountId = (string?)null });
            response.EnsureSuccessStatusCode(); Console.WriteLine("Official Codex login started in an isolated local profile. Complete authorization in the system browser, then run status."); return 0;
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
