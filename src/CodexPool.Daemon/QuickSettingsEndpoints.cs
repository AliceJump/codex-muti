using System.Text.Json.Serialization;

namespace CodexPool.Daemon;

internal static class QuickSettingsEndpoints
{
    // Changes only AutoSwitch under the existing settings lock. A control tile
    // cannot overwrite thresholds concurrently changed in the dashboard.
    internal static void Map(WebApplication app, PoolRuntime pool)
    {
        app.MapPost("/settings/automatic-switching", async (HttpContext context) =>
        {
            var input = await context.Request.ReadFromJsonAsync<AutomaticSwitchingInput>(context.RequestAborted);
            if (input is null) return Results.BadRequest(new { error = new { code = "invalid_settings" } });
            return Results.Ok(pool.UpdateAutomaticSwitching(input.Enabled));
        });
    }

    private sealed record AutomaticSwitchingInput([property: JsonRequired] bool Enabled);
}
