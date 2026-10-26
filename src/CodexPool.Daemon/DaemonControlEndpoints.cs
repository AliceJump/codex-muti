namespace CodexPool.Daemon;

public static class DaemonControlEndpoints
{
    public static void Map(WebApplication app, string localKey, Action stop, Func<bool>? canStop = null)
    {
        app.MapPost("/control/shutdown", (HttpContext context) =>
        {
            if (!LocalCommands.EqualSecret(context.Request.Headers.Authorization.ToString(), "Bearer " + localKey))
                return Results.Unauthorized();
            if (canStop?.Invoke() == false) return Results.Conflict(new { state = "busy" });
            // Flush the acknowledgement before stopping Kestrel. Host shutdown drains
            // accepted requests; this does not signal or kill any Codex process.
            context.Response.OnCompleted(() => { stop(); return Task.CompletedTask; });
            return Results.Accepted(value: new { state = "stopping" });
        });
    }
}
