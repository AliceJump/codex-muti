using System.Text.Json;

namespace CodexPool.Core;

// Detail counters are subsets, not additional tokens. Missing details remain unknown.
public sealed record ResponseTokenUsage(long InputTokens, long OutputTokens, long TotalTokens,
    long? CachedInputTokens, long? ReasoningOutputTokens)
{
    public static ResponseTokenUsage? Parse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object) return null;
        static long? Count(JsonElement item, string field) => item.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? count : null;
        var input = Count(usage, "input_tokens"); var output = Count(usage, "output_tokens"); var total = Count(usage, "total_tokens");
        if (input is null || output is null || total is null || input > long.MaxValue - output || input + output != total) return null;
        long? Detail(string field, string name, long maximum) => usage.TryGetProperty(field, out var detail)
            && detail.ValueKind == JsonValueKind.Object && Count(detail, name) is { } count && count <= maximum ? count : null;
        return new(input.Value, output.Value, total.Value,
            Detail("input_tokens_details", "cached_tokens", input.Value),
            Detail("output_tokens_details", "reasoning_tokens", output.Value));
    }
}

// A passive observer: never changes framing, transport results, retry or account selection.
// Only a terminal upstream usage object is authoritative; EOF/deltas are never estimates.
internal sealed class ResponseUsageObserver(Action<ResponseTokenUsage>? observed) : IDisposable
{
    private const int MaximumFrameBytes = 2 * 1024 * 1024;
    private readonly MemoryStream line = new();
    private readonly MemoryStream data = new();
    private bool oversized;
    private bool reported;
    private bool hasLineContent;
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (observed is null || reported) return;
        foreach (var b in bytes)
        {
            if (b == '\r') continue;
            if (b != '\n')
            {
                hasLineContent = true;
                if (!oversized && line.Length + data.Length < MaximumFrameBytes) line.WriteByte(b);
                else oversized = true;
                continue;
            }
            if (!hasLineContent)
            {
                if (!oversized && data.Length > 0) Inspect();
                data.SetLength(0); oversized = false;
                if (reported) return;
            }
            else if (!oversized)
            {
                var content = line.GetBuffer().AsSpan(0, (int)line.Length);
                if (content.StartsWith("data:"u8))
                {
                    if (data.Length + content.Length >= MaximumFrameBytes) oversized = true;
                    else { data.Write(content[5..]); data.WriteByte((byte)'\n'); }
                }
            }
            line.SetLength(0);
            hasLineContent = false;
        }
    }
    private void Inspect()
    {
        try
        {
            using var json = JsonDocument.Parse(data.GetBuffer().AsMemory(0, (int)data.Length));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String || type.GetString() is not ("response.completed" or "response.incomplete" or "response.failed")
                || !root.TryGetProperty("response", out var response)) return;
            var usage = ResponseTokenUsage.Parse(response);
            if (usage is null) return;
            reported = true; observed!(usage);
        }
        catch (JsonException) { }
    }
    public void Dispose() { line.Dispose(); data.Dispose(); }
}
