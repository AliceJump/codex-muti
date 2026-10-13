using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexPool.Core;

public sealed record ResponsesIdentity(string? CacheAffinity, string? ThreadId, string? ConversationId);

/// <summary>
/// Codex sends hyphenated <c>session-id</c> and <c>thread-id</c>. When both spellings are present,
/// the hyphen form is the one this proxy reads; underscore remains only as a legacy alias.
/// <c>session-id</c> is ChatGPT cache affinity and may be a <c>prompt_cache_key</c> override.
/// <c>thread-id</c> is the client thread. Neither selects the sticky account.
/// Account-bound turn state follows turn-metadata <c>session_id</c> when that field is present.
/// </summary>
public static class ResponsesHeaders
{
    // Forward each client spelling unchanged. Do not rewrite one into the other.
    public static readonly string[] Passthrough = ["session-id", "session_id", "thread-id", "thread_id", "originator", "User-Agent", "OpenAI-Beta",
        "x-openai-internal-codex-responses-lite", "x-codex-beta-features", "x-codex-turn-metadata", "x-codex-window-id"];

    public static ResponsesIdentity Read(IReadOnlyDictionary<string, string> headers)
    {
        var cache = First(headers, "session-id", "session_id");
        var thread = First(headers, "thread-id", "thread_id");
        // Metadata session_id is the conversation. The cache header is a fallback for older clients only.
        return new(cache, thread, MetadataSession(headers) ?? cache);
    }

    public static void BindTurnState(IDictionary<string, string> headers, ConcurrentDictionary<string, string> owners, string accountId)
    {
        var conversation = Read(headers).ConversationId;
        if (conversation is null) { headers.Remove("x-codex-turn-state"); return; }
        var hash = Hash(conversation);
        if (!owners.TryGetValue(hash, out var prior) || prior != accountId) headers.Remove("x-codex-turn-state");
        // Bounded transport ownership hints, never conversation contents or Codex workflow state.
        if (owners.Count >= 1024) owners.Clear();
        owners[hash] = accountId;
    }

    public static void Remember(IReadOnlyDictionary<string, string> headers, ConcurrentDictionary<string, string> owners, string accountId)
    {
        var conversation = Read(headers).ConversationId;
        if (conversation is not null) owners[Hash(conversation)] = accountId;
    }

    private static string? First(IReadOnlyDictionary<string, string> headers, string preferred, string alias)
    {
        if (headers.TryGetValue(preferred, out var value) && value.Length > 0) return value;
        return headers.TryGetValue(alias, out value) && value.Length > 0 ? value : null;
    }

    private static string? MetadataSession(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("x-codex-turn-metadata", out var json) || json.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("session_id", out var session)
                || session.ValueKind != JsonValueKind.String)
                return null;
            var value = session.GetString();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (JsonException) { return null; }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
