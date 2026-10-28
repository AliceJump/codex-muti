using System.Net;
using System.Text.Json;
using CodexPool.Core;

namespace CodexPool.Daemon;

public sealed class ModelCatalogFailure(ModelError error) : Exception(error.Code)
{ public ModelError Error { get; } = error; }
public sealed record ModelCatalogView(string AccountId, JsonElement Catalog);

// The deadline covers admission, response headers and the complete bounded body.
public sealed class ModelCatalog(HttpClient http, PoolRuntime pool, TimeSpan? requestTimeout = null)
{
    public async Task<ModelCatalogView> ReadAsync(string clientVersion, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        try
        {
            using var reservation = pool.ReserveCatalog();
            ModelError? lastError = null;
            await foreach (var account in pool.PrepareCandidatesAsync(reservation, token))
            {
                var native = account.Endpoint.Host == "chatgpt.com";
                using var request = new HttpRequestMessage(HttpMethod.Get, native
                    ? $"https://chatgpt.com/backend-api/codex/models?client_version={Uri.EscapeDataString(clientVersion)}" : "https://api.openai.com/v1/models");
                request.Headers.Authorization = new("Bearer", account.AccessToken);
                if (native) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account.BackendAccountId);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await ModelErrors.FromResponseAsync(response, token);
                    pool.RecordFailure(account.AccountId, error, account.AuthorizationVersion);
                    if (error.FailoverReason is not null) { lastError = error; continue; }
                    throw new ModelCatalogFailure(error);
                }
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var content = new MemoryStream(); var buffer = new byte[8192];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, token); if (count == 0) break;
                    if (content.Length + count > 2 * 1024 * 1024) throw new AuthFailure("oversized_model_catalog");
                    content.Write(buffer, 0, count);
                }
                using var json = JsonDocument.Parse(content.ToArray());
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                    throw new AuthFailure("unrecognized_model_catalog");
                return new(account.AccountId, json.RootElement.Clone());
            }
            if (lastError is not null) throw new ModelCatalogFailure(pool.Unavailable(lastError, reservation.CandidateIds.ToArray(), reservation.AutoSwitch));
            throw new NoEligibleAccountException();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new ModelCatalogFailure(new(504, "proxy_transport_error", "model_catalog_timeout", "模型目录查询超时，请稍后重试。")); }
    }
}
