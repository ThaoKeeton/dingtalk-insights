using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;
using DingTalkInsights.Workbench;
using Workbench.Sdk;

internal static class CredentialTests
{
    const string Key="SYNTHETIC_SECRET_HOST",Url="https://oapi.dingtalk.com/robot/send?access_token=synthetic-only";
    static readonly Credentials Values=new(Key,Url,"SYNTHETIC_SECRET_SIGNATURE");
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static async Task Denied(Func<Task> action)
    {
        try{await action();throw new Exception("missing credential denial");}
        catch(RuntimeFault e){Check(e.Category==RuntimeFailure.Credentials,"wrong safe category");Check(!e.Message.Contains("SYNTHETIC_SECRET"),"provider leaked a value");}
    }
    public static IEnumerable<(string,Func<Task>)> All()
    {
        yield return ("credentials initialization and unconfirmed actions never access host values",async()=>
        {
            await using var f=new CredentialFixture();await f.Controller.InitializeAsync();
            Check(f.Store.Calls.Count==0&&!Directory.Exists(f.Data),"initialization accessed host/business files");
            Check(!await f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,false),"unconfirmed save accepted");
            Check(!await f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,false),"unconfirmed approval accepted");
            Check(!await f.Controller.DeleteCredentialsAsync(CredentialLocation.Host,false)&&f.Store.Calls.Count==0,"unconfirmed delete accessed storage");
        });
        yield return ("credentials explicit host save needs separate purpose approval and reads only that purpose",async()=>
        {
            await using var f=new CredentialFixture();await f.Connect();
            Check(await f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true),"host save failed");
            await f.Read();await Denied(()=>f.Controller.PreviewAnalysisAsync(new()));
            await f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true);f.Store.Calls.Clear();
            var preview=await f.Controller.PreviewAnalysisAsync(new());Check(await f.Controller.AnalyzeCandidatesAsync(preview!,true),"host analysis failed");
            Check(f.Source.Models==1&&f.Source.LastKey==Key,"wrong provider used at dispatch");
            Check(!f.Store.Calls.Any(c=>c=="read:dingtalk.webhook-url"||c=="read:dingtalk.webhook-secret"),"model purpose read sender credentials");
            Check(!JsonSerializer.Serialize(f.Controller.Snapshot).Contains("SYNTHETIC_SECRET"),"snapshot leaked host values");
            await Denied(()=>f.Controller.PreviewSendAsync("Synthetic room"));Check(f.Source.Sends==0,"model approval granted send");
        });
        foreach(var marker in new[]{false,true})
        {
            var replaceBatch=marker;
            yield return ($"credentials {(marker?"batch revision":"single field")} change invalidates an open preview before model reservation",async()=>
            {
                await using var f=new CredentialFixture();await f.Connect();await f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true);
                await f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true);await f.Read();var preview=await f.Controller.PreviewAnalysisAsync(new());
                if(replaceBatch)await f.Provider.SaveAsync(Values with{OpenAiKey="SYNTHETIC_SECRET_CHANGED"});else f.Store.Values["openai.api-key"]="SYNTHETIC_SECRET_CHANGED";
                await Denied(()=>f.Controller.AnalyzeCandidatesAsync(preview!,true));
                Check(f.Source.Models==0&&f.Controller.Snapshot.Requests==0,"changed credentials consumed budget or dispatched");
            });
        }
        foreach(var failId in new[]{"openai.api-key","dingtalk.webhook-url","dingtalk.webhook-secret","credentials.commit"})
        {
            var id=failId;
            yield return ($"credentials interrupted batch at {id} refuses mixed values and never falls back",async()=>
            {
                await using var f=new CredentialFixture();await f.Provider.SaveAsync(Values);f.Store.FailWrite=id;
                await Denied(()=>f.Provider.SaveAsync(Values with{OpenAiKey="SYNTHETIC_SECRET_CHANGED"}));
                Check(!f.Store.Values.ContainsKey("credentials.commit"),"failed batch remained committed");
                await Denied(()=>f.Provider.GetRevisionAsync(CredentialUse.Model));
                f.Store.FailWrite=null;await f.Provider.SaveAsync(Values);var revision=await f.Provider.GetRevisionAsync(CredentialUse.Model);
                Check((await f.Provider.ReadAsync(CredentialUse.Model,revision)).OpenAiKey==Key,"explicit repair failed");
            });
        }
        yield return ("credentials marker change during an ignored value read refuses the stale snapshot",async()=>
        {
            await using var f=new CredentialFixture();await f.Provider.SaveAsync(Values);var revision=await f.Provider.GetRevisionAsync(CredentialUse.Model);
            f.Store.BeforeRead=(id,_)=>{if(id=="openai.api-key")f.Store.Values["credentials.commit"]="invalid changed commit";return Task.CompletedTask;};
            await Denied(()=>f.Provider.ReadAsync(CredentialUse.Model,revision));
        });
        yield return ("credentials malformed batch manifests never authorize a purpose",async()=>
        {
            await using var f=new CredentialFixture();
            foreach(var invalid in new[]{"", "null", "[]", "{\"Version\":2}",new string('x',2049)})
            {
                f.Store.Values["credentials.commit"]=invalid;
                await Denied(()=>f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true));
            }
            Check(f.Source.Models==0&&!f.Store.Calls.Any(c=>c=="read:openai.api-key"),"malformed manifest read a value");
        });
        yield return ("credentials partial host deletion leaves no reusable batch",async()=>
        {
            await using var f=new CredentialFixture();await f.Provider.SaveAsync(Values);f.Store.FailDelete="dingtalk.webhook-url";
            await Denied(()=>f.Provider.DeleteAsync());
            Check(!f.Store.Values.ContainsKey("credentials.commit"),"partial deletion left usable commit");
            await Denied(()=>f.Provider.GetRevisionAsync(CredentialUse.Webhook));
        });
        yield return ("credentials sender-only read excludes model key and empty replacement deletes old fields",async()=>
        {
            await using var f=new CredentialFixture();await f.Provider.SaveAsync(Values);f.Store.Calls.Clear();
            var revision=await f.Provider.GetRevisionAsync(CredentialUse.Webhook);var selected=await f.Provider.ReadAsync(CredentialUse.Webhook,revision);
            Check(selected.OpenAiKey==""&&selected.WebhookUrl==Url&&!f.Store.Calls.Contains("read:openai.api-key"),"sender purpose read model key");
            await f.Provider.SaveAsync(new(Key));
            Check(!f.Store.Values.ContainsKey("dingtalk.webhook-url")&&!f.Store.Values.ContainsKey("dingtalk.webhook-secret"),"empty replacement retained old fields");
            await Denied(()=>f.Provider.GetRevisionAsync(CredentialUse.Webhook));
        });
        yield return ("credentials failed replacement revokes the previously approved provider",async()=>
        {
            await using var f=new CredentialFixture();await f.Connect();await f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true);
            await f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true);await f.Read();
            f.Store.FailWrite="openai.api-key";await Denied(()=>f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true));
            await Denied(()=>f.Controller.PreviewAnalysisAsync(new()));Check(f.Source.Models==0,"failed replacement restored old purpose");
        });
        yield return ("credentials host revision changed during readiness cannot start collection",async()=>
        {
            await using var f=new CredentialFixture();await f.Connect();await f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true);
            await f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Source.InspectHold=async _=>{entered.TrySetResult();await release.Task;};
            var start=f.Controller.StartMonitoringAsync(f.Controller.PreviewMonitoring(f.Request with{UploadToModel=true}),true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try{await f.Provider.SaveAsync(Values);release.TrySetResult();await Denied(()=>start);Check(f.Source.Reads==0&&f.Source.Models==0&&!f.Controller.Snapshot.IsMonitoring,"stale readiness created a collector");}
            finally{release.TrySetResult();try{await start;}catch(RuntimeFault){}}
        });
        yield return ("credentials unsupported host storage cannot fall back to a valid plugin vault",async()=>
        {
            await using var f=new CredentialFixture();await f.Controller.SaveCredentialsAsync(new(Key),true);f.Store.Supported=false;
            await Denied(()=>f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true));
            await Denied(()=>f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true));
            Check(f.Store.Calls.Count==0,"unsupported host touched secret API");
        });
        yield return ("credentials deletion invalidates purpose and preserves other vaults and business ledger",async()=>
        {
            await using var f=new CredentialFixture();await f.Connect();await f.Controller.SaveCredentialsAsync(new("SYNTHETIC_SECRET_LOCAL"),true);
            await f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true);await f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true);
            await f.Read();var preview=await f.Controller.PreviewAnalysisAsync(new());
            Check(await f.Controller.DeleteCredentialsAsync(CredentialLocation.Host,true),"delete failed");
            Check(f.Store.Values.Count==0&&new CredentialVault(Path.Combine(f.Data,"credentials.dpapi"),f.Cipher).Read().OpenAiKey=="SYNTHETIC_SECRET_LOCAL","delete affected a different vault");
            Check(f.Controller.Observation.StoreOwned,"delete released canonical ledger");
            try{await f.Controller.AnalyzeCandidatesAsync(preview!,true);throw new Exception("deleted grant survived");}catch(RuntimeFault){}
            Check(f.Source.Models==0,"deleted credential dispatched");
        });
        foreach(var saving in new[]{false,true})
        {
            var save=saving;
            yield return ($"credentials terminal retirement joins ignored host {(save?"write":"revision read")} and retains canonical lease",async()=>
            {
                await using var f=new CredentialFixture();await f.Connect();await f.Provider.SaveAsync(Values);
                var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async Task Hold(string id,CancellationToken _){if(id==(save?"openai.api-key":"credentials.commit")){entered.TrySetResult();await release.Task;}}
                if(save)f.Store.BeforeWrite=Hold;else f.Store.BeforeRead=Hold;
                var operation=save?f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true):f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var retire=f.Controller.DeactivateAsync();
                try
                {
                    Check(!retire.IsCompleted&&f.Controller.Observation.PendingOperations==1,"retirement abandoned provider operation");
                    try{using var lease=new FileStream(Path.Combine(f.Canonical,"state.dpapi.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);throw new Exception("lease released before secret I/O retired");}catch(IOException){}
                    release.TrySetResult();Check(!await operation,"late provider result granted authority");await retire;
                    Check(!f.Controller.Observation.StoreOwned&&f.Source.Models==0,"late retirement retained live work");
                    if(save)Check(!f.Store.Values.ContainsKey("credentials.commit"),"cancelled write committed a batch");
                }
                finally{release.TrySetResult();await operation;await retire;}
            });
        }
        foreach(var sending in new[]{false,true})
        {
            var send=sending;
            yield return ($"credentials revoke during ignored {(send?"local send":"model")} retains attempts through a fresh owner",async()=>
            {
                await using var f=new CredentialFixture();await f.Connect();await f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true);
                await f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model|CredentialUse.Webhook,true);await f.Read();
                var analysis=await f.Controller.PreviewAnalysisAsync(new());
                var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async Task Hold(CancellationToken _){entered.TrySetResult();await release.Task;}
                Task<bool> operation;
                if(send)
                {
                    await f.Controller.AnalyzeCandidatesAsync(analysis!,true);f.Source.SendHold=Hold;
                    operation=f.Controller.SendReportAsync((await f.Controller.PreviewSendAsync("Synthetic room"))!,true);
                }
                else{f.Source.ModelHold=Hold;operation=f.Controller.AnalyzeCandidatesAsync(analysis!,true);}
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var revoke=f.Controller.RevokeCredentialsAsync();
                try
                {
                    Check(!revoke.IsCompleted&&f.Controller.Observation.StoreOwned,"revoke abandoned ignored transport or lease");
                    release.TrySetResult();Check(!await operation,"revoked operation published late success");await revoke;
                    Check(f.Controller.Snapshot.Requests==1&&f.Controller.Snapshot.ReservedUsd>0,"revoke released model reservation");
                    if(!send)Check(f.Controller.Snapshot.History.Count==0,"revoked model published a report");
                    await f.Controller.DeactivateAsync();
                    await using var next=new InsightController(f.Data,"synthetic-new-owner",f.Cipher,f.Source,f.Clock,hostCredentials:f.Provider);
                    await next.ConnectAsync(f.Canonical,true);
                    Check(f.Source.Models==1,"fresh owner repeated model work");
                    if(send)
                    {
                        await next.SelectHistoryAsync(next.Snapshot.History.Single());
                        await Denied(()=>next.PreviewSendAsync("Synthetic room"));
                        await next.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Webhook,true);
                        var retry=await next.PreviewSendAsync("Synthetic room");
                        Check(retry is {AlreadyAttempted:true,PreviousStatus:DeliveryStatus.Unknown,Attempts:1},"revoke discarded uncertain send");
                        Check(!await next.SendReportAsync(retry!,true)&&f.Source.Sends==1,"fresh owner resent uncertain local attempt");
                    }
                    else
                    {
                        await next.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true);
                        await next.ReadOnceAsync(next.PreviewMonitoring(f.Request),true);
                        Check(await next.PreviewAnalysisAsync(new()) is null&&f.Source.Models==1,"fresh owner reprocessed an uncertain model attempt");
                    }
                }
                finally{release.TrySetResult();await operation;await revoke;}
            });
        }
    }
    internal sealed class CredentialFixture:IAsyncDisposable
    {
        readonly string root=Path.Combine(Path.GetTempPath(),"DTI-credentials-"+Guid.NewGuid().ToString("N"));
        public string Canonical=>Path.Combine(root,"canonical");public string Data=>Path.Combine(root,"plugin");
        public SyntheticCipher Cipher{get;}=new();public SecretTransport Store{get;}=new();public CredentialSource Source{get;}=new();public BridgeClock Clock{get;}=new();
        public WorkbenchCredentialProvider Provider{get;}public InsightController Controller{get;}
        public MonitoringRequest Request=>new("dws","fixture:user","fixture-room",Clock.UtcNow.AddHours(-1),Path.Combine(root,"synthetic-dws"),false,new(),60,30);
        public CredentialFixture(){Provider=new(Store);Provider.Register();Controller=new(Data,"synthetic-credential-owner",Cipher,Source,Clock,hostCredentials:Provider);}
        public async Task Connect(){using(var seed=new BusinessStore(Canonical,Cipher))seed.Save(seed.Load());await Controller.ConnectAsync(Canonical,true);}
        public Task<bool> Read()=>Controller.ReadOnceAsync(Controller.PreviewMonitoring(Request),true);
        public async ValueTask DisposeAsync(){await Controller.DeactivateAsync();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    internal sealed class SecretTransport:IPluginSecretStore
    {
        public Dictionary<string,string> Values{get;}=[];public List<string> Calls{get;}=[];readonly List<PluginSecretDefinition> definitions=[];
        public bool Supported=true;public bool IsSupported=>Supported;public string? FailWrite,FailDelete;
        public Func<string,CancellationToken,Task>? BeforeRead,BeforeWrite;
        public void Register(PluginSecretDefinition definition)=>definitions.Add(definition);
        public IReadOnlyList<PluginSecretDefinition> GetDefinitions()=>definitions;
        public async Task<string?> ReadAsync(string id,CancellationToken cancellationToken=default){Calls.Add("read:"+id);if(BeforeRead is not null)await BeforeRead(id,cancellationToken);return Values.GetValueOrDefault(id);}
        public async Task WriteAsync(string id,string value,CancellationToken cancellationToken=default){Calls.Add("write:"+id);if(BeforeWrite is not null)await BeforeWrite(id,cancellationToken);if(FailWrite==id)throw new IOException("SYNTHETIC_SECRET_PROVIDER_BODY");Values[id]=value;}
        public Task DeleteAsync(string id,CancellationToken cancellationToken=default){Calls.Add("delete:"+id);if(FailDelete==id)throw new IOException("SYNTHETIC_SECRET_PROVIDER_BODY");Values.Remove(id);return Task.CompletedTask;}
    }
    internal sealed class CredentialSource:IRuntimeServices
    {
        public int Models,Sends,Reads;public string? LastKey;public Func<CancellationToken,Task>? ModelHold,SendHold,InspectHold;
        public string MessageText{get;set;}="Synthetic credential evidence";
        public DateTimeOffset? MessageTime{get;set;}
        public async Task<string> InspectSourceAsync(string executable,string profile,CancellationToken cancellation)
        {if(InspectHold is not null)await InspectHold(cancellation);return Guard.Hash("synthetic-dws");}
        public Task<MessageBatch> ReadAsync(string executable,QueryScope scope,CancellationToken cancellation)
        {Reads++;return Task.FromResult(new MessageBatch(scope,[new("credential-message",scope.ConversationId,"fixture-user","Synthetic sender",MessageText,MessageTime??scope.End.AddSeconds(-30))],true,[],new Dictionary<string,JsonElement>()));}
        public Task<MessageBatch> SearchAsync(string executable,QueryScope scope,string keywords,CancellationToken cancellation)=>throw new Exception("Unexpected search");
        public Task<IReadOnlyList<Conversation>> DiscoverAsync(string executable,string profile,DateTimeOffset start,DateTimeOffset end,CancellationToken cancellation)=>throw new Exception("Unexpected discovery");
        public async Task<ModelResult> AnalyzeAsync(string key,MessageBatch batch,string task,ModelSettings model,CancellationToken cancellation)
        {Models++;LastKey=key;if(ModelHold is not null)await ModelHold(cancellation);return new(new AnalysisReport(batch,[],[],task),2,3);}
        public async Task<DeliveryStatus> SendAsync(WebhookTarget target,string text,CancellationToken cancellation)
        {Sends++;if(SendHold is not null)await SendHold(cancellation);return DeliveryStatus.Sent;}
    }
}
