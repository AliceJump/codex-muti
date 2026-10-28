using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CodexPool.Daemon;

// Compatibility adapter for official CLI login. Never opens the user's default CODEX_HOME.
public sealed class NativeLogin : IAsyncDisposable
{
    public const string NativeClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private readonly OAuth auth;
    private readonly string dataDirectory;
    private readonly Func<ProcessStartInfo, Process?> startHelper;
    private readonly TimeSpan loginTimeout;
    private readonly object gate = new();
    private Attempt? active;
    private bool stopping;
    private readonly ConcurrentDictionary<string, string> attempts = new();
    public NativeLogin(OAuth auth, string dataDirectory)
        : this(auth, dataDirectory, Process.Start, TimeSpan.FromMinutes(5)) { }
    internal NativeLogin(OAuth auth, string dataDirectory, Func<ProcessStartInfo, Process?> startHelper, TimeSpan loginTimeout)
    { this.auth = auth; this.dataDirectory = dataDirectory; this.startHelper = startHelper; this.loginTimeout = loginTimeout; }
    public object[] PublicAttempts => attempts.Select(x => (object)new { id = x.Key, state = x.Value }).ToArray();
    public bool IsRunning { get { lock (gate) return active is not null; } }
    public Task<string> StartAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new AuthFailure("invalid_account_name");
        Attempt attempt;
        lock (gate)
        {
            if (stopping) throw new AuthFailure("native_login_stopping");
            // Duplicate clicks/retries attach to the same browser flow instead of failing.
            if (active is not null)
            {
                if (active.Name == name && !active.Cancellation.IsCancellationRequested) return Task.FromResult(active.Id);
                throw new AuthFailure("native_login_already_running");
            }
            attempt = new(name); active = attempt; attempts[attempt.Id] = "waiting_for_browser";
        }
        _ = FinishAsync(attempt); return Task.FromResult(attempt.Id);
    }
    public async Task<bool> CancelAsync(string attemptId)
    {
        Task completion;
        lock (gate)
        {
            if (active is null || active.Id != attemptId) return false;
            completion = active.Completion.Task; active.Cancellation.Cancel();
        }
        // Acknowledge only after the helper and its isolated profile have been cleaned up.
        await completion; return true;
    }
    public Task StopAsync()
    {
        lock (gate)
        {
            stopping = true;
            if (active is null) return Task.CompletedTask;
            var completion = active.Completion.Task;
            active.Cancellation.Cancel();
            return completion;
        }
    }
    public ValueTask DisposeAsync() => new(StopAsync());
    private async Task FinishAsync(Attempt attempt)
    {
        // A fresh native grant is minted, transferred once to DPAPI, and never refreshed by helper.
        // The short-lived file exists only in a directory with the vault's inherited CurrentUser ACL.
        string? profile = null;
        var result = "native_login_failed";
        Process? helper = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token);
        timeout.CancelAfter(loginTimeout);
        try
        {
            profile = Path.GetFullPath(Path.Combine(dataDirectory, "native-login-" + attempt.Id));
            timeout.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(profile);
            var start = new ProcessStartInfo("codex") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["CODEX_HOME"] = profile;
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            start.ArgumentList.Add("login");
            helper = startHelper(start) ?? throw new AuthFailure("native_login_helper_unavailable");
            // Drain output without logging URLs, codes or credentials. Official login owns browser UX.
            var stdout = DrainAsync(helper.StandardOutput, timeout.Token); var stderr = DrainAsync(helper.StandardError, timeout.Token);
            // Observe both drains even on timeout; inherited pipes must not hold the login forever.
            await Task.WhenAll(helper.WaitForExitAsync(timeout.Token), stdout, stderr);
            if (helper.ExitCode != 0) throw new AuthFailure("native_login_not_completed");
            var source = Path.Combine(profile, "auth.json");
            if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new AuthFailure("native_credential_file_missing");
            var credentialBytes = await File.ReadAllBytesAsync(source, timeout.Token);
            try
            {
                using var json = JsonDocument.Parse(credentialBytes); var token = json.RootElement.GetProperty("tokens");
                var idToken = token.GetProperty("id_token").GetString()!;
                var subject = await auth.ValidateIdTokenAsync(idToken, NativeClientId, null, timeout.Token);
                var jwt = new JsonWebToken(idToken);
                if (!jwt.TryGetClaim("https://api.openai.com/auth", out var metadata)) throw new AuthFailure("native_account_metadata_missing");
                using var claims = JsonDocument.Parse(metadata.Value);
                var backendId = token.GetProperty("account_id").GetString();
                var validatedBackendId = claims.RootElement.GetProperty("chatgpt_account_id").GetString();
                if (string.IsNullOrEmpty(backendId) || backendId != validatedBackendId) throw new AuthFailure("native_account_identity_mismatch");
                var accessToken = token.GetProperty("access_token").GetString()!;
                var refreshToken = token.GetProperty("refresh_token").GetString()!;
                if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken)) throw new AuthFailure("native_credential_invalid");
                var expiry = new JsonWebToken(accessToken).ValidTo;
                // Remove the helper's durable copy before publishing a daemon-owned copy.
                // A crash here loses this fresh grant and requires login again; it cannot race refresh.
                File.Delete(source);
                await auth.SaveValidatedLoginAsync(new() { Id = Guid.NewGuid().ToString("N"), Name = attempt.Name, Subject = subject, ClientId = NativeClientId,
                    ProviderKind = "native", BackendAccountId = backendId,
                    Plan = claims.RootElement.TryGetProperty("chatgpt_plan_type", out var plan) ? plan.GetString() : null,
                    AccessToken = accessToken, RefreshToken = refreshToken, IdToken = idToken, Scope = "native_codex",
                    ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)), UpdatedAt = DateTimeOffset.UtcNow }, timeout.Token);
                result = "completed";
            }
            finally { CryptographicOperations.ZeroMemory(credentialBytes); }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { result = attempt.Cancellation.IsCancellationRequested ? "native_login_cancelled" : "native_login_timed_out"; }
        catch (AuthFailure ex) { result = ex.Code; }
        catch { result = "native_login_failed"; }
        finally
        {
            try
            {
                if (helper is not null)
                {
                    try
                    {
                        if (!helper.HasExited) helper.Kill(true);
                        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await helper.WaitForExitAsync(cleanupTimeout.Token);
                    }
                    catch { result = "credential_cleanup_required"; }
                    finally { helper.Dispose(); }
                }
                // Validate owned absolute scope before recursive cleanup; never compute a shell delete.
                if (profile is not null)
                {
                    var parent = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (profile.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(profile) == "native-login-" + attempt.Id
                        && Directory.Exists(profile) && (File.GetAttributes(profile) & FileAttributes.ReparsePoint) == 0) Directory.Delete(profile, true);
                }
            }
            catch { result = "credential_cleanup_required"; }
            finally
            {
                lock (gate)
                {
                    attempts[attempt.Id] = result; active = null; attempt.Cancellation.Dispose();
                    attempt.Completion.TrySetResult();
                }
            }
        }
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        try { while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) > 0) Array.Clear(buffer); }
        finally { Array.Clear(buffer); }
    }
    private sealed class Attempt(string name)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; } = name;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
