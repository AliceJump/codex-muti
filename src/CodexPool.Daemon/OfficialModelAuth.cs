using System.Security.Cryptography;
using System.Text.Json;

namespace CodexPool.Daemon;

// Read-only ingress authentication. Codex remains the sole owner of its login and refresh.
// No official credential is imported into the pool or used for upstream account selection.
public sealed class OfficialModelAuth(string codexHome, Vault vault)
{
    private const int MaximumFileBytes = 128 * 1024;
    public async Task<bool> AcceptsAsync(string method, string path, string authorization, CancellationToken cancellationToken)
    {
        var modelEndpoint = path == "/v1/responses" && method is "POST" or "GET"
            || path == "/v1/models" && method == "GET";
        if (!modelEndpoint
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || authorization.Length is < 8 or > 16384) return false;
        var bearer = authorization[7..];
        if (vault.AcceptsNativeModelBearer(bearer)) return true;
        var file = Path.Combine(codexHome, "auth.json");
        byte[]? bytes = null;
        try
        {
            if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
            await using var source = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (source.Length is <= 0 or > MaximumFileBytes) return false;
            // Read only a bounded record; a concurrently rotated file must never expand the allocation.
            bytes = new byte[MaximumFileBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await source.ReadAsync(bytes.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
            if (count == 0 || count > MaximumFileBytes) return false;
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("auth_mode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() != "chatgpt"
                || !root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object
                || !tokens.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String
                || !tokens.TryGetProperty("account_id", out var account) || account.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(account.GetString())) return false;
            var expected = access.GetString();
            return !string.IsNullOrEmpty(expected) && LocalCommands.EqualSecret(bearer, expected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
