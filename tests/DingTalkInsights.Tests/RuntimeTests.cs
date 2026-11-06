using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;

internal static class RuntimeTests
{
    internal static readonly DateTimeOffset Now=DateTimeOffset.Parse("2034-03-02T10:00:00Z");
    static void Assert(bool condition,string message="assertion failed"){if(!condition)throw new Exception(message);}
    static void Run(Func<Task> action)=>action().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    static async Task Fault(Func<Task> action,RuntimeFailure expected)
    {
        try{await action();throw new Exception("expected runtime fault "+expected);}
        catch(RuntimeFault e){Assert(e.Category==expected,$"expected {expected}, got {e.Category}");Assert(!e.Message.Contains("SYNTHETIC_SECRET"));}
    }
    static async Task<bool> Start(InsightController controller,MonitoringRequest request,bool confirmed)
    {
        if(!confirmed)return await controller.StartMonitoringAsync(null!,false);
        return await controller.StartMonitoringAsync(controller.PreviewMonitoring(request),true);
    }
    static async Task<bool> Analyze(InsightController controller,ModelSettings model,bool confirmed)
    {
        if(!confirmed)return await controller.AnalyzeCandidatesAsync(null!,false);
        var preview=await controller.PreviewAnalysisAsync(model);return preview is not null&&await controller.AnalyzeCandidatesAsync(preview,true);
    }
    static async Task<bool> Send(InsightController controller,string name,bool retry=false)
    {
        var preview=await controller.PreviewSendAsync(name);return preview is not null&&await controller.SendReportAsync(preview,true,retry);
    }
    static Task Gate(Task task)=>task.WaitAsync(TimeSpan.FromSeconds(5));
    public static IEnumerable<(string,Action)> All()
    {
        yield return ("runtime initialization navigation and unconfirmed actions perform zero IO",()=>Run(async()=>
        {
            using var f=new Fixture();var notices=0;f.Cipher.Reads=0;f.Cipher.Writes=0;
            await f.Controller.InitializeAsync();using var view=f.Controller.Subscribe(_=>notices++);f.Controller.SetDraft("Synthetic draft");
            Assert(!await f.Controller.ConnectAsync("not-an-absolute-root",confirmed:false));
            Assert(!await f.Controller.SaveCredentialsAsync(new("SYNTHETIC_SECRET_KEY"),confirmed:false));
            Assert(!await Start(f.Controller,f.Request,confirmed:false));
            Assert(f.Services.Calls==0&&f.Cipher.Reads==0&&f.Cipher.Writes==0&&!Directory.Exists(f.PluginRoot),"enable/navigation authorized IO");
            Assert(f.Controller.Snapshot.Draft=="Synthetic draft"&&notices>=1&&f.Controller.Snapshot.Connection==RuntimeConnection.Demo);
        }));
        yield return ("runtime canonical connection revokes legacy recovery without reading credentials or dispatching",()=>Run(async()=>
        {
            using var f=new Fixture(seed:true);f.Cipher.Reads=0;
            Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,confirmed:true));
            Assert(f.Cipher.Reads==1&&f.Services.Calls==0,"connection read a vault or source");
            var snapshot=f.Controller.Snapshot;Assert(snapshot.History.Count==1&&snapshot.Origin?.Start==Now.AddHours(-2)&&snapshot.Requests==1);
            Assert(!JsonSerializer.Serialize(snapshot).Contains("SYNTHETIC_SECRET"));
            await f.Controller.DisconnectAsync();
            using var legacy=new EncryptedStateStore(Path.Combine(f.CanonicalRoot,"state.dpapi"),f.Cipher,new FixedTime(Now));var state=legacy.Load();
            Assert(state.Recovery is null&&state.UserStopped&&state.Messages.Count==1&&state.Coverage.Count==1&&state.Receipts.Count==1&&state.Attempted.Count==1);
            Assert(state.Requests==1&&state.ReservedUsd==0.01m&&state.InputTokens==7&&state.OutputTokens==9,"handover lost canonical budget/usage");
        }));
        yield return ("runtime legacy lease and duplicate plugin contexts fail closed until disconnect",()=>Run(async()=>
        {
            using var f=new Fixture();
            using(var legacy=new EncryptedStateStore(Path.Combine(f.CanonicalRoot,"state.dpapi"),f.Cipher,new FixedTime(Now)))
                await Fault(()=>f.Controller.ConnectAsync(f.CanonicalRoot,true),RuntimeFailure.Ownership);
            Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));
            await using var other=new InsightController(f.Temp.Child("other-plugin"),"second-host",f.Cipher,f.Services,f.Clock);
            await Fault(()=>other.ConnectAsync(f.CanonicalRoot,true),RuntimeFailure.Ownership);
            Assert(f.Services.Calls==0);await f.Controller.DisconnectAsync();Assert(await other.ConnectAsync(f.CanonicalRoot,true));
        }));
        yield return ("runtime corrupt missing and symlink canonical roots grant no authority",()=>Run(async()=>
        {
            using var f=new Fixture();var statePath=Path.Combine(f.CanonicalRoot,"state.dpapi");var original=File.ReadAllBytes(statePath);
            File.WriteAllText(statePath,"corrupt synthetic content");await Fault(()=>f.Controller.ConnectAsync(f.CanonicalRoot,true),RuntimeFailure.Storage);
            Assert(File.ReadAllText(statePath)=="corrupt synthetic content");
            File.WriteAllBytes(statePath,original);await Fault(()=>f.Controller.ConnectAsync(f.Temp.Child("missing"),true),RuntimeFailure.Connection);
            if(!OperatingSystem.IsWindows())
            {
                var alias=f.Temp.Child("alias");Directory.CreateSymbolicLink(alias,f.CanonicalRoot);
                try{await Fault(()=>f.Controller.ConnectAsync(alias,true),RuntimeFailure.Connection);}finally{Directory.Delete(alias);}
            }
            Assert(f.Services.Calls==0&&f.Controller.Snapshot.Connection!=RuntimeConnection.Canonical);
            using var released=new EncryptedStateStore(statePath,f.Cipher,new FixedTime(Now));released.Load();
        }));
        yield return ("runtime expired canonical content does not widen retention or start a source",()=>Run(async()=>
        {
            using var f=new Fixture(seed:true);f.Clock.Advance(TimeSpan.FromDays(31));
            Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));
            Assert(f.Controller.Snapshot.History.Count==0&&f.Controller.Snapshot.Candidates.Count==0);
            await Fault(()=>Start(f.Controller,f.Request,true),RuntimeFailure.Retention);
            Assert(f.Services.Reads==0&&f.Services.Models==0&&!f.Controller.Snapshot.IsMonitoring);
        }));
        yield return ("runtime read-only Start requires explicit connection and never obtains an analyzer",()=>Run(async()=>
        {
            using var f=new Fixture();await Fault(()=>Start(f.Controller,f.Request,true),RuntimeFailure.Connection);
            Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));Assert(await Start(f.Controller,f.Request,true));await f.Clock.WaitForDelay();
            Assert(f.Services.Reads==1&&f.Services.Models==0&&f.Controller.Snapshot.IsMonitoring);
            var completion=f.Controller.MonitoringCompletion;Assert(!completion.IsCompleted,"monitor observation returned a placeholder completion");
            await f.Controller.StopMonitoringAsync();Assert(completion.IsCompleted,"Stop did not retire the observed real supervisor task");
        }));
        yield return ("runtime credential reuse is separately confirmed revision-bound and never shown to views",()=>Run(async()=>
        {
            using var f=new Fixture();Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));var request=f.Request with{UploadToModel=true};
            await Fault(()=>Start(f.Controller,request,true),RuntimeFailure.Credentials);
            Assert(!await f.Controller.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model,confirmed:false));
            await Fault(()=>Start(f.Controller,request,true),RuntimeFailure.Credentials);
            Assert(await f.Controller.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model,true));
            Assert(await Start(f.Controller,request,true));await f.Clock.WaitForDelay();
            Assert(f.Services.Models==1&&f.Controller.Snapshot.Requests==1&&!JsonSerializer.Serialize(f.Controller.Snapshot).Contains("SYNTHETIC_SECRET"));
            var bound=f.Controller.Snapshot.Configuration!.CredentialRevision;
            Assert(bound!=Guard.Hash(Guard.Hash(request.Profile)+"\n"+f.Vault.Revision),"plugin reused standalone credential authority");
            await f.Controller.StopMonitoringAsync();f.Vault.Save(new("SYNTHETIC_SECRET_CHANGED"));
            await Fault(()=>Start(f.Controller,request,true),RuntimeFailure.Credentials);
            Assert(f.Services.Models==1);
        }));
        foreach(var lifetime in new[]{false,true})
        {
            var viaLifetime=lifetime;
            yield return ($"runtime {(viaLifetime?"Lifetime":"Stop")} retires outer pending configuration before Running or dispatch",()=>Run(async()=>
            {
                using var f=new Fixture();Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));f.Cipher.Statuses.Clear();
                var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Services.Inspect=(_,_,_)=>{entered.TrySetResult();return release.Task;};
                var starting=Start(f.Controller,f.Request,true);Task? stopping=null;
                try
                {
                    await Gate(entered.Task);
                    if(viaLifetime){f.Lifetime.Cancel();stopping=f.Controller.Deactivation;}else stopping=f.Controller.StopMonitoringAsync();
                    Assert(!stopping.IsCompleted,"retirement returned before pending configuration");
                    release.TrySetResult(Guard.Hash("synthetic-executable"));await Gate(stopping);Assert(!await starting);
                    Assert(f.Services.Reads==0&&f.Services.Models==0&&!f.Cipher.Statuses.Contains(MonitorStatus.Running),"old outer continuation saved/dispatched Running");
                    if(!viaLifetime)
                    {
                        f.Services.Inspect=null;Assert(await Start(f.Controller,f.Request,true));await f.Clock.WaitForDelay();Assert(f.Services.Reads==1);
                    }
                    else{Assert(!f.Controller.Snapshot.Active);await Fault(()=>Start(f.Controller,f.Request,true),RuntimeFailure.Inactive);}
                }
                finally{release.TrySetResult(Guard.Hash("synthetic-executable"));await starting;if(stopping is not null)await stopping;}
            }));
        }
        foreach(var lifetime in new[]{false,true})
        {
            var viaLifetime=lifetime;
            yield return ($"runtime {(viaLifetime?"Lifetime deactivation":"disconnect")} holds legacy lease until ignored fetch retires",()=>Run(async()=>
            {
                using var f=new Fixture();Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));
                var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Services.Read=async(scope,_)=>{entered.TrySetResult();await release.Task;return FakeServices.Batch(scope);};
                Task? retiring=null;
                try
                {
                    Assert(await Start(f.Controller,f.Request with{UploadToModel=false},true));await Gate(entered.Task);
                    if(viaLifetime){f.Lifetime.Cancel();retiring=f.Controller.Deactivation;}else retiring=f.Controller.DisconnectAsync();
                    Assert(!retiring.IsCompleted);
                    try{using var conflicting=new EncryptedStateStore(Path.Combine(f.CanonicalRoot,"state.dpapi"),f.Cipher);throw new Exception("released an active lease");}catch(SafetyException){}
                    release.TrySetResult();await Gate(retiring);
                    Assert(f.Controller.Snapshot.Candidates.Count==0&&f.Controller.Snapshot.History.Count==0&&f.Controller.Snapshot.Markdown.Length==0);
                    using var legacy=new EncryptedStateStore(Path.Combine(f.CanonicalRoot,"state.dpapi"),f.Cipher,new FixedTime(Now));var saved=legacy.Load();
                    Assert(saved.Messages.Count==0&&saved.Coverage.Count==0&&saved.Receipts.Count==0&&saved.Recovery is null&&saved.UserStopped,"retired fetch committed stale work");
                }
                finally{release.TrySetResult();if(retiring is not null)await retiring;}
            }));
        }
        yield return ("runtime page reattachment preserves draft results and one controller without dispatch",()=>Run(async()=>
        {
            using var f=new Fixture();var oldUpdates=0;var newUpdates=0;
            var old=f.Controller.Subscribe(_=>oldUpdates++);f.Controller.SetDraft("Synthetic task draft");Assert(await f.Controller.OpenDemoAsync());
            Assert(await f.Controller.ReadOnceAsync(f.DemoRequest,true));Assert(await Analyze(f.Controller,new(),true));
            var report=f.Controller.Snapshot.Markdown;Assert(report.Length>0);old.Dispose();var oldCount=oldUpdates;
            using var current=f.Controller.Subscribe(_=>newUpdates++);f.Controller.SetDraft("Edited synthetic task");
            Assert(f.Controller.Snapshot.Markdown==report&&oldUpdates==oldCount&&newUpdates>0&&f.Services.Calls==0&&!f.Controller.Snapshot.IsMonitoring);
            Assert(await f.Controller.SaveWorkspaceAsync());await f.Controller.DeactivateAsync();
            await using var restored=new InsightController(f.PluginRoot,"fresh-host",f.Cipher,f.Services,f.Clock);
            Assert(restored.Snapshot.Draft.Length==0);Assert(await restored.LoadWorkspaceAsync());Assert(restored.Snapshot.Draft=="Edited synthetic task");
            Assert(!File.ReadAllText(Path.Combine(f.PluginRoot,"workspace.dpapi")).Contains("Edited synthetic task"));
        }));
        yield return ("runtime manual read search analysis history and send share the canonical ledger",()=>Run(async()=>
        {
            using var f=new Fixture();Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));
            Assert(await f.Controller.ReadOnceAsync(f.Request,true));Assert(f.Controller.Snapshot.Candidates.Count==1);
            Assert(await f.Controller.SearchAsync(f.Request.Executable,new(f.Request.Profile,f.Request.ConversationId,f.Request.Start,Now),"Synthetic",false,true));
            await Fault(()=>Analyze(f.Controller,new(),true),RuntimeFailure.Credentials);
            Assert(await f.Controller.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model|CredentialUse.Webhook,true));
            Assert(await Analyze(f.Controller,new(),true));var history=f.Controller.Snapshot.History.Single();
            Assert(!await Analyze(f.Controller,new(),true),"unknown/sent model revision could replay");
            f.Services.SendStatus=DeliveryStatus.Unknown;Assert(await Send(f.Controller,"Synthetic room"));
            Assert(!await Send(f.Controller,"Renamed room")&&f.Services.Sends==1,"page send replayed");
            await f.Controller.DisconnectAsync();Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));
            Assert(await f.Controller.SelectHistoryAsync(history));Assert(await f.Controller.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Webhook,true));
            Assert(!await Send(f.Controller,"Synthetic room")&&f.Services.Sends==1,"reconnect lost durable unknown attempt");
            Assert(await Send(f.Controller,"Synthetic room",retry:true)&&f.Services.Sends==2);
        }));
        yield return ("runtime default demo never calls production services or a Webhook",()=>Run(async()=>
        {
            using var f=new Fixture();Assert(await f.Controller.OpenDemoAsync());Assert(await Start(f.Controller,f.DemoRequest with{UploadToModel=true},true));await f.Clock.WaitForDelay();
            Assert(f.Services.Calls==0&&f.Controller.Snapshot.Requests==0&&f.Controller.Snapshot.Markdown.Length>0);
            await f.Controller.StopMonitoringAsync();await Fault(()=>Send(f.Controller,"Synthetic room"),RuntimeFailure.Connection);
            Assert(f.Services.Sends==0&&!File.Exists(Path.Combine(f.PluginRoot,"demo","send-attempts.dpapi")));
        }));
        yield return ("runtime protected local credential entry does not grant canonical reuse or reveal saved values",()=>Run(async()=>
        {
            using var f=new Fixture();Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));
            Assert(await f.Controller.SaveCredentialsAsync(new("SYNTHETIC_SECRET_LOCAL"),true));
            await Fault(()=>Start(f.Controller,f.Request with{UploadToModel=true},true),RuntimeFailure.Credentials);
            Assert(await f.Controller.UseCredentialsAsync(CredentialLocation.Plugin,CredentialUse.Model,true));
            Assert(await Start(f.Controller,f.Request with{UploadToModel=true},true));await f.Clock.WaitForDelay();
            Assert(f.Services.Models==1&&!JsonSerializer.Serialize(f.Controller.Snapshot).Contains("SYNTHETIC_SECRET"));
            Assert(f.Vault.Read().OpenAiKey=="SYNTHETIC_SECRET_KEY","plugin-local entry changed standalone vault");
        }));
        yield return ("runtime manual failures expose safe categories and retention expires visible history",()=>Run(async()=>
        {
            using var f=new Fixture(seed:true);Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));Assert(await f.Controller.SelectHistoryAsync(f.Controller.Snapshot.History.Single()));
            f.Services.Read=(_,_)=>throw new InvalidOperationException("SYNTHETIC_SECRET_PROVIDER_BODY");
            await Fault(()=>f.Controller.ReadOnceAsync(f.Request,true),RuntimeFailure.Source);
            Assert(!JsonSerializer.Serialize(f.Controller.Snapshot).Contains("SYNTHETIC_SECRET_PROVIDER_BODY"));
            f.Clock.Advance(TimeSpan.FromDays(31));Assert(f.Controller.Snapshot.History.Count==0&&f.Controller.Snapshot.Markdown.Length==0);
        }));
        yield return ("runtime visible retention cannot resurrect history after UTC clock rollback",()=>Run(async()=>
        {
            using var f=new Fixture(seed:true);Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));Assert(await f.Controller.SelectHistoryAsync(f.Controller.Snapshot.History.Single()));
            f.Clock.Advance(TimeSpan.FromDays(31));Assert(f.Controller.Snapshot.History.Count==0&&f.Controller.Snapshot.Markdown.Length==0);
            f.Clock.Advance(TimeSpan.FromDays(-31));Assert(f.Controller.Snapshot.History.Count==0&&f.Controller.Snapshot.Markdown.Length==0,"expired UI content resurrected after clock rollback");
        }));
        yield return ("runtime ignored model completion retires before lease release and preserves unknown reservation",()=>Run(async()=>
        {
            using var f=new Fixture();Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));Assert(await f.Controller.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model,true));
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Services.Analyze=async(batch,task,_)=>{entered.SetResult();await release.Task;return new(new AnalysisReport(batch,[],[],task),2,3);};
            Task? retiring=null;
            try
            {
                Assert(await Start(f.Controller,f.Request with{UploadToModel=true},true));await Gate(entered.Task);
                retiring=f.Controller.DisconnectAsync();Assert(!retiring.IsCompleted);release.TrySetResult();await Gate(retiring);
                using var old=new EncryptedStateStore(Path.Combine(f.CanonicalRoot,"state.dpapi"),f.Cipher,new FixedTime(Now));var saved=old.Load();
                Assert(saved.Requests==1&&saved.ReservedUsd>0&&saved.Attempted.Count==1&&saved.Reports.Count==0,"cancelled model lost durable attempt or committed stale report");
            }
            finally{release.TrySetResult();if(retiring is not null)await retiring;}
        }));
        yield return ("runtime concurrent page sends and ignored send completion preserve Pending through deactivation",()=>Run(async()=>
        {
            using var f=new Fixture();Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));Assert(await f.Controller.ReadOnceAsync(f.Request,true));
            Assert(await f.Controller.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model|CredentialUse.Webhook,true));Assert(await Analyze(f.Controller,new(),true));
            var history=f.Controller.Snapshot.History.Single();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Services.Send=async(_,_,_)=>{entered.SetResult();await release.Task;return DeliveryStatus.Sent;};
            Task? retiring=null;var sending=Send(f.Controller,"Synthetic room");
            try
            {
                await Gate(entered.Task);Assert(!await Send(f.Controller,"Synthetic room")&&f.Services.Sends==1);
                retiring=f.Controller.DeactivateAsync();Assert(!retiring.IsCompleted);
                try{using var conflict=new EncryptedStateStore(Path.Combine(f.CanonicalRoot,"state.dpapi"),f.Cipher);throw new Exception("lease released before send retirement");}catch(SafetyException){}
                release.TrySetResult();await Gate(retiring);Assert(!await sending);
                await using var restored=new InsightController(f.Temp.Child("restored-plugin"),"fresh-host",f.Cipher,f.Services,f.Clock);
                Assert(await restored.ConnectAsync(f.CanonicalRoot,true));Assert(await restored.SelectHistoryAsync(history));Assert(await restored.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Webhook,true));
                Assert(!await Send(restored,"Synthetic room")&&f.Services.Sends==1,"interrupted Pending was replayed");
            }
            finally{release.TrySetResult();await sending;if(retiring is not null)await retiring;}
        }));
        yield return ("runtime local monitoring preview exposes abandoned gap without source IO",()=>Run(async()=>
        {
            using var f=new Fixture(seed:true);Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));var before=f.Services.Calls;var reads=f.Cipher.Reads;
            var preview=f.Controller.PreviewMonitoring(f.Request with{Start=Now.AddMinutes(-30)});
            Assert(preview.NextRead.Start==Now.AddMinutes(-30)&&preview.NextRead.End==Now&&preview.PreviousCoveredThrough==Now.AddHours(-1));
            Assert(preview.AbandonedGap==new CoveredRange(Now.AddHours(-1),Now.AddMinutes(-30))&&preview.WithinRetention);
            Assert(f.Services.Calls==before&&f.Cipher.Reads==reads,"local preview accessed a source/vault");
        }));
        yield return ("runtime send approval binds exact payload owner generation and expiry",()=>Run(async()=>
        {
            using var f=new Fixture(seed:true);Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));
            Assert(await f.Controller.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Webhook,true));var saved=f.Controller.Snapshot.History.Single();
            Assert(await f.Controller.SelectHistoryAsync(saved));var preview=await f.Controller.PreviewSendAsync("Synthetic room");Assert(preview is not null);
            Assert(preview!.Markdown=="Synthetic prior report"&&preview.DestinationFingerprint.Length==12&&!JsonSerializer.Serialize(preview).Contains("SYNTHETIC_SECRET"));
            await f.Controller.StopMonitoringAsync();await Fault(()=>f.Controller.SendReportAsync(preview,true),RuntimeFailure.Configuration);Assert(f.Services.Sends==0);
            var expired=await f.Controller.PreviewSendAsync("Synthetic room");f.Clock.Advance(TimeSpan.FromMinutes(6));
            await Fault(()=>f.Controller.SendReportAsync(expired!,true),RuntimeFailure.Configuration);Assert(f.Services.Sends==0);
            var changed=await f.Controller.PreviewSendAsync("Synthetic room");Assert(await f.Controller.ClearIndexAsync(true));
            await Fault(()=>f.Controller.SendReportAsync(changed!,true),RuntimeFailure.Configuration);Assert(f.Services.Sends==0);
        }));
        yield return ("runtime analysis approval binds exact candidates task model credentials and owner",()=>Run(async()=>
        {
            using var f=new Fixture();Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));Assert(await f.Controller.ReadOnceAsync(f.Request,true));
            Assert(await f.Controller.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model,true));
            var edited=await f.Controller.PreviewAnalysisAsync(new());Assert(edited is not null);f.Controller.SetDraft("A different synthetic task");
            await Fault(()=>f.Controller.AnalyzeCandidatesAsync(edited!,true),RuntimeFailure.Configuration);
            var stopped=await f.Controller.PreviewAnalysisAsync(new());await f.Controller.StopMonitoringAsync();
            await Fault(()=>f.Controller.AnalyzeCandidatesAsync(stopped!,true),RuntimeFailure.Configuration);
            var changedCredential=await f.Controller.PreviewAnalysisAsync(new());Assert(await f.Controller.SaveCredentialsAsync(new("SYNTHETIC_SECRET_LOCAL"),true));
            await Fault(()=>f.Controller.AnalyzeCandidatesAsync(changedCredential!,true),RuntimeFailure.Configuration);
            Assert(f.Services.Models==0&&f.Controller.Snapshot.Requests==0,"stale analysis approval reached a model/reservation");
        }));
        yield return ("runtime failed final report save cannot publish an undurable report or usage",()=>Run(async()=>
        {
            using var f=new Fixture();Assert(await f.Controller.ConnectAsync(f.CanonicalRoot,true));Assert(await f.Controller.ReadOnceAsync(f.Request,true));
            Assert(await f.Controller.UseCredentialsAsync(CredentialLocation.ConnectedStore,CredentialUse.Model,true));
            f.Services.Analyze=(batch,task,_)=>{f.Cipher.FailProtect=true;return Task.FromResult(new ModelResult(new AnalysisReport(batch,[],[],task),2,3));};
            try
            {
                await Fault(()=>Analyze(f.Controller,new(),true),RuntimeFailure.Storage);
                Assert(f.Controller.Snapshot.History.Count==0&&f.Controller.Snapshot.Markdown.Length==0,"failed report save exposed an undurable history result");
            }
            finally{f.Cipher.FailProtect=false;}
            await f.Controller.DisconnectAsync();using var legacy=new EncryptedStateStore(Path.Combine(f.CanonicalRoot,"state.dpapi"),f.Cipher,new FixedTime(Now));var saved=legacy.Load();
            Assert(saved.Requests==1&&saved.Attempted.Count==1&&saved.Reports.Count==0&&saved.InputTokens==0&&saved.OutputTokens==0,"failed result was later resurrected by Stop");
        }));
        yield return ("runtime production services use synthetic DWS process through controller consent boundary",()=>Run(async()=>
        {
            using var f=new Fixture();using var dws=new SyntheticDws(messages:true);
            await using var production=new InsightController(f.Temp.Child("process-plugin"),"process-host",f.Cipher,new RuntimeServices(),f.Clock);
            await Fault(()=>production.ReadOnceAsync(f.Request with{Executable=dws.Executable},true),RuntimeFailure.Connection);Assert(dws.Reads==0);
            Assert(await production.ConnectAsync(f.CanonicalRoot,true));Assert(await production.ReadOnceAsync(f.Request with{Executable=dws.Executable},true));
            Assert(dws.Reads==1&&production.Snapshot.Candidates.Single().MessageId=="synthetic-message");
        }));
    }
    internal sealed class Fixture:IDisposable
    {
        public RuntimeTemp Temp{get;}=new();public SyntheticCipher Cipher{get;}=new();public FakeServices Services{get;}=new();public RuntimeClock Clock{get;}=new();public CancellationTokenSource Lifetime{get;}=new();
        public string CanonicalRoot=>Temp.Child("canonical");public string PluginRoot=>Temp.Child("plugin");public CredentialVault Vault{get;}
        public InsightController Controller{get;}
        public MonitoringRequest Request=>new("dws","corp:user","synthetic-chat",Now.AddHours(-2),Temp.Child("synthetic-dws"),false,new(),60,30);
        public MonitoringRequest DemoRequest=>new("demo","demo:local","demo-project",Now.AddHours(-3),"",false,new(),60,30);
        public Fixture(bool seed=false)
        {
            using(var legacy=new EncryptedStateStore(Path.Combine(CanonicalRoot,"state.dpapi"),Cipher,new FixedTime(Now)))
            {
                var state=legacy.Load();
                if(seed)
                {
                    var scope=new QueryScope("corp:user","synthetic-chat",Now.AddHours(-2),Now.AddHours(-1));var batch=FakeServices.Batch(scope);
                    state.Ingest(batch);state.MarkAttempted(scope,batch.Messages);state.Reserve(new(),0.01m,Now);state.InputTokens=7;state.OutputTokens=9;
                    state.Reports.Add(new(Now,scope.Key,"Synthetic prior task","Synthetic prior report",false,batch.Messages[0].CreatedAt));state.Monitoring=new(scope.Profile,scope.ConversationId,scope.Start);
                    var config=new MonitoringConfiguration("dws",scope.Profile,scope.ConversationId,scope.Start,Request.Executable,Guard.Hash("executable"),true,new(),60,30,Guard.Hash("old-credential"));
                    BackgroundPolicy.Approve(state,config,Now,true);state.Supervisor=state.Supervisor with{AiSuppressed=true,Failure=MonitorFailure.ModelUnknown};
                }
                legacy.Save(state);
            }
            Vault=new(Path.Combine(CanonicalRoot,"credentials.dpapi"),Cipher);Vault.Save(new("SYNTHETIC_SECRET_KEY","https://oapi.dingtalk.com/robot/send?access_token=synthetic-only","SYNTHETIC_SECRET_SIGN"));
            Controller=new(PluginRoot,"synthetic-workbench-owner",Cipher,Services,Clock,Lifetime.Token);
        }
        public void Dispose(){Controller.DeactivateAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();Lifetime.Dispose();Temp.Dispose();}
    }
    internal sealed class FakeServices:IRuntimeServices
    {
        public int Inspections,Reads,Models,Sends,Searches,Discoveries;public int Calls=>Inspections+Reads+Models+Sends+Searches+Discoveries;
        public Func<string,string,CancellationToken,Task<string>>? Inspect;
        public Func<QueryScope,CancellationToken,Task<MessageBatch>>? Read;
        public DeliveryStatus SendStatus=DeliveryStatus.Sent;
        public Func<MessageBatch,string,CancellationToken,Task<ModelResult>>? Analyze;
        public Func<WebhookTarget,string,CancellationToken,Task<DeliveryStatus>>? Send;
        public async Task<string> InspectSourceAsync(string executable,string profile,CancellationToken cancellation)
        {Inspections++;return Inspect is null?Guard.Hash("synthetic-executable"):await Inspect(executable,profile,cancellation);}
        public Task<MessageBatch> ReadAsync(string executable,QueryScope scope,CancellationToken cancellation)
        {Reads++;return Read?.Invoke(scope,cancellation)??Task.FromResult(Batch(scope));}
        public Task<MessageBatch> SearchAsync(string executable,QueryScope scope,string keywords,CancellationToken cancellation)
        {Searches++;return Task.FromResult(Batch(scope) with{Complete=false});}
        public Task<IReadOnlyList<Conversation>> DiscoverAsync(string executable,string profile,DateTimeOffset start,DateTimeOffset end,CancellationToken cancellation)
        {Discoveries++;return Task.FromResult<IReadOnlyList<Conversation>>([new("synthetic-chat","Synthetic room","group")]);}
        public Task<ModelResult> AnalyzeAsync(string key,MessageBatch batch,string task,ModelSettings model,CancellationToken cancellation)
        {
            Models++;Assert(key is "SYNTHETIC_SECRET_KEY" or "SYNTHETIC_SECRET_LOCAL","incorrect fake key selection");return Analyze?.Invoke(batch,task,cancellation)??Task.FromResult(new ModelResult(new AnalysisReport(batch,[],[],task),2,3));
        }
        public Task<DeliveryStatus> SendAsync(WebhookTarget target,string text,CancellationToken cancellation){Sends++;return Send?.Invoke(target,text,cancellation)??Task.FromResult(SendStatus);}
        public static MessageBatch Batch(QueryScope scope)=>new(scope,[new("synthetic-message",scope.ConversationId,"synthetic-user","Synthetic sender","Synthetic body",scope.Start+(scope.End-scope.Start)/2)],true,[],new Dictionary<string,JsonElement>());
    }
    internal sealed class RuntimeClock:IMonitorClock
    {
        readonly System.Threading.Channels.Channel<bool> delays=System.Threading.Channels.Channel.CreateUnbounded<bool>();
        public DateTimeOffset UtcNow{get;private set;}=Now;
        public void Advance(TimeSpan duration)=>UtcNow+=duration;
        public Task Delay(TimeSpan duration,CancellationToken cancellation)
        {delays.Writer.TryWrite(true);return Task.Delay(Timeout.InfiniteTimeSpan,cancellation);}
        public async Task WaitForDelay()=>await delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
