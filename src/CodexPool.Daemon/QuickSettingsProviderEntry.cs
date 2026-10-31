using System.Text.Json.Nodes;

namespace CodexPool.Daemon;

internal static class QuickSettingsProviderEntry
{
    internal sealed record Configuration(string ServicePath, string DataDirectory);

    internal static Configuration Parse(JsonObject value, string providerPath)
    {
        if (value.Count != 3 || value["configVersion"]?.GetValue<int>() != 1
            || value["servicePath"]?.GetValue<string>() is not { } service
            || value["dataDirectory"]?.GetValue<string>() is not { } data
            || !Path.IsPathFullyQualified(service) || !Path.IsPathFullyQualified(data)
            || !Path.GetFileName(service).Equals("CodexPool.Daemon.exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(service).Equals(Path.GetFullPath(providerPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid dedicated Quick Settings provider configuration.");
        var fullService=Path.GetFullPath(service);var fullData=Path.GetFullPath(data);
        if(fullService.StartsWith(@"\\",StringComparison.Ordinal) || fullData.StartsWith(@"\\",StringComparison.Ordinal))throw new IOException("Network provider paths are not allowed.");
        return new(fullService,fullData);
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || args[1] != "codex-pool" || !Guid.TryParse(args[2], out _)
            || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0])) return 2;
        var path = Path.Combine(AppContext.BaseDirectory, "qse-provider.json");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 16384) return 2;
        var configuration = Parse((await JsonNode.ParseAsync(file))!.AsObject(), Environment.ProcessPath!);
        return await QuickSettingsRegistration.RunAsync([args[0], configuration.ServicePath], configuration.DataDirectory, args[2]);
    }
}
