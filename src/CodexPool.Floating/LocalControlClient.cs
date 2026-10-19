using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using CodexPool.Daemon;

namespace CodexPool.Floating;

// Each control request reads the current local key. No connection or event stream
// owns authentication for another request, including after daemon/vault replacement.
internal sealed class LocalControlClient : IDisposable
{
    private readonly HttpClient http;
    private readonly Func<string> readKey;

    internal LocalControlClient(string directory, HttpMessageHandler? handler = null, Func<string>? readKey = null)
    {
        http = new(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri("http://127.0.0.1:3210"), Timeout = TimeSpan.FromSeconds(30) };
        this.readKey = readKey ?? (() => Vault.ReadLocalKey(directory));
    }

    internal Task<HttpResponseMessage> GetAsync(string path, CancellationToken token) => SendAsync(HttpMethod.Get, path, null, token);
    internal Task<HttpResponseMessage> PostAsync(string path, object? body, CancellationToken token) => SendAsync(HttpMethod.Post, path, body, token);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path);
        string key;
        try { key = readKey(); }
        catch (InvalidOperationException) { throw new IOException("Local control authentication is unavailable."); }
        request.Headers.Authorization = new("Bearer", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request, token);
    }

    internal static bool IsUnavailable(Exception ex) => ex is HttpRequestException or IOException or UnauthorizedAccessException
        or System.Security.Cryptography.CryptographicException or OperationCanceledException or System.Text.Json.JsonException;

    public void Dispose() => http.Dispose();
}
