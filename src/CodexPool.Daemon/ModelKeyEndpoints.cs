namespace CodexPool.Daemon;

public static class ModelKeyEndpoints
{
    public static void Map(WebApplication app, Vault vault)
    {
        app.MapGet("/model-keys", () => Results.Ok(vault.ModelKeys));
        app.MapPost("/model-keys/create", async (HttpContext context) =>
        {
            // A credential is displayed only once to an authenticated, CSRF-protected browser.
            // Existing middleware denies model-only keys on all management routes.
            if (context.Request.ContentLength is > 4096) return Results.BadRequest(new { error = new { code = "invalid_key_name" } });
            var input = await context.Request.ReadFromJsonAsync<KeyInput>(context.RequestAborted);
            try
            {
                var issued = vault.CreateModelKey(input?.Name ?? "");
                return Results.Ok(new { id = issued.Id, key = issued.Secret, baseUrl = "http://127.0.0.1:3210/api/v1" });
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = new { code = "invalid_key_name_or_limit" } }); }
        });
        app.MapPost("/model-keys/{id}/revoke", (string id) => vault.RevokeModelKey(id) ? Results.Ok(new { revoked = true }) : Results.NotFound());
    }
    private sealed record KeyInput(string? Name);
}
