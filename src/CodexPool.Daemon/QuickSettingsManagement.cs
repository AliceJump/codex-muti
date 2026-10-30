using System.Text.Json.Nodes;
using System.Text.Json;

namespace CodexPool.Daemon;

internal static class QuickSettingsManagement
{
    private static JsonObject Message(string type) => new() { ["protocol"] = 2, ["type"] = type };
    internal static JsonObject Request(JsonObject status, string operation, string token)
    {
        var numeric=JsonSerializer.SerializeToElement(status["nativeRevision"]);
        if (operation is not ("pin" or "unpin" or "unregister") || !Guid.TryParse(token, out _)
            || status["id"]?.GetValue<string>() != "codex-pool" || status["installed"]?.GetValue<bool>() != true
            || !Guid.TryParse(status["registrationId"]?.GetValue<string>(), out _)
            || numeric.ValueKind!=JsonValueKind.Number || !numeric.TryGetDouble(out var revision) || !double.IsFinite(revision)
            || revision<=0 || revision>=9007199254740991 || Math.Truncate(revision)!=revision)
            throw new IOException("Invalid owned registration.");
        var request = Message("plugin." + operation);
        request["id"] = "codex-pool";request["registrationId"] = status["registrationId"]!.DeepClone();
        request["nativeRevision"] = status["nativeRevision"]!.DeepClone();request["operationId"] = token;
        return request;
    }
    internal static async Task<(int Exit, JsonObject Response)> ExecuteAsync(string operation, string? token,
        Func<JsonObject, Task<JsonObject>> exchange)
    {
        var mutation = operation is "pin" or "unpin" or "unregister";
        var operationId = mutation ? Guid.NewGuid().ToString() : token;
        JsonObject Query(){var request=Message("plugin.operationStatus");request["operationId"]=operationId;return request;}
        try
        {
            if (operation == "operation-status") return (0, Check(await exchange(Query())));
            var statusRequest=Message("plugin.status");statusRequest["id"]="codex-pool";
            var status=Check(await exchange(statusRequest));
            if (operation == "status") return (0,status);
            return (0,Check(await exchange(Request(status,operation,operationId!))));
        }
        catch (Exception error) when (QuickSettingsRegistration.Unavailable(error))
        {
            // Resolve an unknown management result through a read, never by
            // resubmitting the mutation with rebased registration state.
            if (mutation) try { return (0,Check(await exchange(Query()))); }
                catch (Exception queryError) when (QuickSettingsRegistration.Unavailable(queryError)) { }
            return (2,new JsonObject { ["protocol"]=2,["type"]="framework.operation",["operationId"]=operationId,
                ["state"]="unknown",["catalogCommitted"]=null,["nativeConfirmed"]=false });
        }
    }
    private static JsonObject Check(JsonObject response)
    {
        if (response["protocol"]?.GetValue<int>() != 2
            || response["type"]?.GetValue<string>() is not ("framework.operation" or "framework.pluginStatus"))
            throw new IOException("QSE management rejected or unavailable.");
        if (response["type"]!.GetValue<string>() == "framework.operation"
            && response["target"]?["id"]?.GetValue<string>() != "codex-pool")
            throw new IOException("Unexpected registration result.");
        if(response["type"]!.GetValue<string>()=="framework.pluginStatus" && response["id"]?.GetValue<string>()!="codex-pool")throw new IOException("Unexpected registration identity.");
        return response;
    }
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 2 or > 3 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0])
            || args[1] is not ("status" or "pin" or "unpin" or "unregister" or "operation-status")
            || (args[1] == "operation-status" ? args.Length != 3 || !Guid.TryParse(args[2],out _) : args.Length != 2)) return 2;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var result=await ExecuteAsync(args[1],args.Length==3 ? args[2] : null,
            request=>QuickSettingsRegistration.ExchangeAsync(request,Path.GetFullPath(args[0]),deadline.Token));
        Console.WriteLine(result.Response.ToJsonString());return result.Exit;
    }
}
