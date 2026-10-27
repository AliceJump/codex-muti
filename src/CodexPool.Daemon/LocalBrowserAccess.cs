namespace CodexPool.Daemon;

// The local console needs no login, cookies or expiring tickets. A non-simple
// header prevents other websites from submitting controls through HTML forms.
// Host/Origin checks and the absence of CORS are enforced by the daemon.
public static class LocalBrowserAccess
{
    public const string Header = "X-Codex-Pool-Local";
    public static bool Allows(HttpRequest request) =>
        request.Headers[Header] == "1"
        || request.Method == "GET" && request.Path == "/events"
            && request.Headers["Sec-Fetch-Site"] == "same-origin";
}
