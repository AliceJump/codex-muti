using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodexPool.Core;
using CodexPool.Daemon;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class QuickSettingsTests
{
    internal static async Task RunAsync(string root)
    {
        var captions = new System.Text.Json.Nodes.JsonObject();
        Check(QuickSettingsRegistration.QuotaCaption(captions).Contains("未知"));
        var account = new System.Text.Json.Nodes.JsonObject { ["enabled"] = true, ["paused"] = false, ["drain"] = false, ["state"] = "standby", ["auth"] = "ready" };
        Check(QuickSettingsRegistration.AccountSelectable(account));
        account["paused"] = true;Check(!QuickSettingsRegistration.AccountSelectable(account));
        var stopped = QuickSettingsRegistration.Presentation(new(true,false,null,"代理已停止"),true,"");
        Check(stopped["enabled"]!.GetValue<bool>() == false && stopped["online"]!.GetValue<bool>() && stopped["canToggle"]!.GetValue<bool>());
        var unknown = QuickSettingsRegistration.Presentation(new(false,true,null,"状态未知"),true,"");
        Check(unknown["enabled"]!.GetValue<bool>() && !unknown["canToggle"]!.GetValue<bool>());
        var fakeStatus = System.Text.Json.Nodes.JsonNode.Parse("""
            {"activeAccountId":"account-a","accounts":[
              {"id":"account-a","name":"Account A","enabled":true,"paused":false,"drain":false,"state":"active","auth":"ready"},
              {"id":"account-b","name":"Account B","enabled":true,"paused":false,"drain":false,"state":"standby","auth":"ready"}],
             "accountAvailability":[{"accountId":"account-a","available":true},{"accountId":"account-b","available":false}],"usage":[]}
            """)!.AsObject();
        var projection = QuickSettingsRegistration.Presentation(new(true,true,fakeStatus,"代理运行中"),true,"");
        var rows = projection["rows"]!.AsArray();
        Check(rows.Count == 2 && rows[0]!["selected"]!.GetValue<bool>() && !rows[1]!["selected"]!.GetValue<bool>());
        Check(!rows[1]!["enabled"]!.GetValue<bool>() && rows[0]!["subtitle"]!.GetValue<string>().Contains("未知"));
        var intent = Path.Combine(root,"service-intent");
        Check(ProxyServiceIntent.AllowsAutomaticStart(intent));
        ProxyServiceIntent.Confirm(intent,false);Check(!ProxyServiceIntent.AllowsAutomaticStart(intent));
        ProxyServiceIntent.Confirm(intent,true);Check(ProxyServiceIntent.AllowsAutomaticStart(intent));
        var vault = new Vault(Path.Combine(root, "quick-settings"));
        using var upstream = new HttpClient(new NoNetwork());
        var auth = new OAuth(upstream, vault);
        var quota = new Quota(upstream, auth, vault);
        var pool = new PoolRuntime(vault, auth, quota, boundaryProbe: true);
        var thresholds = new PoolSettings(false, 73, 81, 44);
        pool.UpdateSettings(thresholds);
        var builder = WebApplication.CreateBuilder([]);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (JsonException) { context.Response.StatusCode = 400; }
        });
        QuickSettingsEndpoints.Map(app, pool);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(address) };
            using var enabled = await client.PostAsJsonAsync("/settings/automatic-switching", new { enabled = true });
            enabled.EnsureSuccessStatusCode();
            Check(pool.Settings == thresholds with { AutoSwitch = true });
            using var malformed = await client.PostAsync("/settings/automatic-switching", new StringContent("{}", Encoding.UTF8, "application/json"));
            Check(malformed.StatusCode == HttpStatusCode.BadRequest && pool.Settings.AutoSwitch);
            // Another UI updates thresholds before a tile submits its boolean.
            var newer = new PoolSettings(true, 62, 79, 37);
            pool.UpdateSettings(newer);
            using var disabled = await client.PostAsJsonAsync("/settings/automatic-switching", new { enabled = false });
            disabled.EnsureSuccessStatusCode();
            Check(pool.Settings == newer with { AutoSwitch = false });
            var production = new PoolRuntime(vault, auth, quota, boundaryProbe: false);
            var rejected = false;
            try { production.UpdateAutomaticSwitching(true); }
            catch (AuthFailure error) when (error.Code == "live_migration_gate_pending") { rejected = true; }
            Check(rejected && !production.Settings.AutoSwitch);
            Check(production.UpdateAutomaticSwitching(false) == newer with { AutoSwitch = false });
        }
        finally { await app.StopAsync(); }
    }

    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("Quick Settings contract failed.");
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("Upstream network is prohibited in this focused test.");
    }
}
