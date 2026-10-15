using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CodexPool.Daemon;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class BackendHttpsTests
{
    public static async Task RunAsync()
    {
        using var key = RSA.Create(2048);
        using var certificate = Certificate(key, "127.0.0.1");
        using var wrongName = Certificate(key, "127.0.0.2");
        using var expired = Certificate(key, "127.0.0.1", expired: true);
        using var noServerUsage = Certificate(key, "127.0.0.1", serverUsage: false);
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        Require(BackendHttps.IsUsable(certificate));
        foreach (var invalid in new[] { wrongName, expired, noServerUsage, publicOnly }) Require(!BackendHttps.IsUsable(invalid));

        var directory = Path.Combine(Environment.CurrentDirectory, ".research", "backend-https-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Require(BackendHttps.LoadCertificate(directory) is null);
        await File.WriteAllTextAsync(Path.Combine(directory, BackendHttps.ThumbprintFile), "invalid/selection");
        try { using var unexpected = BackendHttps.LoadCertificate(directory); throw new Exception("Invalid certificate selection accepted"); }
        catch (InvalidOperationException) { }

        DefaultHttpContext Context(string scheme, string host, string path, string? origin = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Scheme = scheme; context.Request.Host = new HostString(host); context.Request.Path = path;
            if (origin is not null) context.Request.Headers.Origin = origin;
            return context;
        }
        foreach (var path in new[] { "/backend-api/wham/usage", "/backend-api/wham/accounts/check", "/api/codex/usage" })
        {
            Require(BackendHttps.Allows(Context("https", "127.0.0.1:3211", path).Request));
            Require(BackendHttps.Allows(Context("https", "127.0.0.1:3211", path, BackendHttps.Origin).Request));
        }
        foreach (var path in new[] { "/", "/health", "/settings", "/dashboard/session", "/auth/callback", "/v1/responses", "/backend-api-evil/usage" })
            Require(!BackendHttps.Allows(Context("https", "127.0.0.1:3211", path).Request));
        foreach (var host in new[] { "localhost:3211", "evil.invalid:3211", "127.0.0.1:3210", "127.0.0.1" })
            Require(!BackendHttps.Allows(Context("https", host, "/backend-api/wham/usage").Request));
        foreach (var origin in new[] { "https://evil.invalid", "http://127.0.0.1:3210", "null", BackendHttps.Origin + ", https://evil.invalid" })
            Require(!BackendHttps.Allows(Context("https", "127.0.0.1:3211", "/backend-api/wham/usage", origin).Request));
        Require(!BackendHttps.Allows(Context("http", "127.0.0.1:3211", "/backend-api/wham/usage").Request));
        Require(BackendHttps.Allows(Context("http", "127.0.0.1:3210", "/settings", "http://127.0.0.1:3210").Request));
        Require(!BackendHttps.Allows(Context("http", "127.0.0.1:3210", "/settings", BackendHttps.Origin).Request));

        // Exercise a real TLS handshake on a free loopback port, without changing OS trust or the live daemon.
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (!BackendHttps.Allows(context.Request)) { context.Response.StatusCode = 403; return; }
            await context.Response.WriteAsJsonAsync(new { fixture = "backend-https" });
        });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            handler.ServerCertificateCustomValidationCallback = (_, peer, _, errors) =>
            {
                if (peer is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(certificate);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                return chain.Build(peer);
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Host = "127.0.0.1:3211";
            using var success = await http.GetAsync(address + "/backend-api/wham/accounts/check");
            Require(success.IsSuccessStatusCode && (await success.Content.ReadAsStringAsync()).Contains("backend-https"));
            using var control = await http.GetAsync(address + "/settings"); Require(control.StatusCode == HttpStatusCode.Forbidden);
            http.DefaultRequestHeaders.Add("Origin", "https://evil.invalid");
            using var foreign = await http.GetAsync(address + "/api/codex/usage"); Require(foreign.StatusCode == HttpStatusCode.Forbidden);
            using var untrusted = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            try { using var unexpected = await untrusted.GetAsync(address); throw new Exception("Untrusted TLS accepted"); }
            catch (HttpRequestException) { }
        }
        finally { await app.StopAsync(); }
    }

    private static X509Certificate2 Certificate(RSA key, string ip, bool expired = false, bool serverUsage = true)
    {
        var request = new CertificateRequest("CN=Backend HTTPS Fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Parse(ip));
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(serverUsage ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 2));
        // Windows Schannel needs a native key handle; no certificate is added to an OS trust store.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
    }

    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Backend HTTPS invariant failed"); }
}
