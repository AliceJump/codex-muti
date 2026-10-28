using System.Diagnostics;
using System.Text.Json;
using CodexPool.Daemon;

internal static class NativeLoginTests
{
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException(); }
    private static string State(NativeLogin login, string id) => JsonSerializer.SerializeToElement(login.PublicAttempts)
        .EnumerateArray().Single(x => x.GetProperty("id").GetString() == id).GetProperty("state").GetString()!;
    private static async Task Finished(NativeLogin login, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (State(login, id) == "waiting_for_browser") await Task.Delay(20, timeout.Token);
    }
    private static Process StartFixture(string mode, string profile)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(NativeLoginTests).Assembly.Location);
        start.ArgumentList.Add("--native-login-helper"); start.ArgumentList.Add(mode); start.ArgumentList.Add(profile);
        return Process.Start(start)!;
    }
    public static async Task<int> RunHelperAsync(string[] args)
    {
        // Fixtures never run the installed CLI, contact upstream, or create real credentials.
        if (args[0] == "wait") { await Task.Delay(TimeSpan.FromMinutes(1)); return 1; }
        if (args[0] == "pipe")
        {
            var child = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            child.ArgumentList.Add(typeof(NativeLoginTests).Assembly.Location);
            child.ArgumentList.Add("--native-login-helper"); child.ArgumentList.Add("wait"); child.ArgumentList.Add(args[1]);
            using var process = Process.Start(child)!;
            await File.WriteAllTextAsync(Path.Combine(args[1], "fixture-child.pid"), process.Id.ToString());
            return 1;
        }
        if (args[0] == "cleanup")
        {
            await File.WriteAllTextAsync(Path.Combine(args[1], "locked-fixture"), "synthetic");
            await Task.Delay(TimeSpan.FromMinutes(1)); return 1;
        }
        return args[0] == "missing" ? 0 : 1;
    }
    public static async Task Lifecycle(string root)
    {
        var directory = Path.Combine(root, "native-login-lifecycle");
        using var http = new HttpClient(); var auth = new OAuth(http, new Vault(directory));
        var launches = new List<int>();
        var login = new NativeLogin(auth, directory, info =>
        {
            Check(info.Environment["CODEX_HOME"]!.StartsWith(directory + Path.DirectorySeparatorChar));
            Check(info.ArgumentList.SequenceEqual(new[] { "-c", "cli_auth_credentials_store=\"file\"", "login" }));
            var process = StartFixture("wait", info.Environment["CODEX_HOME"]!); launches.Add(process.Id); return process;
        }, TimeSpan.FromSeconds(20));
        var id = await login.StartAsync("A");
        try
        {
            var duplicates = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => login.StartAsync("A"))));
            Check(duplicates.All(x => x == id) && launches.Count == 1);
            try { await login.StartAsync("B"); Check(false); } catch (AuthFailure ex) { Check(ex.Code == "native_login_already_running"); }
            Check(!await login.CancelAsync("unknown") && State(login, id) == "waiting_for_browser");
            Check(await login.CancelAsync(id));
            Check(State(login, id) == "native_login_cancelled" && !Directory.Exists(Path.Combine(directory, "native-login-" + id)));
            Check(!await login.CancelAsync(id));
            try { using var exited = Process.GetProcessById(launches[0]); Check(exited.HasExited); }
            catch (ArgumentException) { /* An exited helper may already be absent from the process table. */ }
            var next = await login.StartAsync("B");
            Check(next != id && launches.Count == 2); Check(await login.CancelAsync(next));
            Check(State(login, next) == "native_login_cancelled");
        }
        finally { foreach (var pid in launches) StopFixture(pid); }
    }
    public static async Task Failures(string root)
    {
        var directory = Path.Combine(root, "native-login-failures");
        using var http = new HttpClient(); var auth = new OAuth(http, new Vault(directory));
        foreach (var (mode, expected) in new[] { ("fail", "native_login_not_completed"), ("missing", "native_credential_file_missing"),
            ("wait", "native_login_timed_out"), ("unavailable", "native_login_helper_unavailable"), ("throw", "native_login_failed") })
        {
            var launches = new List<int>();
            var login = new NativeLogin(auth, directory, info =>
            {
                if (mode == "unavailable") return null;
                if (mode == "throw") throw new InvalidOperationException("synthetic helper failure");
                var process = StartFixture(mode, info.Environment["CODEX_HOME"]!); launches.Add(process.Id); return process;
            }, TimeSpan.FromSeconds(2));
            try
            {
                for (var index = 0; index < 2; index++)
                {
                    var id = await login.StartAsync("A"); await Finished(login, id);
                    Check(State(login, id) == expected);
                    Check(!Directory.Exists(Path.Combine(directory, "native-login-" + id)));
                }
            }
            finally { foreach (var pid in launches) StopFixture(pid); }
        }
        // Path validation previously ran before the try/finally and could strand the semaphore.
        var invalid = new NativeLogin(auth, "\0", _ => throw new InvalidOperationException(), TimeSpan.FromSeconds(2));
        for (var index = 0; index < 2; index++)
        {
            var id = await invalid.StartAsync("A"); await Finished(invalid, id); Check(State(invalid, id) == "native_login_failed");
        }
    }
    public static async Task InheritedPipe(string root)
    {
        var directory = Path.Combine(root, "native-login-pipe");
        using var http = new HttpClient(); var auth = new OAuth(http, new Vault(directory));
        int? childId = null;
        var login = new NativeLogin(auth, directory, info => StartFixture("pipe", info.Environment["CODEX_HOME"]!), TimeSpan.FromSeconds(3));
        var id = await login.StartAsync("A");
        var marker = Path.Combine(directory, "native-login-" + id, "fixture-child.pid");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!File.Exists(marker)) await Task.Delay(20, timeout.Token);
            childId = int.Parse(await File.ReadAllTextAsync(marker));
            await Finished(login, id); Check(State(login, id) == "native_login_timed_out");
            var next = await login.StartAsync("B"); Check(await login.CancelAsync(next));
        }
        finally { if (childId is { } pid) StopFixture(pid); }
    }
    public static async Task CleanupFailure(string root)
    {
        var directory = Path.Combine(root, "native-login-cleanup");
        using var http = new HttpClient(); var auth = new OAuth(http, new Vault(directory));
        var launches = new List<int>();
        var login = new NativeLogin(auth, directory, info =>
        {
            var process = StartFixture("cleanup", info.Environment["CODEX_HOME"]!); launches.Add(process.Id); return process;
        }, TimeSpan.FromSeconds(20));
        var id = await login.StartAsync("A");
        var profile = Path.Combine(directory, "native-login-" + id); var marker = Path.Combine(profile, "locked-fixture");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(marker)) await Task.Delay(20, timeout.Token);
            using (var held = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None)) Check(await login.CancelAsync(id));
            Check(State(login, id) == "credential_cleanup_required");
            var next = await login.StartAsync("B"); Check(await login.CancelAsync(next));
        }
        finally { foreach (var pid in launches) StopFixture(pid); }
    }
    private static void StopFixture(int pid)
    {
        try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(true); }
        catch (ArgumentException) { }
    }
}
