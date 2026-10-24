using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexPool.Core;

internal static class CompatibilityTests
{
    public static async Task Run()
    {
        RequestConversion();
        await ResponsesAndImages();
        await StreamingTools();
        await FailuresAndReplay();
        await EdgeCases();
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException(); }
    private static CompatibilityRequest Parse(string json, CompatibilityKind kind = CompatibilityKind.Chat)
    { using var document = JsonDocument.Parse(json); return CompatibilityRequest.Parse(document.RootElement, kind); }
    private static string Event(string type, object? response = null) => "data: " + JsonSerializer.Serialize(new { type, response }) + "\r\n\r\n";
    private static readonly object Message = new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "你好🌏" } } };
    private static object Completed(object[]? output = null) => new { output = output ?? [Message], usage = new { input_tokens = 10, output_tokens = 2, total_tokens = 12,
        input_tokens_details = new { cached_tokens = 3 }, output_tokens_details = new { reasoning_tokens = 1 } } };
    private static void RequestConversion()
    {
        var request = Parse("""{"model":"test","messages":[{"role":"system","content":"rule"},{"role":"user","content":[{"type":"text","text":"hi"},{"type":"image_url","image_url":{"url":"https://example.com/image.png"}}]},{"role":"assistant","content":null,"tool_calls":[{"id":"call_A","type":"function","function":{"name":"weather","arguments":"{\"city\":\"北京\"}"}}]},{"role":"tool","tool_call_id":"call_A","content":"sunny"}],"tools":[{"type":"function","function":{"name":"weather","parameters":{"type":"object","properties":{}}}}],"tool_choice":{"type":"function","function":{"name":"weather"}},"response_format":{"type":"json_schema","json_schema":{"name":"result","schema":{"type":"object"},"strict":true}}}""");
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Check(root.GetProperty("input")[0].GetProperty("role").GetString() == "developer");
        Check(root.GetProperty("input")[1].GetProperty("content")[1].GetProperty("type").GetString() == "input_image");
        Check(root.GetProperty("input")[2].GetProperty("arguments").GetString() == "{\"city\":\"北京\"}");
        Check(root.GetProperty("input")[3].GetProperty("call_id").GetString() == "call_A");
        Check(root.GetProperty("tools")[0].GetProperty("name").GetString() == "weather");
        Check(root.GetProperty("tool_choice").GetProperty("name").GetString() == "weather");
        Check(root.GetProperty("text").GetProperty("format").GetProperty("type").GetString() == "json_schema");
        Check(root.GetProperty("stream").GetBoolean() && !root.GetProperty("store").GetBoolean() && !request.Stream);
        foreach (var extra in new[] { "\"temperature\":0.7", "\"max_tokens\":20", "\"n\":2", "\"stop\":[\"END\"]", "\"stream\":\"true\"", "\"store\":true" })
        {
            try { Parse("{\"model\":\"test\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]," + extra + "}"); throw new InvalidOperationException(); }
            catch (CompatibilityFailure) { }
        }
        using var catalog = JsonDocument.Parse("""{"models":[{"slug":"visible","visibility":"list"},{"slug":"hidden","visibility":"hide"}]}""");
        var list = CompatibilityCatalog.Models(catalog.RootElement);
        Check(list["object"]!.GetValue<string>() == "list" && list["data"]!.AsArray().Count == 1);
    }
    private static async Task ResponsesAndImages()
    {
        foreach (var kind in new[] { CompatibilityKind.Chat, CompatibilityKind.Responses })
        {
            var request = kind == CompatibilityKind.Chat ? Parse("""{"model":"test","messages":[{"role":"user","content":"hello"}]}""")
                : Parse("""{"model":"test","input":"hello"}""", kind);
            var wire = new Sink(); using var converter = new CompatibilitySink(wire, request);
            converter.Start(200,"text/event-stream");
            var bytes = Encoding.UTF8.GetBytes(Event("response.completed", Completed()));
            foreach (var b in bytes) await converter.WriteAsync(new byte[] { b }, default);
            await converter.FinishAsync(default);
            using var result = JsonDocument.Parse(wire.Text);
            Check(converter.Terminal && wire.Type == "application/json");
            if (kind == CompatibilityKind.Chat)
            {
                Check(result.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() == "你好🌏");
                Check(result.RootElement.GetProperty("usage").GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32() == 3);
            }
            else Check(result.RootElement.GetProperty("output")[0].GetProperty("type").GetString() == "message");
        }
        var imageRequest = Parse("""{"model":"gpt-image-2","prompt":"test","quality":"low"}""", CompatibilityKind.Image);
        Check(imageRequest.Model == "gpt-6.1-sol");
        var imageWire = new Sink(); using var imageSink = new CompatibilitySink(imageWire, imageRequest);
        imageSink.Start(200,"text/event-stream");
        var png = Convert.ToBase64String(new byte[] { 137,80,78,71,13,10,26,10,0 });
        await imageSink.WriteAsync(Encoding.UTF8.GetBytes(Event("response.completed", Completed([new { type="image_generation_call", result=png }]))),default);
        Check(JsonNode.Parse(imageWire.Text)!["data"]![0]!["b64_json"]!.GetValue<string>() == png);
        var doneWire = new Sink(); using var doneSink = new CompatibilitySink(doneWire, imageRequest);
        doneSink.Start(200,"text/event-stream");
        await doneSink.WriteAsync(Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(new {type="response.output_item.done",item=new {type="image_generation_call",id="img_A",result=png}}) + "\n\n"),default);
        Check(doneWire.Text.Length==0);
        await doneSink.WriteAsync(Encoding.UTF8.GetBytes(Event("response.completed",Completed([new{type="image_generation_call",id="img_A"}]))),default);
        Check(JsonNode.Parse(doneWire.Text)!["data"]![0]!["b64_json"]!.GetValue<string>()==png);
        try { Parse("""{"model":"gpt-image-2","prompt":"test","size":"1024x1024"}""", CompatibilityKind.Image); throw new InvalidOperationException(); }
        catch (CompatibilityFailure) { }
    }
    private static async Task StreamingTools()
    {
        var request = Parse("""{"model":"test","messages":[{"role":"user","content":"hello"}],"stream":true,"stream_options":{"include_usage":true}}""");
        var wire = new Sink(); using var converter = new CompatibilitySink(wire,request); converter.Start(200,"text/event-stream");
        var frames = new[] {
            """{"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","call_id":"call_A","name":"weather","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","output_index":2,"delta":"{\"city\":"}""",
            """{"type":"response.function_call_arguments.delta","output_index":2,"delta":"\"北京\"}"}""",
            """{"type":"response.output_item.done","output_index":2,"item":{"type":"function_call","call_id":"call_A","name":"weather","arguments":"{\"city\":\"北京\"}"}}"""
        };
        foreach(var frame in frames) await converter.WriteAsync(Encoding.UTF8.GetBytes("data: " + frame + "\n\n"),default);
        // Actual upstream output_index includes preceding reasoning items.
        await converter.WriteAsync(Encoding.UTF8.GetBytes(Event("response.completed",Completed([
            new { type="reasoning" }, new { type="reasoning" }, new { type="function_call",call_id="call_A",name="weather",arguments="{\"city\":\"北京\"}" }]))), default);
        await converter.FinishAsync(default);
        Check(wire.Text.EndsWith("data: [DONE]\n\n"));
        var chunks=wire.Text.Split("\n\n",StringSplitOptions.RemoveEmptyEntries).Where(x=>x!="data: [DONE]").Select(x=>JsonNode.Parse(x[6..])!).ToArray();
        var arguments = string.Concat(chunks.Select(x => x["choices"]!.AsArray().FirstOrDefault()?["delta"]?["tool_calls"]?[0]?["function"]?["arguments"]?.GetValue<string>()));
        Check(arguments=="{\"city\":\"北京\"}");
        Check(chunks.Any(x=>x["choices"]!.AsArray().FirstOrDefault()?["finish_reason"]?.GetValue<string>()=="tool_calls"));
        Check(chunks.Last()["choices"]!.AsArray().Count==0 && chunks.Last()["usage"]!["total_tokens"]!.GetValue<int>()==12);
    }
    private static async Task FailuresAndReplay()
    {
        var request=Parse("""{"model":"test","messages":[{"role":"user","content":"hello"}]}""");
        foreach (var stream in new[] { Event("response.created"), Event("response.failed",new {error=new {message="SECRET-CANARY"}}) })
        {
            var wire=new Sink();using var converter=new CompatibilitySink(wire,request);converter.Start(200,"text/event-stream");
            await converter.WriteAsync(Encoding.UTF8.GetBytes(stream),default);await converter.FinishAsync(default);
            Check(wire.Status==502 && !wire.Text.Contains("SECRET-CANARY") && !wire.Text.Contains("[DONE]"));
        }
        var handler=new Handler();using var client=new HttpClient(handler);var sink=new Sink();using var conversion=new CompatibilitySink(sink,request);
        var result=await new ResponsesRelay(client).ForwardAsync(request.Body,"application/json",[
            new("A",new("https://fixture.invalid/responses"),"a"),new("B",new("https://fixture.invalid/responses"),"b")],new Dictionary<string,string>(),conversion,default);
        Check(result.Committed && handler.Count==1);await conversion.FinishAsync(default);Check(sink.Status==502);
    }
    private static async Task EdgeCases()
    {
        var imageRequest=Parse("""{"model":"gpt-image-2","prompt":"test"}""",CompatibilityKind.Image);
        foreach(var output in new object[][] {
            [],[new{type="image_generation_call",result="not-base64"}],
            [new{type="image_generation_call",result=Convert.ToBase64String("not-an-image"u8)}],
            [new{type="image_generation_call",id="A",result="iVBORw0KGgo="},new{type="image_generation_call",id="B",result="iVBORw0KGgo="}]
        })
        {
            var wire=new Sink();using var converter=new CompatibilitySink(wire,imageRequest);converter.Start(200,"text/event-stream");
            try{await converter.WriteAsync(Encoding.UTF8.GetBytes(Event("response.completed",Completed(output))),default);throw new InvalidOperationException();}
            catch(CompatibilityFailure){Check(wire.Text.Length==0&&converter.FailureCode is not null);}
        }
        var partialWire=new Sink();using(var partial=new CompatibilitySink(partialWire,imageRequest))
        {
            partial.Start(200,"text/event-stream");
            await partial.WriteAsync(Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(new {type="response.output_item.done",item=new{type="image_generation_call",id="A",result="iVBORw0KGgo="}})+"\n\n"
                +Event("response.failed",new{error=new{message="SECRET-CANARY"}})),default);
            await partial.FinishAsync(default);Check(partialWire.Status==502&&!partialWire.Text.Contains("b64_json")&&!partialWire.Text.Contains("SECRET-CANARY"));
        }
        var streamRequest=Parse("""{"model":"test","messages":[{"role":"user","content":"test"}],"stream":true,"stream_options":{"include_usage":true}}""");
        var wireStream=new Sink();using(var stream=new CompatibilitySink(wireStream,streamRequest))
        {
            stream.Start(200,"text/event-stream");
            foreach(var frame in new[]{
                """{"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","call_id":"A","name":"first"}}""",
                """{"type":"response.output_item.added","output_index":4,"item":{"type":"function_call","call_id":"B","name":"second"}}""",
                """{"type":"response.function_call_arguments.delta","output_index":4,"delta":"{"}""",
                """{"type":"response.function_call_arguments.delta","output_index":2,"delta":"{}"}"""})
                await stream.WriteAsync(Encoding.UTF8.GetBytes("data: "+frame+"\n\n"),default);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(Event("response.completed",new{output=new object[]{new{type="reasoning"},new{type="reasoning"},new{type="function_call",call_id="A",name="first",arguments="{}"},new{type="reasoning"},new{type="function_call",call_id="B",name="second",arguments="{}"}}})),default);
            Check(wireStream.Text.Contains("\"index\":1")&&wireStream.Text.EndsWith("data: [DONE]\n\n")&&!wireStream.Text.Contains("\"prompt_tokens\""));
        }
        foreach(var frame in new[]{"""{"type":"response.function_call_arguments.delta","output_index":9,"delta":"{}"}""", """{"type":"response.output_text.delta","delta":"no-index"}""", "{broken-json"})
        {
            var wire=new Sink();using var stream=new CompatibilitySink(wire,streamRequest);stream.Start(200,"text/event-stream");
            try{await stream.WriteAsync(Encoding.UTF8.GetBytes("data: "+frame+"\n\n"),default);throw new InvalidOperationException();}
            catch(CompatibilityFailure){Check(!wire.Text.Contains("[DONE]"));}
        }
        var cutWire=new Sink();using(var cut=new CompatibilitySink(cutWire,streamRequest))
        {
            cut.Start(200,"text/event-stream");
            await cut.WriteAsync(Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"partial\"}\n\n"),default);
            await cut.FinishAsync(default);Check(cutWire.Text.Contains("partial")&&cut.Failed&&!cutWire.Text.Contains("[DONE]"));
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        { Count++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Event("response.created"),Encoding.UTF8,"text/event-stream")}); }
    }
    private sealed class Sink : IResponseSink
    {
        private readonly MemoryStream buffer=new();
        public string Text=>Encoding.UTF8.GetString(buffer.ToArray());
        public int Status;public string? Type;
        public void Start(int statusCode,string contentType){Status=statusCode;Type=contentType;}
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,CancellationToken token)=>buffer.WriteAsync(bytes,token);
    }
}
