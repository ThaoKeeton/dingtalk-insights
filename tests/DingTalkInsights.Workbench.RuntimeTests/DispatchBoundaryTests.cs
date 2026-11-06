using System.Text.Json;
using DingTalkInsights.Core;
using DingTalkInsights.Runtime;

internal static class DispatchBoundaryTests
{
    static readonly Credentials Values=new("SYNTHETIC_SECRET_HOST","https://oapi.dingtalk.com/robot/send?access_token=synthetic-only","SYNTHETIC_SECRET_SIGNATURE");
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static async Task Attempt(Func<Task> action){try{await action();}catch(RuntimeFault){}}
    public static IEnumerable<(string,Func<Task>)> All()
    {
        foreach(var supervised in new[]{false,true})foreach(var readNumber in new[]{1,2})
        {
            var monitor=supervised;var advanceOn=readNumber;
            yield return ($"dispatch boundary UTC budget after credential wait {(monitor?"supervisor":"manual")} read {advanceOn}",async()=>
            {
                await using var f=new CredentialTests.CredentialFixture();f.Clock.Now=DateTimeOffset.Parse("2034-03-02T23:59:59Z");
                await f.Connect();await f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true);await f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true);
                var model=new ModelSettings{MaxRequestsPerDay=1};DateTimeOffset dispatched=default;var valueReads=0;
                f.Source.ModelHold=_=>{dispatched=f.Clock.UtcNow;return Task.CompletedTask;};
                if(!monitor)await f.Read();
                f.Store.BeforeRead=(id,_)=>{if(id=="openai.api-key"&&(!monitor||f.Source.Reads>0)&&++valueReads==advanceOn)f.Clock.Now+=TimeSpan.FromSeconds(2);return Task.CompletedTask;};
                if(monitor)
                {
                    await f.Controller.StartMonitoringAsync(f.Controller.PreviewMonitoring(f.Request with{UploadToModel=true,Model=model}),true);
                    await f.Clock.Wait();await f.Controller.StopMonitoringAsync();
                }
                else await f.Controller.AnalyzeCandidatesAsync((await f.Controller.PreviewAnalysisAsync(model))!,true);
                Check(f.Source.Models==1&&f.Controller.Snapshot.BudgetDay==dispatched.UtcDateTime.ToString("yyyy-MM-dd"),"model dispatched using yesterday's reservation after async credential I/O");
                f.Store.BeforeRead=null; // The fresh-owner check stays on that same observed dispatch day.
                var day=f.Controller.Snapshot.BudgetDay;await f.Controller.DeactivateAsync();
                using(var saved=new BusinessStore(f.Canonical,f.Cipher))Check(saved.Load() is {Requests:1} state&&state.BudgetDay==day,"dispatch-day reservation did not survive retirement");
                await using var next=new InsightController(f.Data,"synthetic-next-budget-owner",f.Cipher,f.Source,f.Clock,hostCredentials:f.Provider);
                await next.ConnectAsync(f.Canonical,true);await next.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true);
                f.Source.MessageText="Synthetic second revision";
                await next.ReadOnceAsync(next.PreviewMonitoring(f.Request),true);
                var second=await next.PreviewAnalysisAsync(model);Check(second is not null,"new synthetic revision missing");
                await Attempt(()=>next.AnalyzeCandidatesAsync(second!,true));Check(f.Source.Models==1&&next.Snapshot.Requests==1,"new owner bypassed the actual dispatch day's request limit");
            });
        }
        foreach(var sending in new[]{false,true})foreach(var readNumber in new[]{1,2})
        {
            var send=sending;var advanceOn=readNumber;
            yield return ($"dispatch boundary retention after credential wait {(send?"local send":"manual model")} read {advanceOn}",async()=>
            {
                await using var f=new CredentialTests.CredentialFixture();var at=f.Clock.UtcNow.AddDays(-30).AddSeconds(1);
                var scope=Seed(f,at,send);await f.Controller.ConnectAsync(f.Canonical,true);await f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true);
                await f.Controller.UseCredentialsAsync(CredentialLocation.Host,send?CredentialUse.Webhook:CredentialUse.Model,true);
                AnalysisPreview? analysis=null;SendPreview? delivery=null;
                if(send){await f.Controller.SelectHistoryAsync(f.Controller.Snapshot.History.Single());delivery=await f.Controller.PreviewSendAsync("Synthetic room");}
                else{await f.Controller.SearchAsync("",scope,"near-expiry",false,true);analysis=await f.Controller.PreviewAnalysisAsync(new());}
                var reads=0;DateTimeOffset? dispatched=null;
                f.Store.BeforeRead=(id,_)=>{if(id==(send?"dingtalk.webhook-url":"openai.api-key")&&++reads==advanceOn)f.Clock.Now+=TimeSpan.FromSeconds(2);return Task.CompletedTask;};
                f.Source.ModelHold=f.Source.SendHold=_=>{dispatched=f.Clock.UtcNow;return Task.CompletedTask;};
                await Attempt(()=>send?f.Controller.SendReportAsync(delivery!,true):f.Controller.AnalyzeCandidatesAsync(analysis!,true));
                Check(dispatched is null||dispatched<=at.AddDays(30),"expired source/report reached the transport after async credential I/O");
                if(f.Clock.UtcNow>at.AddDays(30))Check(f.Source.Models==0&&f.Source.Sends==0&&f.Controller.Snapshot.History.Count==0,"expired content was dispatched or retained visibly");
            });
        }
        yield return ("dispatch boundary supervisor refreshes retention after before-model credential preparation",async()=>
        {
            await using var f=new CredentialTests.CredentialFixture();await f.Connect();await f.Controller.SaveCredentialsAsync(CredentialLocation.Host,Values,true);await f.Controller.UseCredentialsAsync(CredentialLocation.Host,CredentialUse.Model,true);
            f.Source.MessageTime=f.Clock.UtcNow.AddDays(-30).AddSeconds(1);var advanced=false;
            f.Store.BeforeRead=(id,_)=>{if(id=="openai.api-key"&&f.Source.Reads>0&&!advanced){advanced=true;f.Clock.Now+=TimeSpan.FromSeconds(2);}return Task.CompletedTask;};
            var dispatched=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.Source.ModelHold=_=>{dispatched.TrySetResult();return Task.CompletedTask;};
            var request=f.Request with{Start=f.Clock.UtcNow.AddDays(-30),UploadToModel=true};
            await f.Controller.StartMonitoringAsync(f.Controller.PreviewMonitoring(request),true);
            try
            {
                await Task.WhenAny(f.Controller.MonitoringCompletion,dispatched.Task).WaitAsync(TimeSpan.FromSeconds(5));
                Check(advanced&&f.Source.Models==0,"supervisor uploaded sources that expired during before-model credentials");
            }
            finally{await f.Controller.StopMonitoringAsync();}
        });
    }
    static QueryScope Seed(CredentialTests.CredentialFixture f,DateTimeOffset at,bool report)
    {
        var scope=new QueryScope("fixture:user","fixture-room",at,at.AddSeconds(1));
        using var seed=new BusinessStore(f.Canonical,f.Cipher);var state=seed.Load();
        state.Ingest(new(scope,[new("old",scope.ConversationId,"fixture-user","Synthetic sender","Synthetic near-expiry evidence",at)],true,[],new Dictionary<string,JsonElement>()));
        if(report)state.Reports.Add(new(f.Clock.UtcNow,scope.Key,"Synthetic task","Synthetic near-expiry report",false,at));
        seed.Save(state);return scope;
    }
}
