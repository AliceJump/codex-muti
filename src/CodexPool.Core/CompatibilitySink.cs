using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexPool.Core;

/// <summary>Converts wire formats only. Upstream commitment remains owned by ResponsesRelay.</summary>
public sealed class CompatibilitySink(IResponseSink output, CompatibilityRequest request) : IResponseSink, IDisposable
{
    private const int Limit = 48 * 1024 * 1024;
    private readonly MemoryStream line = new();
    private readonly MemoryStream data = new();
    private readonly Dictionary<int, int> calls = [];
    private readonly Dictionary<int, StringBuilder> callArguments = [];
    private readonly Dictionary<int, StringBuilder> textDeltas = [];
    private readonly Dictionary<string, string> imageResults = [];
    private int bytes;
    private int status;
    private bool sse;
    private bool roleSent;
    private bool terminal;
    private string id = "chatcmpl-" + Guid.NewGuid().ToString("N");
    private readonly long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public bool Written { get; private set; }
    public bool Terminal => terminal;
    public bool Failed { get; private set; }
    public bool Consumed { get; private set; }
    public string? FailureCode { get; private set; }

    public void SetHeader(string name, string value) => output.SetHeader(name, value);
    public void Start(int statusCode, string contentType)
    {
        status = statusCode; sse = statusCode is >= 200 and < 300 && contentType == "text/event-stream";
        if (!sse) output.Start(statusCode, contentType);
    }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        try { await ConsumeAsync(buffer, cancellationToken); }
        catch (CompatibilityFailure ex) { FailureCode = ex.Code; throw; }
    }
    private async ValueTask ConsumeAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        Consumed = true;
        if (!sse) { Written = true; await output.WriteAsync(buffer, cancellationToken); return; }
        if (buffer.Length > Limit - bytes) throw new CompatibilityFailure("response_too_large");
        bytes += buffer.Length;
        // Byte framing preserves fragmented UTF-8 and CRLF, including splits between CR and LF.
        foreach (var value in buffer.ToArray())
        {
            if (value == '\r') continue;
            if (value != '\n') { line.WriteByte(value); continue; }
            if (line.Length == 0)
            {
                if (data.Length > 0) await EventAsync(cancellationToken);
                data.SetLength(0);
            }
            else
            {
                var raw = line.ToArray();
                if (raw.AsSpan().StartsWith("data:"u8)) { data.Write(raw, 5, raw.Length - 5); data.WriteByte((byte)'\n'); }
            }
            line.SetLength(0);
        }
    }
    private async Task EventAsync(CancellationToken token)
    {
        if (terminal) return;
        JsonObject value;
        try { value = JsonNode.Parse(data.GetBuffer().AsSpan(0, (int)data.Length))?.AsObject() ?? throw new JsonException(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new CompatibilityFailure("invalid_upstream_event"); }
        var type = Text(value["type"]);
        if (type is "response.failed" or "error")
        { await FailAsync("upstream_generation_failed", token); return; }
        if (request.Kind == CompatibilityKind.Image && type == "response.output_item.done" && value["item"] is JsonObject imageItem
            && Text(imageItem["type"]) == "image_generation_call" && Text(imageItem["result"]) is { Length: > 0 } completedImage)
            imageResults[Text(imageItem["id"]) ?? "single"] = completedImage;
        if (request.Kind == CompatibilityKind.Responses && request.Stream)
        {
            if (type is "response.completed" or "response.incomplete") terminal = true;
            await SendAsync(Encoding.UTF8.GetBytes("data: " + value.ToJsonString() + "\n\n"), "text/event-stream", token);
            return;
        }
        if (type is "response.completed" or "response.incomplete")
        {
            if (value["response"] is not JsonObject response) throw new CompatibilityFailure("invalid_upstream_response");
            if (type == "response.incomplete" && (request.Kind != CompatibilityKind.Chat || Text(response["incomplete_details"]?["reason"]) != "max_output_tokens"))
            { await FailAsync("upstream_response_incomplete", token); return; }
            if (request.Kind == CompatibilityKind.Responses)
                await SendJsonAsync(response, token);
            else if (request.Kind == CompatibilityKind.Image)
            {
                var images = new JsonArray();
                foreach (var item in (response["output"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (Text(item["type"]) != "image_generation_call") continue;
                    var result = Text(item["result"]);
                    if (!string.IsNullOrEmpty(result)) imageResults[Text(item["id"]) ?? "single"] = result;
                }
                foreach (var result in imageResults.Values)
                {
                    if (string.IsNullOrEmpty(result) || result.Length > 44 * 1024 * 1024) throw new CompatibilityFailure("invalid_image_output");
                    // Validate base64 and a supported image signature; never return arbitrary URLs/tool text.
                    byte[] image;
                    try { image = Convert.FromBase64String(result); } catch (FormatException) { throw new CompatibilityFailure("invalid_image_output"); }
                    if (!(image.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                        || image.AsSpan().StartsWith(new byte[] { 255, 216, 255 })
                        || image.Length >= 12 && image.AsSpan(0, 4).SequenceEqual("RIFF"u8) && image.AsSpan(8, 4).SequenceEqual("WEBP"u8)))
                        throw new CompatibilityFailure("invalid_image_output");
                    images.Add(new JsonObject { ["b64_json"] = result });
                }
                if (images.Count != 1) throw new CompatibilityFailure("missing_image_output");
                await SendJsonAsync(new JsonObject { ["created"] = created, ["data"] = images }, token);
            }
            else
            {
                var message = ChatMessage(response);
                var reason = type == "response.incomplete" ? "length" : message["tool_calls"] is JsonArray { Count: > 0 } ? "tool_calls"
                    : message["refusal"] is not null ? "content_filter" : "stop";
                var usage = Usage(response);
                if (request.Stream)
                {
                    // Some upstreams emit only terminal items, without deltas.
                    if (!roleSent) await RoleAsync(token);
                    var outputItems = response["output"] as JsonArray ?? [];
                    for (var index = 0; index < outputItems.Count; index++)
                    {
                        if (outputItems[index] is not JsonObject item) continue;
                        if (Text(item["type"]) == "function_call") await ToolAsync(index, item, token, true);
                        if (Text(item["type"]) == "message" && item["content"] is JsonArray content)
                        {
                            var finalText = string.Concat(content.OfType<JsonObject>().Where(x => Text(x["type"]) == "output_text").Select(x => Text(x["text"])));
                            var delivered = textDeltas.GetValueOrDefault(index)?.ToString() ?? "";
                            if (!finalText.StartsWith(delivered, StringComparison.Ordinal)) throw new CompatibilityFailure("inconsistent_upstream_text");
                            if (finalText.Length > delivered.Length) await ChunkAsync(new JsonObject { ["content"] = finalText[delivered.Length..] }, null, token);
                        }
                    }
                    if (message["refusal"] is not null) await ChunkAsync(new JsonObject { ["refusal"] = message["refusal"]!.DeepClone() }, null, token);
                    await ChunkAsync(new JsonObject(), reason, token);
                    if (request.IncludeUsage && usage is not null)
                        await FrameAsync(new JsonObject { ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = created,
                            ["model"] = request.Model, ["choices"] = new JsonArray(), ["usage"] = usage }, token);
                    await SendAsync("data: [DONE]\n\n"u8.ToArray(), "text/event-stream", token);
                }
                else
                {
                    var result = new JsonObject { ["id"] = id, ["object"] = "chat.completion", ["created"] = created, ["model"] = request.Model,
                        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = reason }) };
                    if (usage is not null) result["usage"] = usage;
                    await SendJsonAsync(result, token);
                }
            }
            terminal = true; return;
        }
        if (request.Kind != CompatibilityKind.Chat || !request.Stream) return;
        if (type == "response.created") { await RoleAsync(token); return; }
        var indexed = type is "response.output_text.delta" or "response.function_call_arguments.delta"
            || type is "response.output_item.added" or "response.output_item.done" && value["item"] is JsonObject i && Text(i["type"]) == "function_call";
        var outputIndex = 0;
        if (indexed && !(value["output_index"] is JsonValue v && v.TryGetValue<int>(out outputIndex) && outputIndex >= 0))
            throw new CompatibilityFailure("invalid_upstream_output_index");
        if (type == "response.output_text.delta")
        {
            await RoleAsync(token);
            var delta = Text(value["delta"]) ?? throw new CompatibilityFailure("invalid_upstream_delta");
            if (!textDeltas.TryGetValue(outputIndex, out var accumulated)) textDeltas[outputIndex] = accumulated = new();
            accumulated.Append(delta);
            await ChunkAsync(new JsonObject { ["content"] = delta }, null, token);
        }
        if (type is "response.output_item.added" or "response.output_item.done" && value["item"] is JsonObject function && Text(function["type"]) == "function_call")
            await ToolAsync(outputIndex, function, token, type == "response.output_item.done");
        if (type == "response.function_call_arguments.delta")
        {
            if (!calls.TryGetValue(outputIndex, out var index)) throw new CompatibilityFailure("invalid_upstream_tool_order");
            var delta = Text(value["delta"]) ?? throw new CompatibilityFailure("invalid_upstream_delta");
            if (!callArguments.TryGetValue(outputIndex, out var accumulated)) callArguments[outputIndex] = accumulated = new();
            accumulated.Append(delta);
            await ChunkAsync(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = index,
                ["function"] = new JsonObject { ["arguments"] = delta } }) }, null, token);
        }
    }
    private async Task ToolAsync(int upstreamIndex, JsonObject item, CancellationToken token, bool done)
    {
        await RoleAsync(token);
        if (!calls.TryGetValue(upstreamIndex, out var index))
        {
            index = calls.Count; calls.Add(upstreamIndex, index);
            await ChunkAsync(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = index,
                ["id"] = Text(item["call_id"]) ?? throw new CompatibilityFailure("invalid_upstream_tool"), ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = Text(item["name"]) ?? throw new CompatibilityFailure("invalid_upstream_tool"), ["arguments"] = "" } }) }, null, token);
        }
        if (done)
        {
            var finalArguments = Text(item["arguments"]) ?? "";
            var delivered = callArguments.GetValueOrDefault(upstreamIndex)?.ToString() ?? "";
            if (!finalArguments.StartsWith(delivered, StringComparison.Ordinal)) throw new CompatibilityFailure("inconsistent_upstream_tool");
            if (finalArguments.Length > delivered.Length)
                await ChunkAsync(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = index,
                    ["function"] = new JsonObject { ["arguments"] = finalArguments[delivered.Length..] } }) }, null, token);
            callArguments[upstreamIndex] = new(finalArguments);
        }
    }
    private static JsonObject ChatMessage(JsonObject response)
    {
        if (response["output"] is not JsonArray items) throw new CompatibilityFailure("invalid_upstream_output");
        var text = new StringBuilder(); var refusal = new StringBuilder(); var tools = new JsonArray();
        foreach (var item in items.OfType<JsonObject>())
        {
            var type = Text(item["type"]);
            if (type == "function_call") tools.Add(new JsonObject { ["id"] = Text(item["call_id"]), ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = Text(item["name"]), ["arguments"] = Text(item["arguments"]) } });
            else if (type == "message" && item["content"] is JsonArray content)
                foreach (var part in content.OfType<JsonObject>())
                {
                    if (Text(part["type"]) == "output_text") text.Append(Text(part["text"]));
                    else if (Text(part["type"]) == "refusal") refusal.Append(Text(part["refusal"]));
                    else throw new CompatibilityFailure("unsupported_upstream_output");
                }
            else if (type != "reasoning") throw new CompatibilityFailure("unsupported_upstream_output");
        }
        var result = new JsonObject { ["role"] = "assistant", ["content"] = text.Length == 0 && (tools.Count > 0 || refusal.Length > 0) ? null : JsonValue.Create(text.ToString()) };
        if (tools.Count > 0) result["tool_calls"] = tools;
        if (refusal.Length > 0) result["refusal"] = refusal.ToString();
        return result;
    }
    private static JsonObject? Usage(JsonObject response)
    {
        using var json = JsonDocument.Parse(response.ToJsonString());
        var usage = ResponseTokenUsage.Parse(json.RootElement);
        if (usage is null) return null;
        var result = new JsonObject { ["prompt_tokens"] = usage.InputTokens, ["completion_tokens"] = usage.OutputTokens, ["total_tokens"] = usage.TotalTokens };
        if (usage.CachedInputTokens is { } cached) result["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached };
        if (usage.ReasoningOutputTokens is { } reasoning) result["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = reasoning };
        return result;
    }
    private async Task RoleAsync(CancellationToken token)
    { if (!roleSent) { roleSent = true; await ChunkAsync(new JsonObject { ["role"] = "assistant" }, null, token); } }
    private Task ChunkAsync(JsonObject delta, string? reason, CancellationToken token)
    {
        var chunk = new JsonObject { ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = created, ["model"] = request.Model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = reason }) };
        if (request.IncludeUsage) chunk["usage"] = null;
        return FrameAsync(chunk, token);
    }
    private Task FrameAsync(JsonObject value, CancellationToken token) => SendAsync(Encoding.UTF8.GetBytes("data: " + value.ToJsonString() + "\n\n"), "text/event-stream", token);
    private Task SendJsonAsync(JsonObject value, CancellationToken token) => SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), "application/json", token);
    private async Task SendAsync(byte[] value, string type, CancellationToken token)
    { if (!Written) output.Start(status, type); Written = true; await output.WriteAsync(value, token); }
    private async Task FailAsync(string code, CancellationToken token)
    {
        terminal = true; Failed = true; FailureCode = code;
        var error = new JsonObject { ["error"] = new JsonObject { ["type"] = "proxy_generation_error", ["code"] = code, ["message"] = "上游生成未能完成。" } };
        if (Written) await FrameAsync(error, token);
        else { status = 502; await SendJsonAsync(error, token); }
    }
    public async Task FinishAsync(CancellationToken token)
    {
        if (sse && !terminal) await FailAsync("upstream_stream_incomplete", token);
    }
    private static string? Text(JsonNode? value) => value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    public void Dispose() { line.Dispose(); data.Dispose(); }
}
