using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CodexPool.Daemon;

public sealed record BrowserSession(string Csrf, DateTimeOffset Expires);
public sealed class BrowserSessions
{
    public const string CookieName = "codex_pool_session";
    private readonly ConcurrentDictionary<string, DateTimeOffset> tickets = new();
    private readonly ConcurrentDictionary<string, BrowserSession> sessions = new();
    private static string Random() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Issue()
    {
        foreach (var pair in tickets.Where(x => x.Value < DateTimeOffset.UtcNow)) tickets.TryRemove(pair.Key, out _);
        if (tickets.Count >= 32) throw new AuthFailure("dashboard_sessions_full");
        var ticket = Random(); tickets[ticket] = DateTimeOffset.UtcNow.AddMinutes(1); return ticket;
    }
    public bool Redeem(string ticket, HttpContext context)
    {
        if (!tickets.TryRemove(ticket, out var expiry) || expiry < DateTimeOffset.UtcNow) return false;
        foreach (var pair in sessions.Where(x => x.Value.Expires < DateTimeOffset.UtcNow)) sessions.TryRemove(pair.Key, out _);
        if (sessions.Count >= 32) return false;
        var id = Random(); var session = new BrowserSession(Random(), DateTimeOffset.UtcNow.AddHours(8)); sessions[id] = session;
        context.Response.Cookies.Append(CookieName, id, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict,
            Path = "/", Expires = session.Expires, IsEssential = true });
        return true;
    }
    public BrowserSession? Find(HttpContext context) => context.Request.Cookies.TryGetValue(CookieName, out var id)
        && sessions.TryGetValue(id, out var session) && session.Expires > DateTimeOffset.UtcNow ? session : null;
}
