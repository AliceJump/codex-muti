using System.Text.RegularExpressions;
using CodexPool.Core;

namespace CodexPool.Daemon;

public static class CompactionEndpoints
{
    public static void Map(WebApplication app, PoolRuntime pool, string localKey)
    {
        // Keep control capability enforcement at the endpoint as well as the shared
        // local API middleware. Model credentials and browser cookies cannot authorize it.
        var group = app.MapGroup("/compaction").AddEndpointFilter(async (context, next) =>
            LocalCommands.EqualSecret(context.HttpContext.Request.Headers.Authorization.ToString(), "Bearer " + localKey)
                ? await next(context) : Results.Unauthorized());
        group.MapGet("/advice", (string model) => Regex.IsMatch(model, "^[a-zA-Z0-9._:/-]{1,128}$")
            ? Results.Ok(new { enabled = pool.CompactionEnabled, advice = pool.CompactionAdvice(model) }) : Results.BadRequest());
        group.MapPost("/reserve", async (HttpContext context) =>
        {
            var expected = await context.Request.ReadFromJsonAsync<CompactionAdvice>(context.RequestAborted);
            if (expected is null || !Guid.TryParseExact(expected.AccountId, "N", out _) || expected.Epoch < 0
                || expected.Model is null || !Regex.IsMatch(expected.Model, "^[a-zA-Z0-9._:/-]{1,128}$")) return Results.BadRequest();
            try { return Results.Ok(pool.BeginCompaction(expected)); }
            catch (CompactionRejectedException ex) { return Results.Conflict(new { error = new { code = ex.Code } }); }
        });
        group.MapPost("/{id}/finish", async (string id, HttpContext context) =>
        {
            var outcome = await context.Request.ReadFromJsonAsync<CompactionOutcome>(context.RequestAborted);
            if (!Guid.TryParseExact(id, "N", out _) || outcome?.Outcome is not ("completed" or "failed" or "unknown")) return Results.BadRequest();
            return Results.Ok(new { switched = pool.FinishCompaction(id, outcome.Outcome == "completed") });
        });
    }
}
