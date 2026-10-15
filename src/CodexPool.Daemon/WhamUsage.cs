using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexPool.Daemon;

/// <summary>
/// ChatGPT Desktop 在 chatgpt_base_url 指向本机时会请求 /backend-api/wham/usage。
/// 此处按模型路由可用性改写 rate_limit.allowed，从而在池内仍有可路由账户时解除 Desktop 的发送硬阻塞，
/// 同时保留用户本机 ChatGPT 登录供远端能力使用。仅 loopback 可达。
/// </summary>
public static class WhamUsage
{
    public const int MaxResponseBytes = 1024 * 1024;

    public static byte[] Rewrite(ReadOnlyMemory<byte> upstream, bool allowed, Func<string, bool>? canRouteModel = null)
    {
        if (upstream.Length > MaxResponseBytes) throw new JsonException();
        if (JsonNode.Parse(upstream.Span) is not JsonObject body || body["rate_limit"] is not JsonObject limit
            || body["user_id"] is not JsonValue user || !user.TryGetValue<string>(out _)
            || body["account_id"] is not JsonValue account || !account.TryGetValue<string>(out _)
            || body["plan_type"] is not JsonValue plan || !plan.TryGetValue<string>(out _)) throw new JsonException();
        SetPermission(limit, allowed);
        if (allowed)
        {
            // These flags describe the logged-in account, not the pool serving the model.
            if (body["spend_control"] is JsonObject spend) spend["reached"] = false;
            if (body["credits"] is JsonObject credits && credits.ContainsKey("overage_limit_reached")) credits["overage_limit_reached"] = false;
            // Do not keep a subscription-exhaustion CTA over an available pool route.
            if (body["rate_limit_upsell"] is JsonObject banner)
            {
                var reserveBanner = banner["banner_type"] is JsonValue kind && kind.TryGetValue<string>(out var name) && name == "luna_reserve";
                if (!reserveBanner) body["rate_limit_upsell"] = null;
            }
        }
        if (canRouteModel is not null && body["additional_rate_limits"] is JsonArray additional)
            foreach (var entry in additional.OfType<JsonObject>())
                if (entry["normal_model_slug"] is JsonValue modelValue && modelValue.TryGetValue<string>(out var model)
                    && !string.IsNullOrWhiteSpace(model) && model.Length <= 128 && entry["rate_limit"] is JsonObject modelLimit)
                    SetPermission(modelLimit, canRouteModel(model));
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private static void SetPermission(JsonObject limit, bool allowed)
    { limit["allowed"] = allowed; limit["limit_reached"] = !allowed; }
}
