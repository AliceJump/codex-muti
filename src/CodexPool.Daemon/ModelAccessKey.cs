using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CodexPool.Daemon;

public sealed class ModelAccessKey
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Hash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public override string ToString() => "ModelAccessKey [REDACTED]";
}
public sealed record ModelKeyView(string Id, string Name, DateTimeOffset CreatedAt);
public sealed record IssuedModelKey(string Id, [property: JsonIgnore] string Secret)
{ public override string ToString() => "IssuedModelKey [REDACTED]"; }

public static class ModelAccess
{
    public static bool IsModelRoute(string method, string path) => method == "GET" && path is "/v1/models" or "/api/v1/models"
        || method == "POST" && path is "/v1/chat/completions" or "/v1/responses" or "/v1/images/generations"
            or "/api/v1/chat/completions" or "/api/v1/responses" or "/api/v1/images/generations";
}
