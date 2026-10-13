using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CodexPool.Daemon;

public sealed class AuthFailure(string code) : Exception(code) { public string Code { get; } = code; }
public sealed class OAuth(HttpClient http, Vault vault)
{
    private const string Issuer = "https://auth.openai.com";
    private const string Resource = "https://api.openai.com/v1";
    private const string Redirect = "http://127.0.0.1:3210/auth/callback";
    private const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private readonly ConcurrentDictionary<string, PendingLogin> pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> health = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> refreshBackoff = new(StringComparer.Ordinal);
    private Metadata? metadata;
    public string? Health(string accountId) => health.TryGetValue(accountId, out var value) ? value : null;

    public async Task<string> BeginAsync(string name, string? accountId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new AuthFailure("invalid_account_name");
        foreach (var pair in pending.Where(x => x.Value.ExpiresAt < DateTimeOffset.UtcNow)) pending.TryRemove(pair.Key, out _);
        if (pending.Count >= 8) throw new AuthFailure("too_many_login_attempts");
        var account = accountId is null ? null : vault.Find(accountId) ?? throw new AuthFailure("account_not_found");
        var endpoints = await GetMetadataAsync(cancellationToken);
        var state = RandomValue(); var nonce = RandomValue(); var verifier = RandomValue();
        var clientId = account?.ClientId ?? "dynamic_agent_client";
        pending[state] = new(name, nonce, verifier, account?.Id, clientId, DateTimeOffset.UtcNow.AddMinutes(10));
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["ext_agent_host_id"] = vault.HostId, ["response_type"] = "code",
            ["redirect_uri"] = Redirect, ["scope"] = Scopes, ["resource"] = Resource,
            ["state"] = state, ["nonce"] = nonce, ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        };
        if (account is null) fields["agent_name_hint"] = "Codex Pool";
        else fields["id_token_hint"] = account.IdToken;
        return endpoints.Authorize + "?" + string.Join("&", fields.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
    }

