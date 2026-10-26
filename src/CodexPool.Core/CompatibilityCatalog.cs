using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexPool.Core;

public static class CompatibilityCatalog
{
    public static JsonObject Models(JsonElement catalog)
    {
        if (!catalog.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new CompatibilityFailure("unrecognized_model_catalog");
        var data = new JsonArray();
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("visibility", out var visibility) || visibility.ValueKind != JsonValueKind.String || visibility.GetString() != "list"
                || !model.TryGetProperty("slug", out var slug) || slug.ValueKind != JsonValueKind.String
                || !Regex.IsMatch(slug.GetString()!, "^[a-zA-Z0-9._:/-]{1,128}$")) continue;
            data.Add(new JsonObject { ["id"] = slug.GetString(), ["object"] = "model", ["created"] = 0, ["owned_by"] = "openai" });
        }
        return new JsonObject { ["object"] = "list", ["data"] = data };
    }
}
