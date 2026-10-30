using System.Text.Json;
using System.Text.Json.Nodes;
using CodexPool.Daemon;

internal static class QuickSettingsRecoveryTests
{
    private static void Check(bool value){if(!value)throw new InvalidOperationException("Quick Settings recovery contract failed.");}
    internal static async Task RunAsync(string root)
    {
        var folder=Path.Combine(root,"qse-recovery");Directory.CreateDirectory(folder);
        var broker=Path.Combine(folder,"broker.exe");var service=Path.Combine(folder,"CodexPool.Daemon.exe");File.WriteAllText(broker,"");File.WriteAllText(service,"");
        var probes=0;using var http=new HttpClient();var proxy=new QuickSettingsProxy(service,folder,http,()=>null,()=>{probes++;return true;});
        await proxy.DuringStartupAsync(async()=>{var state=await proxy.ReadAsync(CancellationToken.None);Check(!state.Known && probes==1);return new(true,"Synthetic startup");});
        Check((await proxy.ReadAsync(CancellationToken.None)).Known && probes==2);
        var malformed=false;try{QuickSettingsRegistration.Required<bool>(new JsonObject(),"value");}catch(JsonException){malformed=true;}Check(malformed);
        malformed=false;try{QuickSettingsRegistration.Presentation(new(true,true,new JsonObject(),""),true,"");}catch(JsonException){malformed=true;}Check(malformed);
        var accounts=new JsonArray();for(var i=0;i<33;i++)accounts.Add(new JsonObject{["id"]="account-"+i,["name"]="Account "+i,["enabled"]=true,["paused"]=false,["drain"]=false,["state"]="standby",["auth"]="ready"});
        var data=new JsonObject{["accounts"]=accounts,["activeAccountId"]="account-32",["usage"]=new JsonArray(),["accountAvailability"]=new JsonArray()};
        var projected=QuickSettingsRegistration.Presentation(new(true,true,data,""),true,"");
        Check(projected["tileLabel"]!.GetValue<string>()=="Account 32" && projected["rows"]!.AsArray().Count==32 && projected["rows"]!.AsArray().Any(x=>x!["id"]!.GetValue<string>()=="account-32" && x["selected"]!.GetValue<bool>()));
        Check(QuickSettingsRegistration.Presentation(new(false,true,null,""),true,"")["tileLabel"]!.GetValue<string>()=="代理状态暂不可确认");
        using var stopped=new CancellationTokenSource();var stage=0;var eventCount=0;var resultCount=0;JsonObject? frozen=null;JsonObject? receipt=null;
        JsonObject Reply(string type)=>new(){["protocol"]=2,["type"]=type,["brokerEpoch"]="recovery-broker"};
        Task<ProxySnapshot> Read(CancellationToken _)=>Task.FromResult(new ProxySnapshot(true,true,(JsonObject)data.DeepClone(),stage>=3 ? "Newer snapshot" : "Original snapshot"));
        Task<JsonObject> Exchange(JsonObject request,string _,CancellationToken __)
        {
            var type=request["type"]!.GetValue<string>();
            if(type=="framework.hello")return Task.FromResult(new JsonObject{["manifestVersion"]=2,["stateVersion"]=2});
            if(type=="plugin.register"){
                if(stage==0)stage=1;
                else{Check(stage==3 && JsonNode.DeepEquals(request["state"],frozen));stage=4;}
                return Task.FromResult(Reply("plugin.registered"));
            }
            Check(type=="plugin.poll");var response=Reply("plugin.poll.result");
            if(stage==1){
                stage=2;eventCount++;response["event"]=new JsonObject{["eventId"]="refresh-event",["id"]="codex-pool",["brokerEpoch"]="recovery-broker",["providerEpoch"]=request["providerEpoch"]!.DeepClone(),["expectedRevision"]=request["state"]!["revision"]!.DeepClone(),["kind"]="invoke",["componentId"]="refresh",["action"]="refresh"};
            }else if(stage==2){
                Check(request["result"] is not null && request["state"] is JsonObject);resultCount++;frozen=(JsonObject)request["state"]!.DeepClone();receipt=(JsonObject)request["result"]!.DeepClone();stage=3;
                throw new IOException("Synthetic result ACK loss after commit.");
            }else if(stage==4){
                Check(JsonNode.DeepEquals(request["state"],frozen) && JsonNode.DeepEquals(request["result"],receipt));resultCount++;stage=5;
            }else if(stage==5){
                Check(request["result"] is null && request["state"]!["revision"]!.GetValue<ulong>()>frozen!["revision"]!.GetValue<ulong>());
                Check(request["state"]!["tile"]!["summary"]!.GetValue<string>()=="Newer snapshot");stage=6;stopped.Cancel();
            }else throw new InvalidOperationException("Unexpected recovery frame.");
            return Task.FromResult(response);
        }
        Check(await QuickSettingsRegistration.RunAsync([broker,service],folder,Guid.NewGuid().ToString(),Read,Exchange,stopped.Token)==0);
        Check(stage==6 && eventCount==1 && resultCount==2);
        var attempts=0;Task<JsonObject> Offline(JsonObject _,string __,CancellationToken ___){attempts++;throw new IOException("Synthetic broker outage.");}
        Check(await QuickSettingsRegistration.RunAsync([broker,service],folder,Guid.NewGuid().ToString(),_=>Task.FromResult(new ProxySnapshot(true,false,null,"")),Offline,unavailableExitAfter:TimeSpan.FromMilliseconds(50))==0 && attempts<=3);
        var configuration=new JsonObject{["configVersion"]=1,["servicePath"]="//untrusted/share/CodexPool.Daemon.exe",["dataDirectory"]=folder};
        var rejected=false;try{QuickSettingsProviderEntry.Parse(configuration,Path.Combine(folder,"provider","CodexPool.Daemon.exe"));}catch(IOException){rejected=true;}Check(rejected);
    }
}
