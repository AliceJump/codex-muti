using System.Text.Json;

namespace CodexPool.Daemon;

public static class ProxyServiceIntent
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("CODEX_POOL_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPool");

    public static bool AllowsAutomaticStart(string directory)
    {
        var path = Path.Combine(directory, "proxy-service-intent.json");
        if (!File.Exists(path)) return true;
        try
        {
            if (new FileInfo(path).Length > 4096) return false;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.GetProperty("running").GetBoolean();
        }
        catch { return false; }
    }

    public static void Confirm(string directory, bool running)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "proxy-service-intent.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { running }));
        File.Move(temporary, path, overwrite: true);
    }
}
