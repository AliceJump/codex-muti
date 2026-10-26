using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexPool.Core;

public sealed class CompatibilityFailure(string code) : IOException(code)
{ public string Code { get; } = code; }

public enum CompatibilityKind { Chat, Responses, Image }
public sealed record CompatibilityRequest(byte[] Body, string Model, CompatibilityKind Kind, bool Stream, bool IncludeUsage = false)
{
    public static CompatibilityRequest Parse(JsonElement input, CompatibilityKind kind)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new CompatibilityFailure("invalid_request");
        var source = JsonNode.Parse(input.GetRawText())!.AsObject();
        var allowed = kind switch
        {
            CompatibilityKind.Chat => new[] { "model", "messages", "stream", "stream_options", "n", "tools", "tool_choice", "parallel_tool_calls", "reasoning_effort", "response_format", "store" },
            CompatibilityKind.Image => ["model", "prompt", "n", "quality", "size", "response_format", "stream"],
            _ => ["model", "input", "instructions", "stream", "store", "tools", "tool_choice", "parallel_tool_calls", "reasoning", "text", "include"]
        };
        if (source.Any(p => !allowed.Contains(p.Key) && p.Value is not null)) throw new CompatibilityFailure("unsupported_parameter");
        var model = String(source["model"]);
        if (!Regex.IsMatch(model, "^[a-zA-Z0-9._:/-]{1,128}$")) throw new CompatibilityFailure("invalid_model");
        var stream = Boolean(source["stream"], false);
        if (source["n"] is not null && source["n"]!.GetValueKind() != JsonValueKind.Null && source["n"]!.ToJsonString() != "1")
            throw new CompatibilityFailure("only_n_1_supported");
        if (source["store"] is not null && Boolean(source["store"], false)) throw new CompatibilityFailure("storage_unsupported");
        var body = new JsonObject { ["model"] = model, ["store"] = false, ["stream"] = true };
        var includeUsage = false;
        if (kind == CompatibilityKind.Image)
        {
            if (model != "gpt-image-2") throw new CompatibilityFailure("unsupported_image_model");
            if (stream) throw new CompatibilityFailure("image_streaming_unsupported");
            if (source["size"] is not null && String(source["size"]) != "auto") throw new CompatibilityFailure("image_size_unsupported");
            if (source["response_format"] is not null && String(source["response_format"]) != "b64_json") throw new CompatibilityFailure("image_response_format_unsupported");
            var prompt = String(source["prompt"]);
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32000) throw new CompatibilityFailure("invalid_image_prompt");
            var quality = source["quality"] is null ? "auto" : String(source["quality"]);
            if (quality is not ("auto" or "low" or "medium" or "high")) throw new CompatibilityFailure("invalid_image_quality");
            model = "gpt-6.1-sol";
            body["model"] = model;
            body["instructions"] = "Generate the requested image using the image generation tool.";
            body["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt });
            body["tools"] = new JsonArray(new JsonObject { ["type"] = "image_generation", ["model"] = "gpt-image-2", ["quality"] = quality });
            body["tool_choice"] = new JsonObject { ["type"] = "image_generation" };
        }
        else if (kind == CompatibilityKind.Chat)
        {
            if (source["stream_options"] is JsonObject options)
            {
                if (options.Any(p => p.Key != "include_usage")) throw new CompatibilityFailure("unsupported_stream_options");
                includeUsage = Boolean(options["include_usage"], false);
            }
            else if (source["stream_options"] is not null) throw new CompatibilityFailure("invalid_stream_options");
            if (source["messages"] is not JsonArray messages || messages.Count == 0) throw new CompatibilityFailure("invalid_messages");
            var items = new JsonArray();
            foreach (var node in messages)
            {
                if (node is not JsonObject message || message.Any(p => p.Key is not ("role" or "content" or "tool_calls" or "tool_call_id")))
                    throw new CompatibilityFailure("invalid_message");
                var role = String(message["role"]);
                if (role is not ("user" or "assistant" or "system" or "developer" or "tool")) throw new CompatibilityFailure("invalid_message_role");
                if (role == "tool")
                {
                    if (message.Any(p => p.Key is not ("role" or "content" or "tool_call_id"))) throw new CompatibilityFailure("invalid_tool_message");
                    items.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = Identifier(message["tool_call_id"]), ["output"] = String(message["content"]) });
                    continue;
                }
                if (message["content"] is not null)
                {
                    JsonNode content;
                    if (message["content"]!.GetValueKind() == JsonValueKind.String) content = JsonValue.Create(String(message["content"]))!;
                    else if (message["content"] is JsonArray parts)
                    {
                        var converted = new JsonArray();
                        foreach (var part in parts)
                        {
                            if (part is not JsonObject block) throw new CompatibilityFailure("invalid_content_part");
                            var type = String(block["type"]);
                            if (type == "text" && block.All(p => p.Key is "type" or "text"))
                                converted.Add(new JsonObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = String(block["text"]) });
                            else if (type == "image_url" && role == "user" && block.All(p => p.Key is "type" or "image_url") && block["image_url"] is JsonObject image
                                && image.All(p => p.Key is "url" or "detail"))
                            {
                                var url = String(image["url"]);
                                if (!(url.StartsWith("data:image/", StringComparison.Ordinal) || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"))
                                    throw new CompatibilityFailure("invalid_image_url");
                                var convertedImage = new JsonObject { ["type"] = "input_image", ["image_url"] = url };
                                if (image["detail"] is not null)
                                {
                                    var detail = String(image["detail"]);
                                    if (detail is not ("auto" or "low" or "high")) throw new CompatibilityFailure("invalid_image_detail");
                                    convertedImage["detail"] = detail;
                                }
                                converted.Add(convertedImage);
                            }
                            else throw new CompatibilityFailure("unsupported_content_part");
                        }
                        content = converted;
                    }
                    else throw new CompatibilityFailure("invalid_message_content");
                    items.Add(new JsonObject { ["role"] = role == "system" ? "developer" : role, ["content"] = content });
                }
                else if (role != "assistant" || message["tool_calls"] is not JsonArray { Count: > 0 }) throw new CompatibilityFailure("invalid_message_content");
                if (message["tool_calls"] is not null)
                {
                    if (role != "assistant" || message["tool_calls"] is not JsonArray calls) throw new CompatibilityFailure("invalid_tool_calls");
                    foreach (var call in calls)
                    {
                        if (call is not JsonObject c || String(c["type"]) != "function" || c["function"] is not JsonObject f)
                            throw new CompatibilityFailure("unsupported_tool");
                        if (c.Any(p => p.Key is not ("id" or "type" or "function")) || f.Any(p => p.Key is not ("name" or "arguments")))
                            throw new CompatibilityFailure("unsupported_tool_parameter");
                        items.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = Identifier(c["id"]),
                            ["name"] = Identifier(f["name"]), ["arguments"] = String(f["arguments"]) });
                    }
                }
            }
            body["input"] = items;
            if (source["tools"] is not null)
            {
                if (source["tools"] is not JsonArray tools) throw new CompatibilityFailure("invalid_tools");
                var converted = new JsonArray();
                foreach (var tool in tools)
                {
                    if (tool is not JsonObject t || String(t["type"]) != "function" || t.Any(p => p.Key is not ("type" or "function"))
                        || t["function"] is not JsonObject function || function.Any(p => p.Key is not ("name" or "description" or "parameters" or "strict")))
                        throw new CompatibilityFailure("unsupported_tool");
                    var f = (JsonObject)function.DeepClone(); f["name"] = Identifier(f["name"]); f["type"] = "function";
                    converted.Add(f);
                }
                body["tools"] = converted;
            }
            if (source["tool_choice"] is JsonObject choice)
            {
                if (String(choice["type"]) != "function" || choice["function"] is not JsonObject f
                    || choice.Any(p => p.Key is not ("type" or "function")) || f.Count != 1) throw new CompatibilityFailure("unsupported_tool_choice");
                body["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = Identifier(f["name"]) };
            }
            else if (source["tool_choice"] is not null)
            {
                var choiceValue = String(source["tool_choice"]);
                if (choiceValue is not ("auto" or "none" or "required")) throw new CompatibilityFailure("unsupported_tool_choice");
                body["tool_choice"] = choiceValue;
            }
            if (source["reasoning_effort"] is not null)
            {
                var effort = String(source["reasoning_effort"]);
                if (effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh")) throw new CompatibilityFailure("invalid_reasoning_effort");
                body["reasoning"] = new JsonObject { ["effort"] = effort };
            }
            if (source["response_format"] is not null)
            {
                if (source["response_format"] is not JsonObject format) throw new CompatibilityFailure("invalid_response_format");
                var type = String(format["type"]);
                JsonObject responseFormat;
                if (type is "text" or "json_object" && format.Count == 1) responseFormat = new JsonObject { ["type"] = type };
                else if (type == "json_schema" && format["json_schema"] is JsonObject schema && format.Count == 2)
                { responseFormat = (JsonObject)schema.DeepClone(); responseFormat["type"] = "json_schema"; }
                else throw new CompatibilityFailure("unsupported_response_format");
                body["text"] = new JsonObject { ["format"] = responseFormat };
            }
            if (source["parallel_tool_calls"] is not null) body["parallel_tool_calls"] = Boolean(source["parallel_tool_calls"], false);
        }
        else
        {
            if (source["input"] is JsonArray array) body["input"] = array.DeepClone();
            else if (source["input"]?.GetValueKind() == JsonValueKind.String)
                body["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = String(source["input"]) });
            else throw new CompatibilityFailure("invalid_responses_input");
            foreach (var field in new[] { "instructions", "tools", "tool_choice", "parallel_tool_calls", "reasoning", "text", "include" })
                if (source[field] is not null) body[field] = source[field]!.DeepClone();
        }
        return new(JsonSerializer.SerializeToUtf8Bytes(body), model, kind, stream, includeUsage);
    }

    private static string String(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : throw new CompatibilityFailure("invalid_parameter_type");
    private static bool Boolean(JsonNode? node, bool fallback) => node is null ? fallback : node.GetValueKind() is JsonValueKind.True or JsonValueKind.False
        ? node.GetValue<bool>() : throw new CompatibilityFailure("invalid_parameter_type");
    private static string Identifier(JsonNode? node)
    {
        var value = String(node);
        if (!Regex.IsMatch(value, "^[a-zA-Z0-9_-]{1,128}$")) throw new CompatibilityFailure("invalid_tool_identifier");
        return value;
    }
}