    public async Task<string> CompleteAsync(string state, string code, string? issuedClientId, string? error, CancellationToken cancellationToken)
    {
        if (!pending.TryRemove(state, out var login) || login.ExpiresAt < DateTimeOffset.UtcNow) throw new AuthFailure("invalid_login_state");
        if (error is not null) throw new AuthFailure("authorization_denied");
        if (string.IsNullOrEmpty(code)) throw new AuthFailure("missing_authorization_code");
        using var use = login.AccountId is null ? null : vault.AcquireUse(login.AccountId);
        var clientId = login.ClientId == "dynamic_agent_client" ? issuedClientId : login.ClientId;
        if (string.IsNullOrWhiteSpace(clientId) || clientId == "dynamic_agent_client" || clientId.Length > 256 || clientId.Any(char.IsControl))
            throw new AuthFailure("missing_issued_client_id");
        if (login.ClientId != "dynamic_agent_client" && issuedClientId is not null && issuedClientId != login.ClientId)
            throw new AuthFailure("client_identity_mismatch");
        var token = await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId,
            ["code"] = code, ["code_verifier"] = login.Verifier, ["redirect_uri"] = Redirect, ["resource"] = Resource }, cancellationToken);
        var subject = await ValidateIdTokenAsync(token.IdToken, clientId, login.Nonce, cancellationToken);
        if (login.AccountId is not null && vault.Find(login.AccountId)?.Subject != subject) throw new AuthFailure("account_identity_mismatch");
        var savedId = vault.Add(new() { Id = login.AccountId ?? Guid.NewGuid().ToString("N"), Name = login.Name, Subject = subject,
            ClientId = clientId, AccessToken = token.AccessToken, RefreshToken = token.RefreshToken, IdToken = token.IdToken,
            Scope = token.Scope, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
            EarliestRefreshAt = token.EarliestRefreshAt, UpdatedAt = DateTimeOffset.UtcNow });
        health.TryRemove(savedId, out _); refreshBackoff.TryRemove(savedId, out _); return savedId;
    }

    public async Task<AccountCredential> ReadyAsync(string id, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        using var use = vault.AcquireUse(id);
        var refreshLock = refreshLocks.GetOrAdd(id, _ => new(1, 1)); await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var account = vault.Find(id) ?? throw new AuthFailure("account_not_found");
            if (account.AuthStatus is "reauth_required" or "refresh_pending" || health.TryGetValue(id, out var status) && status == "reauth_required") throw new AuthFailure("reauth_required");
            if (!forceRefresh && account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return account;
            if (refreshBackoff.TryGetValue(id, out var retryAt) && retryAt > DateTimeOffset.UtcNow) throw new AuthFailure("auth_temporarily_unavailable");
            if (account.EarliestRefreshAt > DateTimeOffset.UtcNow) throw new AuthFailure("refresh_not_yet_allowed");
            TokenResponse token;
            // Crash between remote rotation and local replacement must not reuse the old token.
            vault.SetAuthStatus(id, "refresh_pending");
            try { token = account.ProviderKind == "native" ? await RefreshNativeAsync(account, cancellationToken)
                : await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId,
                    ["refresh_token"] = account.RefreshToken, ["resource"] = Resource }, cancellationToken); }
            catch (AuthFailure ex)
            {
                health[id] = ex.Code == "auth_temporarily_unavailable" ? ex.Code : "reauth_required";
                if (health[id] == "reauth_required") vault.RequireReauthorization(id);
                if (ex.Code == "auth_temporarily_unavailable")
                { vault.SetAuthStatus(id, "ready"); refreshBackoff[id] = DateTimeOffset.UtcNow.AddSeconds(30); }
                throw;
            }
            catch (OperationCanceledException)
            {
                // Token endpoint may have rotated while the response was lost. Never reuse blindly.
                health[id] = "reauth_required";
                vault.RequireReauthorization(id);
                throw new AuthFailure("refresh_outcome_unknown");
            }
            catch (HttpRequestException)
            {
                health[id] = "reauth_required";
                vault.RequireReauthorization(id);
                throw new AuthFailure("refresh_outcome_unknown");
            }
            try
            {
                if (token.IdToken.Length > 0 && await ValidateIdTokenAsync(token.IdToken, account.ClientId, null, cancellationToken) != account.Subject)
                    throw new AuthFailure("account_identity_mismatch");
            }
            catch { health[id] = "reauth_required"; vault.RequireReauthorization(id); throw; }
            try { vault.ReplaceTokens(id, account.RefreshToken, token); }
            catch
            { health[id] = "reauth_required"; throw new AuthFailure("rotated_token_persistence_failed"); }
            health.TryRemove(id, out _); refreshBackoff.TryRemove(id, out _); return vault.Find(id)!;
        }
        finally { refreshLock.Release(); }
    }

    public async Task<string> ValidateIdTokenAsync(string token, string clientId, string? nonce, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new AuthFailure("missing_id_token");
        var endpoints = await GetMetadataAsync(cancellationToken);
        var jwks = new JsonWebKeySet(await GetJsonTextAsync(endpoints.Jwks, cancellationToken));
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true, IssuerSigningKeys = jwks.GetSigningKeys(), RequireSignedTokens = true,
            ValidateIssuer = true, ValidIssuer = Issuer, ValidateAudience = true, ValidAudience = clientId,
            ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
                SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512]
        });
        if (!validation.IsValid || validation.SecurityToken is not JsonWebToken jwt) throw new AuthFailure("invalid_id_token");
        if (jwt.TryGetClaim("azp", out var authorizedParty) && authorizedParty.Value != clientId
            || jwt.Audiences.Count() > 1 && !jwt.TryGetClaim("azp", out _)) throw new AuthFailure("invalid_authorized_party");
        if (nonce is not null && (!jwt.TryGetClaim("nonce", out var claim) || !FixedEquals(claim.Value, nonce))) throw new AuthFailure("invalid_oidc_nonce");
        if (string.IsNullOrWhiteSpace(jwt.Subject)) throw new AuthFailure("missing_account_subject");
        return jwt.Subject;
    }
    private async Task<TokenResponse> RefreshNativeAsync(AccountCredential account, CancellationToken cancellationToken)
    {
        // Matches the pinned official codex-login native refresh wire shape; separate from SIWC.
        using var response = await http.PostAsJsonAsync(Issuer + "/oauth/token", new
        { grant_type = "refresh_token", client_id = account.ClientId, refresh_token = account.RefreshToken }, cancellationToken);
        var text = await ReadLimitedAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new AuthFailure(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized ? "reauth_required" : "auth_temporarily_unavailable");
        var token = JsonSerializer.Deserialize<TokenResponse>(text) ?? throw new AuthFailure("invalid_token_response");
        if (string.IsNullOrEmpty(token.AccessToken)) throw new AuthFailure("invalid_token_response");
        if (string.IsNullOrEmpty(token.RefreshToken)) token.RefreshToken = account.RefreshToken;
        var seconds = (new JsonWebToken(token.AccessToken).ValidTo - DateTime.UtcNow).TotalSeconds;
        if (seconds < 1) throw new AuthFailure("invalid_token_expiry");
        token.Scope = "native_codex"; token.ExpiresIn = (int)Math.Min(seconds, 86400);
        if (!string.IsNullOrEmpty(token.IdToken))
        {
            var jwt = new JsonWebToken(token.IdToken);
            if (!jwt.TryGetClaim("https://api.openai.com/auth", out var metadata)) throw new AuthFailure("native_account_metadata_missing");
            using var claims = JsonDocument.Parse(metadata.Value);
            if (claims.RootElement.GetProperty("chatgpt_account_id").GetString() != account.BackendAccountId) throw new AuthFailure("account_identity_mismatch");
        }
        return token;
    }

    private async Task<TokenResponse> ExchangeAsync(Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        var endpoints = await GetMetadataAsync(cancellationToken);
        using var response = await http.PostAsync(endpoints.Token, new FormUrlEncodedContent(fields), cancellationToken);
        var text = await ReadLimitedAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string? code = null;
            try { using var body = JsonDocument.Parse(text); if (body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) code = error.GetString(); } catch (JsonException) { }
            throw new AuthFailure(response.StatusCode == HttpStatusCode.Unauthorized || code == "invalid_grant" ? "reauth_required" : "auth_temporarily_unavailable");
        }
        TokenResponse token;
        try { token = JsonSerializer.Deserialize<TokenResponse>(text) ?? throw new AuthFailure("invalid_token_response"); }
        catch (JsonException) { throw new AuthFailure("invalid_token_response"); }
        if (token.TokenType != "Bearer" && token.TokenType != "bearer") throw new AuthFailure("invalid_token_type");
        if (token.AccessToken.Length == 0 || token.RefreshToken.Length == 0 || token.ExpiresIn <= 0 || token.ExpiresIn > 86400)
            throw new AuthFailure("invalid_token_response");
        if (!token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal))
            throw new AuthFailure("missing_plan_permission");
        using var json = JsonDocument.Parse(text);
        if (json.RootElement.TryGetProperty("earliest_refresh_at", out var refresh) && refresh.ValueKind == JsonValueKind.Number && refresh.TryGetInt64(out var seconds))
            token.EarliestRefreshAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return token;
    }
    private async Task<Metadata> GetMetadataAsync(CancellationToken cancellationToken)
    {
        if (metadata is not null) return metadata;
        using var json = JsonDocument.Parse(await GetJsonTextAsync(Issuer + "/.well-known/openid-configuration", cancellationToken));
        if (json.RootElement.GetProperty("issuer").GetString() != Issuer) throw new AuthFailure("invalid_issuer_metadata");
        string Endpoint(string name)
        {
            var value = json.RootElement.GetProperty(name).GetString()!;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "auth.openai.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
                throw new AuthFailure("invalid_issuer_endpoint");
            return value;
        }
        metadata = new(Endpoint("authorization_endpoint"), Endpoint("token_endpoint"), Endpoint("jwks_uri")); return metadata;
    }
    private async Task<string> GetJsonTextAsync(string url, CancellationToken token)
    { using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token); if (!response.IsSuccessStatusCode) throw new AuthFailure("auth_temporarily_unavailable"); return await ReadLimitedAsync(response.Content, token); }
    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken token)
    {
        await using var input = await content.ReadAsStreamAsync(token); using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await input.ReadAsync(buffer, token); if (count == 0) break; if (output.Length + count > 1024 * 1024) throw new AuthFailure("oversized_auth_response"); output.Write(buffer, 0, count); }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private static string RandomValue() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    private sealed record Metadata(string Authorize, string Token, string Jwks);
    private sealed record PendingLogin(string Name, string Nonce, string Verifier, string? AccountId, string ClientId, DateTimeOffset ExpiresAt)
    { public override string ToString() => "PendingLogin [REDACTED]"; }
}
