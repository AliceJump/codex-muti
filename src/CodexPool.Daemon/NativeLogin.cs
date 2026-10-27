using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CodexPool.Daemon;

// Compatibility adapter for official CLI login. Never opens the user's default CODEX_HOME.
public sealed class NativeLogin(OAuth auth, string dataDirectory)
{
    public const string NativeClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private readonly SemaphoreSlim loginLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string> attempts = new();
    public object[] PublicAttempts => attempts.Select(x => (object)new { id = x.Key, state = x.Value }).ToArray();
    public async Task<string> StartAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new AuthFailure("invalid_account_name");
        if (!await loginLock.WaitAsync(0)) throw new AuthFailure("native_login_already_running");
        var attemptId = Guid.NewGuid().ToString("N"); attempts[attemptId] = "waiting_for_browser";
        _ = FinishAsync(attemptId, name); return attemptId;
    }
    private async Task FinishAsync(string attemptId, string name)
    {
        // A fresh native grant is minted, transferred once to DPAPI, and never refreshed by helper.
        // The short-lived file exists only in a directory with the vault's inherited CurrentUser ACL.
        var profile = Path.GetFullPath(Path.Combine(dataDirectory, "native-login-" + attemptId));
        Process? helper = null;
        try
        {
            Directory.CreateDirectory(profile);
            var start = new ProcessStartInfo("codex") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["CODEX_HOME"] = profile;
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            start.ArgumentList.Add("login");
            helper = Process.Start(start) ?? throw new AuthFailure("native_login_helper_unavailable");
            // Drain output without logging URLs, codes or credentials. Official login owns browser UX.
            var stdout = DrainAsync(helper.StandardOutput); var stderr = DrainAsync(helper.StandardError);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await helper.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr);
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
                await auth.SaveValidatedLoginAsync(new() { Id = Guid.NewGuid().ToString("N"), Name = name, Subject = subject, ClientId = NativeClientId,
                    ProviderKind = "native", BackendAccountId = backendId,
                    Plan = claims.RootElement.TryGetProperty("chatgpt_plan_type", out var plan) ? plan.GetString() : null,
                    AccessToken = accessToken, RefreshToken = refreshToken, IdToken = idToken, Scope = "native_codex",
                    ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)), UpdatedAt = DateTimeOffset.UtcNow }, timeout.Token);
                attempts[attemptId] = "completed";
            }
            finally { CryptographicOperations.ZeroMemory(credentialBytes); }
        }
        catch (AuthFailure ex) { attempts[attemptId] = ex.Code; }
        catch { attempts[attemptId] = "native_login_failed"; }
        finally
        {
            if (helper is not null) { if (!helper.HasExited) helper.Kill(true); helper.Dispose(); }
            // Validate owned absolute scope before recursive cleanup; never compute a shell delete.
            var parent = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            try { if (profile.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(profile) == "native-login-" + attemptId
                    && Directory.Exists(profile) && (File.GetAttributes(profile) & FileAttributes.ReparsePoint) == 0) Directory.Delete(profile, true); }
            catch { attempts[attemptId] = "credential_cleanup_required"; }
            loginLock.Release();
        }
    }
    private static async Task DrainAsync(StreamReader reader)
    { var buffer = new char[2048]; while (await reader.ReadAsync(buffer) > 0) Array.Clear(buffer); }
}
