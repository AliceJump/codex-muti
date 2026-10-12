using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexPool.Daemon;

public sealed class AccountCredential
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Subject { get; init; }
    public required string ClientId { get; init; }
    public required string AccessToken { get; set; }
    public required string RefreshToken { get; set; }
    public required string IdToken { get; set; }
    public required string Scope { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Paused { get; set; }
    public bool Draining { get; set; }
    public DateTimeOffset? CooldownUntil { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? EarliestRefreshAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public override string ToString() => "AccountCredential [REDACTED]";
    public object PublicView(bool active, string? authHealth = null) => new { id = Id, name = Name, active,
        enabled = Enabled, paused = Paused, drain = Draining, cooldownUntil = CooldownUntil,
        state = !Enabled ? "disabled" : Paused ? "paused" : Draining ? "draining" : CooldownUntil > DateTimeOffset.UtcNow ? "cooldown" : active ? "active" : "standby",
        plan = (string?)null, auth = authHealth ?? (ExpiresAt > DateTimeOffset.UtcNow ? "ready" : "refresh_needed"), quota = (object?)null, updatedAt = UpdatedAt };
}

public sealed class VaultDocument
{
    public int Version { get; set; } = 1;
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid();
    public string LocalKey { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string? ActiveAccountId { get; set; }
    public List<AccountCredential> Accounts { get; set; } = [];
    public override string ToString() => "VaultDocument [REDACTED]";
}

public sealed class Vault
{
    private readonly string directory;
    private readonly string path;
    private readonly object gate = new();
    private VaultDocument document;
    public Vault(string directory)
    {
        this.directory = Path.GetFullPath(directory); path = Path.Combine(this.directory, "vault.dpapi");
        var info = Directory.CreateDirectory(this.directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe storage directory.");
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user unavailable.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(user);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
        document = File.Exists(path) ? Read(path) : new();
        if (document.Version != 1) throw new InvalidOperationException("Unsupported vault version.");
        if (!File.Exists(path)) Persist(document);
    }
    public string LocalKey { get { lock (gate) return document.LocalKey; } }
    public string HostId { get { lock (gate) return document.HostId; } }
    public string? ActiveId { get { lock (gate) return document.ActiveAccountId; } }
    public int DistinctUsers { get { lock (gate) return document.Accounts.Select(x => x.Subject).Distinct(StringComparer.Ordinal).Count(); } }
    public object[] PublicAccounts => Views(_ => null);
    public object[] Views(Func<string, string?> authHealth) { lock (gate) return document.Accounts.Select(x => x.PublicView(x.Id == document.ActiveAccountId, authHealth(x.Id))).ToArray(); }
    public AccountCredential? Selected { get { lock (gate) return Clone(document.Accounts.FirstOrDefault(x => x.Id == document.ActiveAccountId)); } }
    public AccountCredential? Find(string id) { lock (gate) return Clone(document.Accounts.FirstOrDefault(x => x.Id == id)); }
    public string Add(AccountCredential credential)
    {
        lock (gate)
        {
            var next = CloneDocument(document);
            var duplicate = next.Accounts.FindIndex(x => x.ClientId == credential.ClientId);
            if (duplicate >= 0)
            {
                var existing = next.Accounts[duplicate];
                if (existing.Subject != credential.Subject) throw new InvalidOperationException("Account identity mismatch.");
                credential = new() { Id = existing.Id, Name = existing.Name, Subject = credential.Subject, ClientId = credential.ClientId,
                    AccessToken = credential.AccessToken, RefreshToken = credential.RefreshToken, IdToken = credential.IdToken,
                    Scope = credential.Scope, ExpiresAt = credential.ExpiresAt, EarliestRefreshAt = credential.EarliestRefreshAt, UpdatedAt = credential.UpdatedAt,
                    Enabled = existing.Enabled, Paused = existing.Paused, Draining = existing.Draining, CooldownUntil = existing.CooldownUntil };
                next.Accounts[duplicate] = credential;
            }
            else next.Accounts.Add(credential);
            next.ActiveAccountId ??= credential.Id;
            Persist(next); document = next; return credential.Id;
        }
    }
    public bool ActivateForProbe(string id)
    {
        lock (gate)
        {
            if (!document.Accounts.Any(x => x.Id == id && IsEligible(x))) return false;
            var next = CloneDocument(document); next.ActiveAccountId = id; Persist(next); document = next; return true;
        }
    }
    public static bool IsEligible(AccountCredential account) => account.Enabled && !account.Paused && !account.Draining && !(account.CooldownUntil > DateTimeOffset.UtcNow);
    public bool Control(string id, string operation)
    {
        lock (gate)
        {
            var next = CloneDocument(document); var target = next.Accounts.FirstOrDefault(x => x.Id == id);
            if (target is null) return false;
            switch (operation)
            {
                case "pause": target.Paused = true; break;
                case "resume": target.Paused = false; break;
                case "drain": target.Draining = true; break;
                case "undrain": target.Draining = false; break;
                case "enable": target.Enabled = true; break;
                case "disable": target.Enabled = false; break;
                default: throw new ArgumentException("Unknown control.", nameof(operation));
            }
            Persist(next); document = next; return true;
        }
    }
    public void ReplaceTokens(string id, string expectedRefreshToken, TokenResponse tokens)
    {
        lock (gate)
        {
            var next = CloneDocument(document); var target = next.Accounts.Single(x => x.Id == id);
            if (target.RefreshToken != expectedRefreshToken) throw new InvalidOperationException("Refresh generation changed.");
            target.AccessToken = tokens.AccessToken; target.RefreshToken = tokens.RefreshToken;
            if (!string.IsNullOrEmpty(tokens.IdToken)) target.IdToken = tokens.IdToken;
            target.Scope = tokens.Scope; target.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn);
            target.EarliestRefreshAt = tokens.EarliestRefreshAt; target.UpdatedAt = DateTimeOffset.UtcNow;
            Persist(next); document = next;
        }
    }
    public static string ReadLocalKey(string directory) => Read(Path.Combine(directory, "vault.dpapi")).LocalKey;
    private static VaultDocument Read(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe vault file.");
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<VaultDocument>(plain) ?? throw new InvalidOperationException("Invalid vault."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void Persist(VaultDocument value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value); byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temp = Path.Combine(directory, "vault-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(encrypted); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static AccountCredential? Clone(AccountCredential? value) => value is null ? null : JsonSerializer.Deserialize<AccountCredential>(JsonSerializer.Serialize(value));
    private static VaultDocument CloneDocument(VaultDocument value) => JsonSerializer.Deserialize<VaultDocument>(JsonSerializer.Serialize(value))!;
}

public sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("id_token")] public string IdToken { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("token_type")] public string TokenType { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonIgnore] public DateTimeOffset? EarliestRefreshAt { get; set; }
    public override string ToString() => "TokenResponse [REDACTED]";
}
