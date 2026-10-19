using System.Text.Json;
using CodexPool.Core;

namespace CodexPool.Floating;

internal static class AccountTrayMenu
{
    internal static PoolSettings ToggleSettings(JsonElement state)
    {
        var settings = state.GetProperty("settings").Deserialize<PoolSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return settings with { AutoSwitch = !settings.AutoSwitch };
    }

    internal static GlassMenuItem Accounts(JsonElement snapshot, Action<string> activate, bool enabled = true)
    {
        var active = snapshot.GetProperty("activeAccountId").GetString();
        var accounts = snapshot.GetProperty("accounts").EnumerateArray().ToArray();
        var name = accounts.FirstOrDefault(x => x.GetProperty("id").GetString() == active);
        var activeName = name.ValueKind == JsonValueKind.Object ? name.GetProperty("name").GetString() ?? "未命名账户" : "未选择";
        var children = accounts.Select(account =>
        {
            var id = account.GetProperty("id").GetString();
            var current = id == active;
            var available = enabled && !current && id is not null && Guid.TryParseExact(id, "N", out _)
                && account.GetProperty("enabled").GetBoolean() && !account.GetProperty("paused").GetBoolean() && !account.GetProperty("drain").GetBoolean();
            return new GlassMenuItem(account.GetProperty("name").GetString() ?? "未命名账户", available ? () => activate(id!) : null,
                Enabled: available, IsChecked: current);
        }).ToArray();
        return new("当前账户：" + activeName, Children: children, Enabled: enabled && children.Length > 0);
    }
}
