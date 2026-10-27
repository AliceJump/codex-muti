using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexPool.Core;

namespace CodexPool.Daemon;

public sealed class AccountDeletionRejected(string code) : Exception(code)
{ public string Code { get; } = code; }

public sealed class AccountCredential
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Subject { get; init; }
    public required string ClientId { get; init; }
    public string ProviderKind { get; set; } = "siwc";
    public string? BackendAccountId { get; set; }
    public string? Plan { get; set; }
    public string? PlanSource { get; set; }
    public DateTimeOffset? PlanUpdatedAt { get; set; }
    public required string AccessToken { get; set; }
    public required string RefreshToken { get; set; }
    public required string IdToken { get; set; }
    public bool IdTokenMetadataStale { get; set; }
    public required string Scope { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Paused { get; set; }
    public bool Draining { get; set; }
    public DateTimeOffset? CooldownUntil { get; set; }
    public string? CooldownErrorCode { get; set; }
    public DateTimeOffset? CooldownObservedAt { get; set; }
    public string AuthStatus { get; set; } = "ready";
    // Changes only when a validated login replaces the grant, not on refresh.
    public Guid AuthorizationVersion { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? EarliestRefreshAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public override string ToString() => "AccountCredential [REDACTED]";
    public object PublicView(bool active, string? authHealth = null, QuotaSnapshot? usage = null)
    {
        var subscription = SubscriptionExpiry.Inspect(IdToken, IdTokenMetadataStale);
        return new { id = Id, name = Name, active,
        enabled = Enabled, paused = Paused, drain = Draining, cooldownUntil = CooldownUntil,
        state = !Enabled ? "disabled" : Paused ? "paused" : Draining ? "draining" : CooldownUntil > DateTimeOffset.UtcNow ? "cooldown" : active ? "active" : "standby",
        provider = ProviderKind, plan = Plan, planSource = PlanSource ?? "legacy", planUpdatedAt = PlanUpdatedAt,
        planState = (PlanSource is "validated_id_token" or "login_id_token") && subscription.TokenCurrent
            || usage?.Plan == Plan && PlanUpdatedAt == usage?.UpdatedAt && usage?.Fresh(DateTimeOffset.UtcNow) == true ? "current" : "historical",
        subscriptionActiveUntil = subscription.ActiveUntil, subscriptionState = subscription.State,
        subscriptionSyncReason = subscription.Reason,
        subscriptionDeadlineState = subscription.State switch { "current" => "reported", "not_reported" => "unknown", var status => status },
        planProvenance = usage?.Plan is not null && usage.Plan == Plan ? "quota" : Plan is not null ? "credential" : "unknown",
        subscriptionSource = "validated_id_token", subscriptionObservedAt = subscription.ObservedAt,
        auth = authHealth ?? (AuthStatus != "ready" ? AuthStatus : ExpiresAt > DateTimeOffset.UtcNow ? "ready" : "refresh_needed"), quota = usage, quotaUpdatedAt = usage?.UpdatedAt, updatedAt = UpdatedAt };
    }
}

public sealed class VaultDocument
{
    public int Version { get; set; } = 1;
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid();
    public string LocalKey { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string? ActiveAccountId { get; set; }
    public List<AccountCredential> Accounts { get; set; } = [];
    public PoolSettings Settings { get; set; } = new();
    public List<QuotaSnapshot> Quotas { get; set; } = [];
    public List<ModelAccessKey> ModelKeys { get; set; } = [];
    public override string ToString() => "VaultDocument [REDACTED]";
}

public sealed class Vault
{
    private readonly string directory;
    private readonly string path;
    private readonly object gate = new();
    private readonly Dictionary<string, (int Model, int Background)> uses = new(StringComparer.Ordinal);
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
    public ModelKeyView[] ModelKeys { get { lock (gate) return document.ModelKeys.Select(x => new ModelKeyView(x.Id, x.Name, x.CreatedAt)).ToArray(); } }
    public IssuedModelKey CreateModelKey(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("invalid_key_name");
        lock (gate)
        {
            if (document.ModelKeys.Count >= 100) throw new ArgumentException("model_key_limit");
            var secret = "cpk_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var record = new ModelAccessKey { Id = Guid.NewGuid().ToString("N"), Name = name, Hash = ModelAccessKey.Digest(secret), CreatedAt = DateTimeOffset.UtcNow };
            var next = CloneDocument(document); next.ModelKeys.Add(record); Persist(next); document = next;
            return new(record.Id, secret);
        }
    }
    public bool RevokeModelKey(string id)
    {
        lock (gate)
        {
            var next = CloneDocument(document);
            if (next.ModelKeys.RemoveAll(x => x.Id == id) == 0) return false;
            Persist(next); document = next; return true;
        }
    }
    public bool AcceptsModelKey(string authorization)
    {
        if (!authorization.StartsWith("Bearer cpk_", StringComparison.Ordinal) || authorization.Length != 75) return false;
        var hash = ModelAccessKey.Digest(authorization[7..]);
        lock (gate)
        {
            var accepted = false;
            foreach (var key in document.ModelKeys) accepted |= LocalCommands.EqualSecret(hash, key.Hash);
            return accepted;
        }
    }
    public IDisposable AcquireUse(string id, bool model = false)
    {
        lock (gate)
        {
            if (!document.Accounts.Any(x => x.Id == id)) throw new AuthFailure("account_not_found");
            var count = uses.GetValueOrDefault(id);
            uses[id] = model ? (count.Model + 1, count.Background) : (count.Model, count.Background + 1);
            return new AccountUse(this, id, model);
        }
    }
    private void ReleaseUse(string id, bool model)
    {
        lock (gate)
        {
            var count = uses[id];
            count = model ? (count.Model - 1, count.Background) : (count.Model, count.Background - 1);
            if (count == (0, 0)) uses.Remove(id); else uses[id] = count;
        }
    }
    private sealed class AccountUse(Vault vault, string id, bool model) : IDisposable
    {
        private Vault? owner = vault;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseUse(id, model);
        public override string ToString() => "AccountUse [REDACTED]";
    }
    public bool AcceptsNativeModelBearer(string bearer)
    {
        // Ingress proof is independent of sticky scheduling. Pausing a route does not log the user out.
        lock (gate)
        {
            var accepted = false;
            foreach (var account in document.Accounts.Where(x => x.ProviderKind == "native" && x.AuthStatus == "ready"))
                accepted |= !string.IsNullOrEmpty(account.AccessToken) && LocalCommands.EqualSecret(bearer, account.AccessToken);
            return accepted;
        }
    }
    public string HostId { get { lock (gate) return document.HostId; } }
    public string? ActiveId { get { lock (gate) return document.ActiveAccountId; } }
    public PoolSettings Settings { get { lock (gate) return document.Settings; } }
    public QuotaSnapshot[] Quotas { get { lock (gate) return document.Quotas.ToArray(); } }
    public void SaveSettings(PoolSettings settings)
    {
        StickyScheduler.Validate(settings);
        lock (gate) { var next = CloneDocument(document); next.Settings = settings; Persist(next); document = next; }
    }
    public bool SaveQuota(QuotaSnapshot snapshot, Guid? authorizationVersion = null)
    {
        lock (gate)
        {
            var next = CloneDocument(document);
            var account = next.Accounts.Single(x => x.Id == snapshot.AccountId);
            if (authorizationVersion is { } expected && account.AuthorizationVersion != expected) return false;
            next.Quotas.RemoveAll(x => x.AccountId == snapshot.AccountId); next.Quotas.Add(snapshot);
            if (snapshot.Plan is not null)
            { account.Plan = snapshot.Plan; account.PlanSource = snapshot.Source; account.PlanUpdatedAt = snapshot.UpdatedAt; }
            Persist(next); document = next; return true;
        }
    }
    public bool Cooldown(string id, DateTimeOffset until, string? errorCode = null, DateTimeOffset? observedAt = null, Guid? authorizationVersion = null)
    {
        lock (gate)
        {
            var next = CloneDocument(document); var account = next.Accounts.Single(x => x.Id == id);
            if (authorizationVersion is { } expected && account.AuthorizationVersion != expected) return false;
            account.CooldownUntil = until; account.CooldownErrorCode = errorCode; account.CooldownObservedAt = observedAt;
            Persist(next); document = next; return true;
        }
    }
    public void ClearQuotaCooldown(string id, DateTimeOffset observedAt)
    {
        lock (gate)
        {
            var current = document.Accounts.Single(x => x.Id == id);
            if (current.CooldownObservedAt != observedAt || !ModelErrors.IsUsageCode(current.CooldownErrorCode)) return;
            var next = CloneDocument(document); var account = next.Accounts.Single(x => x.Id == id);
            account.CooldownUntil = null; account.CooldownErrorCode = null; account.CooldownObservedAt = null;
            Persist(next); document = next;
        }
    }
    public void ClearLegacyCooldown(string id, DateTimeOffset expectedUntil)
    {
        lock (gate)
        {
            var current = document.Accounts.Single(x => x.Id == id);
            if (current.CooldownUntil != expectedUntil || current.CooldownErrorCode is not null || current.CooldownObservedAt is not null) return;
            var next = CloneDocument(document);
            next.Accounts.Single(x => x.Id == id).CooldownUntil = null;
            Persist(next); document = next;
        }
    }
    public int DistinctUsers { get { lock (gate) return document.Accounts.Select(x => x.Subject).Distinct(StringComparer.Ordinal).Count(); } }
    public int NativeDistinctUsers { get { lock (gate) return document.Accounts.Where(x => x.ProviderKind == "native").Select(x => x.Subject).Distinct(StringComparer.Ordinal).Count(); } }
    public object[] PublicAccounts => Views(_ => null);
    public object[] Views(Func<string, string?> authHealth, Func<string, QuotaSnapshot?>? quotaView = null) { lock (gate) return document.Accounts.Select(x => x.PublicView(x.Id == document.ActiveAccountId, authHealth(x.Id), quotaView is null ? document.Quotas.FirstOrDefault(q => q.AccountId == x.Id) : quotaView(x.Id))).ToArray(); }
    public AccountCredential? Selected { get { lock (gate) return Clone(document.Accounts.FirstOrDefault(x => x.Id == document.ActiveAccountId)); } }
    public AccountCredential? Find(string id) { lock (gate) return Clone(document.Accounts.FirstOrDefault(x => x.Id == id)); }
    public AccountCredential[] Accounts { get { lock (gate) return document.Accounts.Select(x => Clone(x)!).ToArray(); } }
    private static bool SameRegistration(AccountCredential existing, AccountCredential incoming) =>
        existing.ProviderKind == incoming.ProviderKind && existing.ClientId == incoming.ClientId
        && (incoming.ProviderKind != "native" || existing.Subject == incoming.Subject && existing.BackendAccountId == incoming.BackendAccountId);
    public string? RegistrationId(AccountCredential credential)
    { lock (gate) return document.Accounts.FirstOrDefault(x => SameRegistration(x, credential))?.Id; }
    public string Add(AccountCredential credential)
    {
        lock (gate)
        {
            if (credential.Plan is not null)
            { credential.PlanSource = "login_id_token"; credential.PlanUpdatedAt = credential.UpdatedAt; }
            var next = CloneDocument(document);
            credential.AuthorizationVersion = Guid.NewGuid();
            var duplicate = next.Accounts.FindIndex(x => SameRegistration(x, credential));
            if (duplicate >= 0)
            {
                var existing = next.Accounts[duplicate];
                if (existing.Subject != credential.Subject) throw new InvalidOperationException("Account identity mismatch.");
                credential = new() { Id = existing.Id, Name = existing.Name, Subject = credential.Subject, ClientId = credential.ClientId,
                    AccessToken = credential.AccessToken, RefreshToken = credential.RefreshToken, IdToken = credential.IdToken,
                    Scope = credential.Scope, ExpiresAt = credential.ExpiresAt, EarliestRefreshAt = credential.EarliestRefreshAt, UpdatedAt = credential.UpdatedAt,
                    Enabled = existing.Enabled, Paused = existing.Paused, Draining = existing.Draining, CooldownUntil = existing.CooldownUntil,
                    CooldownErrorCode = existing.CooldownErrorCode, CooldownObservedAt = existing.CooldownObservedAt,
                    ProviderKind = credential.ProviderKind, BackendAccountId = credential.BackendAccountId, Plan = credential.Plan,
                    AuthorizationVersion = credential.AuthorizationVersion,
                    PlanSource = credential.PlanSource, PlanUpdatedAt = credential.PlanUpdatedAt };
                // A new grant proves authentication recovery, not quota or model access.
                if (ModelErrors.IsAuthCode(credential.CooldownErrorCode))
                { credential.CooldownUntil = null; credential.CooldownErrorCode = null; credential.CooldownObservedAt = null; }
                next.Accounts[duplicate] = credential;
            }
            else next.Accounts.Add(credential);
            next.ActiveAccountId ??= credential.Id;
            Persist(next); document = next; return credential.Id;
        }
    }
    public bool ActivateForProbe(string id, bool allowRefreshing = false)
    {
        lock (gate)
        {
            if (!document.Accounts.Any(x => x.Id == id && (IsEligible(x)
                || allowRefreshing && x.AuthStatus == "refresh_pending" && x.Enabled && !x.Paused && !x.Draining && !(x.CooldownUntil > DateTimeOffset.UtcNow)))) return false;
            var next = CloneDocument(document); next.ActiveAccountId = id; Persist(next); document = next; return true;
        }
    }
    public static bool IsEligible(AccountCredential account) => account.Enabled && !account.Paused && !account.Draining && account.AuthStatus == "ready" && !(account.CooldownUntil > DateTimeOffset.UtcNow);
    public void RequireReauthorization(string id) => SetAuthStatus(id, "reauth_required");
    public void SetAuthStatus(string id, string status)
    {
        if (status is not ("ready" or "refresh_pending" or "reauth_required")) throw new ArgumentException("Invalid auth status.", nameof(status));
        lock (gate)
        {
            var next = CloneDocument(document); next.Accounts.Single(x => x.Id == id).AuthStatus = status;
            Persist(next); document = next;
        }
    }
    public bool Rename(string id, string? name)
    {
        if (name is null || name.Any(char.IsControl)) throw new ArgumentException("invalid_account_name", nameof(name));
        name = name.Trim();
        if (name.Length is < 1 or > 80) throw new ArgumentException("invalid_account_name", nameof(name));
        lock (gate)
        {
            var next = CloneDocument(document); var target = next.Accounts.FirstOrDefault(x => x.Id == id);
            if (target is null) return false;
            target.Name = name; // Only the local display label changes; grants and scheduler IDs remain stable.
            Persist(next); document = next; return true;
        }
    }
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
    /// <summary>
    /// Permanently removes credentials, control flags and quota for the account.
    /// The same lock coordinates deletion and all upstream-operation reservations.
    /// PoolRuntime must synchronize its owner after durable removal, including an empty pool.
    /// </summary>
    public bool Delete(string id, out string? newActiveId, bool allowMigration = true,
        Func<AccountCredential, bool>? eligible = null)
    {
        lock (gate)
        {
            newActiveId = null;
            var next = CloneDocument(document);
            var index = next.Accounts.FindIndex(x => x.Id == id);
            if (index < 0) return false;
            var use = uses.GetValueOrDefault(id);
            if (use.Model > 0) throw new AccountDeletionRejected("account_in_flight");
            if (use.Background > 0) throw new AccountDeletionRejected("account_busy");
            var wasActive = next.ActiveAccountId == id;
            AccountCredential? standby = null;
            if (wasActive && next.Accounts.Count > 1)
            {
                if (!allowMigration) throw new AccountDeletionRejected("live_migration_gate_pending");
                standby = next.Accounts.FirstOrDefault(x => x.Id != id && (eligible ?? IsEligible)(x));
                if (standby is null) throw new AccountDeletionRejected("activate_another_first");
            }
            next.Accounts.RemoveAt(index);
            next.Quotas.RemoveAll(x => x.AccountId == id);
            if (wasActive)
            {
                next.ActiveAccountId = standby?.Id;
                newActiveId = next.ActiveAccountId;
            }
            else newActiveId = next.ActiveAccountId;
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
            if (!string.IsNullOrEmpty(tokens.IdToken))
            {
                target.IdToken = tokens.IdToken;
                var metadata = SubscriptionExpiry.Inspect(tokens.IdToken);
                // A returned, validated ID token can update the plan independently
                // of usage. Never let older claims overwrite a newer usage response.
                if (metadata.ObservedAt is { } issuedAt && metadata.TokenCurrent
                    && !(target.PlanSource == "native_codex_usage" && target.PlanUpdatedAt > issuedAt)
                    && SubscriptionExpiry.PlanFromIdToken(tokens.IdToken) is { } plan)
                { target.Plan = plan; target.PlanSource = "validated_id_token"; target.PlanUpdatedAt = issuedAt; }
            }
            // A successful refresh may omit ID metadata. Keeping the old token for
            // identity hints must not present its subscription claims as refreshed.
            target.IdTokenMetadataStale = string.IsNullOrEmpty(tokens.IdToken);
            target.Scope = tokens.Scope; target.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn);
            target.AuthStatus = "ready";
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
