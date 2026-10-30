using System.Text.Json;
using CodexPool.Core;

internal static class QuotaDisplayTests
{
    internal static Task Run()
    {
        var now = DateTimeOffset.UtcNow;
        object Window(int seconds, double remaining, string family = "codex", bool reset = false) => new
        {
            kind = seconds == 604800 ? "secondary_window" : "primary_window",
            windowSeconds = seconds, limitId = family, remainingPercent = remaining,
            resetAt = reset ? now.AddSeconds(-1) : now.AddDays(2)
        };
        JsonElement Quota(object[] windows, bool stale = false, bool continuation = false, object? error = null) => JsonSerializer.SerializeToElement(new
        {
            updatedAt = stale ? now.AddMinutes(-3) : now, windows, allowed = continuation, refreshError = error
        });
        var automatic = JsonSerializer.SerializeToElement(new { autoSwitch = true, windowThreshold = 95 });
        foreach (var settings in new[] { default(JsonElement), automatic })
        {
            foreach (var remaining in new[] { 80d, 4d, 0d })
            {
                var selected = QuotaDisplay.TrayWindow(Quota([Window(604800, remaining)]), settings);
                Check(selected is { Label: "周", Seconds: 604800 } && selected.Remaining == remaining);
            }
            Check(QuotaDisplay.TrayWindow(Quota([Window(604800, 80)], stale: true), settings) is { Remaining: null });
            Check(QuotaDisplay.TrayWindow(Quota([Window(604800, 80, reset: true)]), settings) is { Remaining: null });
            Check(QuotaDisplay.TrayWindow(Quota([Window(604800, 80)], error: new { code = "quota_forbidden" }), settings) is { Remaining: null });
            Check(QuotaDisplay.TrayWindow(Quota([Window(604800, 80), Window(3600, 1, "additional_0")]), settings) is { Seconds: 604800, Remaining: 80 });
        }
        foreach (var seconds in new[] { 18000, 25200, 32400 })
        {
            Check(QuotaDisplay.TrayWindow(Quota([Window(seconds, 80), Window(604800, 20)]), automatic) is { Remaining: 80 } selected && selected.Seconds == seconds);
            Check(QuotaDisplay.TrayWindow(Quota([Window(seconds, 80, reset: true), Window(604800, 80)])) is { Remaining: null } unknown && unknown.Seconds == seconds);
            Check(QuotaDisplay.TrayWindow(Quota([Window(seconds, 80), Window(604800, 4)]), automatic) is { Seconds: 604800, Remaining: 4 });
        }
        Check(QuotaDisplay.TrayWindow(Quota([])) is null);
        Check(QuotaDisplay.TrayWindow(Quota([Window(604800, 80, "additional_0")])) is null);
        Check(QuotaDisplay.TrayWindow(Quota([Window(604800, 0)], continuation: true)) is null);
        Check(QuotaDisplay.TrayWindow(Quota([Window(18000, 0), Window(604800, 80)], continuation: true)) is null);
        return Task.CompletedTask;
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Quota display regression.");
    }
}
