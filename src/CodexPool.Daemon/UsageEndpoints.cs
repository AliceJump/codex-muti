using System.Globalization;

namespace CodexPool.Daemon;

public static class UsageEndpoints
{
    public static void Map(WebApplication app, UsageStatistics statistics)
    {
        app.MapGet("/statistics", (HttpRequest request) =>
        {
            if (!Range(request, out var from, out var to)) return Invalid();
            return Results.Ok(statistics.View(from, to));
        });
        app.MapGet("/requests", (HttpRequest request) =>
        {
            if (!Range(request, out var from, out var to)) return Invalid();
            long? before = null; var limit = 100;
            if (request.Query.TryGetValue("before", out var cursor))
            {
                if (cursor.Count != 1 || !long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) return Invalid();
                before = parsed;
            }
            if (request.Query.TryGetValue("limit", out var size)
                && (size.Count != 1 || !int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 200)) return Invalid();
            return Results.Ok(statistics.Requests(from, to, before, limit));
        });
    }
    private static IResult Invalid() => Results.BadRequest(new { error = new { code = "invalid_history_range" } });
    private static bool Range(HttpRequest request, out DateOnly? from, out DateOnly? to)
    {
        var validFrom = Date(request, "from", out from); var validTo = Date(request, "to", out to);
        return validFrom && validTo && !(from > to);
    }
    private static bool Date(HttpRequest request, string key, out DateOnly? day)
    {
        day = null;
        if (!request.Query.TryGetValue(key, out var value)) return true;
        if (value.Count != 1 || !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        day = parsed; return true;
    }
}
