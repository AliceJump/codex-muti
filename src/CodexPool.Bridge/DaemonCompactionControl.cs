using System.Net.Http.Json;
using System.Text.Json;
using CodexPool.Core;

namespace CodexPool.Bridge;

public sealed class DaemonCompactionControl(HttpClient http) : ICompactionControl
{
    private const string Endpoint = "http://127.0.0.1:3210";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<CompactionAdvice?> AdviceAsync(string model, CancellationToken token)
    {
        using var response = await http.GetAsync(Endpoint + "/compaction/advice?model=" + Uri.EscapeDataString(model), token);
        response.EnsureSuccessStatusCode();
        using var payload = await ReadAsync(response, token);
        return payload.RootElement.TryGetProperty("advice", out var advice) && advice.ValueKind == JsonValueKind.Object
            ? advice.Deserialize<CompactionAdvice>(Json) : null;
    }
    public async Task<CompactionHold?> ReserveAsync(CompactionAdvice advice, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync(Endpoint + "/compaction/reserve", advice, token);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) return null;
        response.EnsureSuccessStatusCode();
        using var payload = await ReadAsync(response, token);
        var hold = payload.RootElement.Deserialize<CompactionHold>(Json);
        return hold is not null && Guid.TryParseExact(hold.Id, "N", out _) && hold.AccountId == advice.AccountId
            && hold.Epoch == advice.Epoch && hold.Model == advice.Model && hold.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(5) ? hold : null;
    }
    public async Task FinishAsync(string id, string outcome, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync(Endpoint + "/compaction/" + id + "/finish", new { outcome }, token);
        response.EnsureSuccessStatusCode();
    }
    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream(); var bytes = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(bytes, token); if (count == 0) break;
            if (buffer.Length + count > 16384) throw new BridgeFailure("control_response_limit");
            buffer.Write(bytes, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
}
