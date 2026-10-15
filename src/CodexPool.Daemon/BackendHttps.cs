using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;

namespace CodexPool.Daemon;

public static class BackendHttps
{
    public const int Port = 3211;
    public const string Origin = "https://127.0.0.1:3211";
    public const string ThumbprintFile = "backend-https.thumbprint";

    public static X509Certificate2? LoadCertificate(string directory)
    {
        var path = Path.Combine(directory, ThumbprintFile);
        if (!File.Exists(path)) return null;
        var thumbprint = File.ReadAllText(path).Trim();
        if (thumbprint.Length != 40 || thumbprint.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Invalid local backend certificate selection.");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        foreach (var certificate in matches)
        {
            if (IsUsable(certificate)) return certificate;
            certificate.Dispose();
        }
        throw new InvalidOperationException("Local backend certificate is unavailable or expired. Run setup-backend-https.ps1 again.");
    }

    public static bool IsUsable(X509Certificate2 certificate)
    {
        var now = DateTime.UtcNow;
        return certificate.HasPrivateKey && certificate.NotBefore.ToUniversalTime() <= now
            && certificate.NotAfter.ToUniversalTime() > now
            && certificate.MatchesHostname("127.0.0.1", allowWildcards: false, allowCommonName: false)
            && certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"));
    }

    public static bool IsBackendPath(string path) => path.StartsWith("/backend-api/", StringComparison.Ordinal)
        || path.StartsWith("/api/codex/", StringComparison.Ordinal);

    public static bool Allows(HttpRequest request)
    {
        if (request.Host.Host != "127.0.0.1") return false;
        var origin = request.IsHttps && request.Host.Port == Port && IsBackendPath(request.Path.Value ?? "")
            ? Origin : request.Scheme == "http" && request.Host.Port == 3210 ? "http://127.0.0.1:3210" : null;
        return origin is not null && (!request.Headers.TryGetValue("Origin", out var supplied) || supplied == origin);
    }
}
