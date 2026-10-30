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
        Check(projection["name"]!.GetValue<string>() == "Codex Pool" && projection["tileLabel"]!.GetValue<string>() == "Account A");
        Check(!projection["quotaIcon"]!["known"]!.GetValue<bool>() && projection["quotaIcon"]!["label"]!.GetValue<string>() == "—");
        var now = DateTimeOffset.UtcNow;
        var iconQuota = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new {
            accountId = "account-a", updatedAt = now, cacheState = "current", windows = new[] {
                new { kind = "5h", windowSeconds = 18000, remainingPercent = 42d, resetAt = now.AddHours(1) },
                new { kind = "weekly", windowSeconds = 604800, remainingPercent = 4d, resetAt = now.AddDays(1) }
            }
        }))!.AsObject();
        fakeStatus["usage"]!.AsArray().Add(iconQuota);
        fakeStatus["settings"] = JsonSerializer.SerializeToNode(new PoolSettings(true, 95, 95, 95));
        var dynamic = QuickSettingsRegistration.Presentation(new(true,true,fakeStatus,"代理运行中"),true,"");
        Check(dynamic["quotaIcon"]!["remaining"]!.GetValue<double>() == 4); // Same limiting weekly window as the tray.
        Check(dynamic["quotaIcon"]!["accentArgb"]!.GetValue<uint>() == 0xFFFF7069);
        fakeStatus["settings"]!["autoSwitch"] = false;
        dynamic = QuickSettingsRegistration.Presentation(new(true,true,fakeStatus,"代理运行中"),true,"");
        Check(dynamic["quotaIcon"]!["remaining"]!.GetValue<double>() == 42 && dynamic["quotaIcon"]!["label"]!.GetValue<string>() == "42");
        iconQuota["windows"]![0]!["remainingPercent"] = 0d;
        dynamic = QuickSettingsRegistration.Presentation(new(true,true,fakeStatus,"代理运行中"),true,"");
        Check(dynamic["quotaIcon"]!["exhausted"]!.GetValue<bool>());
        iconQuota["cacheState"] = "historical";
        dynamic = QuickSettingsRegistration.Presentation(new(true,true,fakeStatus,"代理运行中"),true,"");
        Check(!dynamic["quotaIcon"]!["known"]!.GetValue<bool>() && !dynamic["quotaIcon"]!["exhausted"]!.GetValue<bool>());
        fakeStatus["activeAccountId"] = "account-b";
        dynamic = QuickSettingsRegistration.Presentation(new(true,true,fakeStatus,"代理运行中"),true,"");
        Check(dynamic["tileLabel"]!.GetValue<string>() == "Account B" && !dynamic["quotaIcon"]!["known"]!.GetValue<bool>());
        Check(new QuotaIconState(30,false).AccentArgb == 0xFF75DBAF && new QuotaIconState(29,false).AccentArgb == 0xFFFFC65F);
        Check(new QuotaIconState(14,false).AccentArgb == 0xFFFF7069 && new QuotaIconState(null,false).AccentArgb == 0xFF96A1B1);
        var manifest=QuickSettingsRegistration.Manifest();Check(manifest["manifestVersion"]!.GetValue<int>()==2 && manifest["id"]!.GetValue<string>()=="codex-pool");
        var registration=new System.Text.Json.Nodes.JsonObject { ["protocol"]=2,["type"]="framework.pluginStatus",["id"]="codex-pool",["installed"]=true,["registrationId"]=Guid.NewGuid().ToString(),["nativeRevision"]=1 };
        var sent=new List<string>();
        var managed=await QuickSettingsManagement.ExecuteAsync("pin",null,request=>{
            var type=request["type"]!.GetValue<string>();sent.Add(type);
            if(type=="plugin.status")return Task.FromResult((System.Text.Json.Nodes.JsonObject)registration.DeepClone());
            if(type=="plugin.pin")throw new IOException("Synthetic response loss.");
            Check(type=="plugin.operationStatus");return Task.FromResult(new System.Text.Json.Nodes.JsonObject { ["protocol"]=2,["type"]="framework.operation",["operationId"]=request["operationId"]!.DeepClone(),["state"]="awaiting-shell",["catalogCommitted"]=true,["nativeConfirmed"]=false,["target"]=new System.Text.Json.Nodes.JsonObject{["id"]="codex-pool"} });
        });
        Check(managed.Exit==0 && managed.Response["state"]!.GetValue<string>()=="awaiting-shell" && !managed.Response["nativeConfirmed"]!.GetValue<bool>());
        Check(sent.Count(x=>x=="plugin.pin")==1 && sent.Count(x=>x=="plugin.operationStatus")==1);
        sent.Clear();var unknownManagement=await QuickSettingsManagement.ExecuteAsync("unregister",null,request=>{
            var type=request["type"]!.GetValue<string>();sent.Add(type);
            if(type=="plugin.status")return Task.FromResult((System.Text.Json.Nodes.JsonObject)registration.DeepClone());
            throw new IOException("Synthetic management outage.");
        });
        Check(unknownManagement.Exit==2 && unknownManagement.Response["state"]!.GetValue<string>()=="unknown" && unknownManagement.Response["catalogCommitted"] is null && !unknownManagement.Response["nativeConfirmed"]!.GetValue<bool>());
        Check(sent.Count(x=>x=="plugin.unregister")==1);
        registration["id"]="other-plugin";var foreignRejected=false;try{QuickSettingsManagement.Request(registration,"pin",Guid.NewGuid().ToString());}catch(IOException){foreignRejected=true;}Check(foreignRejected);
        Check(manifest["activation"]!["mode"]!.GetValue<string>()=="on-demand");
        Check(await QuickSettingsProviderEntry.RunAsync([])==2);
        Check(await QuickSettingsProviderEntry.RunAsync([Path.Combine(root,"missing-broker.exe"),"other-plugin",Guid.NewGuid().ToString()])==2);
        var providerPath=Path.Combine(root,"helper","CodexPool.Daemon.exe");
        var entryConfig=new System.Text.Json.Nodes.JsonObject { ["configVersion"]=1,["servicePath"]=Path.Combine(root,"service","CodexPool.Daemon.exe"),["dataDirectory"]=Path.Combine(root,"data") };
        Check(QuickSettingsProviderEntry.Parse(entryConfig,providerPath).DataDirectory==Path.Combine(root,"data"));
        entryConfig["servicePath"]=providerPath;
        var configRejected=false;try{QuickSettingsProviderEntry.Parse(entryConfig,providerPath);}catch(IOException){configRejected=true;}Check(configRejected);
        entryConfig["servicePath"]=Path.Combine(root,"service","CodexPool.Daemon.exe");entryConfig["command"]="not-allowed";
        configRejected=false;try{QuickSettingsProviderEntry.Parse(entryConfig,providerPath);}catch(IOException){configRejected=true;}Check(configRejected);
        var contract=QuickSettingsRegistration.ContractState(dynamic,"test-provider-epoch",7);
        Check(contract["providerEpoch"]!.GetValue<string>()=="test-provider-epoch" && contract["revision"]!.GetValue<ulong>()==7);
        Check(contract["tile"]!["label"]!.GetValue<string>()=="Account B" && !contract["tile"]!["indicator"]!["known"]!.GetValue<bool>());
        Check(contract["tile"]!["detailLabel"]!.GetValue<string>()=="管理账户");
        Check(contract["components"]!.AsArray().Single(x=>x!["id"]!.GetValue<string>()=="accounts")!["appearance"]!.GetValue<string>()=="settings-list");
        Check(contract["footer"]![0]!["appearance"]!.GetValue<string>()=="settings-link");
        var stoppedPage=QuickSettingsRegistration.ContractState(stopped,"test-provider-epoch",8);
        Check(stoppedPage["components"]!.AsArray().Count==1 && stoppedPage["components"]![0]!["appearance"]!.GetValue<string>()=="empty-state" && stoppedPage["components"]![0]!["title"]!.GetValue<string>()=="代理已关闭");
        var components=contract["components"]!.AsArray();Check(components.Single(x=>x!["id"]!.GetValue<string>()=="accounts")!["items"]!.AsArray().Count==2);
        Check(!contract.ContainsKey("rows") && !contract.ContainsKey("quotaIcon"));
        var menu=contract["contextMenu"]!.AsArray();Check(menu.Count==1 && menu[0]!["id"]!.GetValue<string>()=="open-management" && menu[0]!["title"]!.GetValue<string>()=="打开网页管理");
        var launches=0;Check(QuickSettingsRegistration.OpenManagement(new(true,true,null,""),()=>++launches).Success && launches==1);
        Check(!QuickSettingsRegistration.OpenManagement(new(true,false,null,""),()=>++launches).Success && launches==1);
        Check(!QuickSettingsRegistration.OpenManagement(new(false,true,null,""),()=>++launches).Success && launches==1);
        Check(!QuickSettingsRegistration.OpenManagement(new(true,true,null,""),()=>throw new InvalidOperationException()).Success);
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
